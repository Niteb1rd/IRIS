using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Pipeline;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vd12 = Vortice.Direct3D12;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

internal sealed class CopyQueueReadback : IDisposable
{
    private const uint SharedResourceRead = 0x80000000;
    private const uint SharedResourceWrite = 1;
    private const int PitchAlignment = 256;
    private const int PlacementAlignment = 512;
    private const int BufferStep = 1 << 20;
    private const int OpenedMax = 32;
    private const double StatsEveryMs = 60_000.0;
    private const int DisposeWaitMs = 500;

    public static bool Abandoned { get; private set; }

    private static readonly List<object> s_abandoned = new();

    private static readonly Guid DxgiResource1Iid = new("30961379-4609-4a41-998e-54fe567ee0c1");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSharedHandleFn(IntPtr self, IntPtr attributes, uint access, IntPtr name, out IntPtr handle);

    private sealed class Source
    {
        public IDisposable? Owner;
        public IDisposable? Texture;
        public Vd12.ID3D12Resource? Owned;
        public int Refs;
    }

    private sealed class Batch
    {
        public readonly List<Rect> Regions = new();
        public readonly List<ulong> Offsets = new();
        public readonly List<int> Pitches = new();
        public Vd12.ID3D12CommandAllocator? Allocator;
        public Vd12.ID3D12GraphicsCommandList? List;
        public Vd12.ID3D12Resource? Buffer;
        public long BufferBytes;
        public Source? Source;
        public long Ticket;
        public ulong SyncValue;
        public ulong DoneValue;
        public bool Abandoned;
        public double IssuedMs;
        public double SyncSeenMs;
        public bool Timed;
    }

    private readonly Vd.ID3D11DeviceContext4 _context4;
    private readonly Vd.ID3D11DeviceContext _context;
    private readonly Vd.ID3D11Texture2D _touch;
    private readonly Vd.ID3D11Fence _sync11;
    private readonly Vd12.ID3D12Device _device;
    private readonly Vd12.ID3D12CommandQueue _queue;
    private readonly Vd12.ID3D12Fence _sync12;
    private readonly Vd12.ID3D12Fence _done;
    private readonly AutoResetEvent _event = new(false);
    private readonly Batch[] _patches;
    private readonly Batch[] _scans;
    private readonly Dictionary<IntPtr, (Vd12.ID3D12Resource Resource, uint Width, uint Height)> _opened = new();
    private readonly List<Vd12.ID3D12Resource> _retired = new();
    private readonly Action<string>? _log;
    private readonly bool _logStats;
    private readonly List<double> _syncTimes = new();
    private readonly List<double> _copyTimes = new();
    private double _statsStartMs = double.NaN;
    private ulong _syncValue;
    private ulong _doneValue;
    private byte[] _rows = Array.Empty<byte>();
    private bool _disposed;

    public CopyQueueReadback(Vd.ID3D11Device device11, Vd.ID3D11DeviceContext context, int patchBatches, int scanBatches, Action<string>? log, bool logStats)
    {
        _context = context;
        _log = log;
        _logStats = logStats;
        var made = new List<IDisposable>();
        try
        {
            _context4 = context.QueryInterface<Vd.ID3D11DeviceContext4>();
            made.Add(_context4);
            using var device5 = device11.QueryInterface<Vd.ID3D11Device5>();
            using (var dxgi = device11.QueryInterface<Vx.IDXGIDevice>())
            using (var adapter = dxgi.GetAdapter())
            {
                var result = Vd12.D3D12.D3D12CreateDevice(adapter, Vc.FeatureLevel.Level_11_0, out Vd12.ID3D12Device? device12);
                if (result.Failure || device12 is null)
                    throw new InvalidOperationException($"a Direct3D 12 device could not be created (HRESULT 0x{result.Code:X8})");
                _device = device12;
                made.Add(device12);
            }

            _queue = _device.CreateCommandQueue(Vd12.CommandListType.Copy, Vd12.CommandQueuePriority.Normal);
            made.Add(_queue);
            _done = _device.CreateFence(0);
            made.Add(_done);
            _sync12 = _device.CreateFence(0, Vd12.FenceFlags.Shared);
            made.Add(_sync12);
            var handle = _device.CreateSharedHandle(_sync12, null, null!);
            try
            {
                _sync11 = device5.OpenSharedFence(handle);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }

            made.Add(_sync11);
            _touch = device11.CreateTexture2D(new Vd.Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vx.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vx.SampleDescription(1, 0),
                Usage = Vd.ResourceUsage.Default,
                BindFlags = Vd.BindFlags.None,
                CPUAccessFlags = Vd.CpuAccessFlags.None,
                MiscFlags = Vd.ResourceOptionFlags.None,
            });
            made.Add(_touch);
            _patches = NewBatches(patchBatches, made);
            _scans = NewBatches(scanBatches, made);
        }
        catch
        {
            for (var i = made.Count - 1; i >= 0; i--)
                TryDispose(made[i]);
            _event.Dispose();
            throw;
        }
    }

    public string MethodText => "a Direct3D 12 copy queue";

    public WaitHandle CompletionHandle => _event;

    public bool CanTakeFrame => FreeBatch(_patches) is not null;

    public bool CanTakeScan => FreeBatch(_scans) is not null;

    public (ReadbackTicket Patches, ReadbackTicket Scan) Issue(Vd.ID3D11Texture2D texture, IDisposable owner, IReadOnlyList<Rect> patches, Rect? scan)
    {
        var source = new Source { Owner = owner, Texture = texture };
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var patchBatch = FreeBatch(_patches) ?? throw new InvalidOperationException("No readback batch is free for the patches.");
            var scanBatch = scan is null ? null : FreeBatch(_scans) ?? throw new InvalidOperationException("No readback batch is free for the scan region.");
            var (resource, owned) = Open(texture, texture.Description);
            source.Owned = owned;

            _context.CopySubresourceRegion(_touch, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, 1, 1, 1));
            var syncValue = ++_syncValue;
            _context4.Signal(_sync11, syncValue);
            _context.Flush();
            _queue.Wait(_sync12, syncValue).CheckError();
            _sync12.SetEventOnCompletion(syncValue, _event).CheckError();

            var now = StopwatchClock.Shared.NowMs;
            var patchTicket = Fill(patchBatch, resource, patches, source, syncValue, now);
            var scanTicket = default(ReadbackTicket);
            if (scanBatch is not null)
                scanTicket = Fill(scanBatch, resource, new[] { scan!.Value }, source, syncValue, now);
            return (patchTicket, scanTicket);
        }
        catch
        {
            if (source.Refs == 0)
            {
                TryDispose(source.Texture);
                TryDispose(source.Owner);
                TryDispose(source.Owned);
            }

            throw;
        }
    }

    public ReadbackState Poll(ReadbackTicket ticket)
    {
        if (_disposed || ticket.IsNone || Find(ticket) is not { } batch)
            return ReadbackState.Lost;
        Sweep();
        return IsComplete(batch) ? ReadbackState.Done : ReadbackState.Pending;
    }

    public ReadbackState Read(ReadbackTicket ticket, IReadOnlyList<Mat> destinations, bool raw)
    {
        if (_disposed || ticket.IsNone || Find(ticket) is not { } batch)
            return ReadbackState.Lost;
        Sweep();
        if (!IsComplete(batch))
            return ReadbackState.Pending;
        try
        {
            if (destinations.Count != batch.Regions.Count)
                throw new ArgumentException("One destination for each region is needed.", nameof(destinations));
            var mapped = batch.Buffer!.Map<byte>(0, (int)batch.BufferBytes);
            try
            {
                for (var i = 0; i < batch.Regions.Count; i++)
                {
                    var region = batch.Regions[i];
                    var pitch = batch.Pitches[i];
                    var bytes = pitch * region.Height;
                    if (_rows.Length < bytes)
                        _rows = GC.AllocateArray<byte>(bytes, pinned: true);
                    mapped.Slice((int)batch.Offsets[i], bytes).CopyTo(_rows);
                    using var view = Mat.FromPixelData(region.Height, region.Width, MatType.CV_8UC4, Marshal.UnsafeAddrOfPinnedArrayElement(_rows, 0), pitch);
                    if (raw)
                        view.CopyTo(destinations[i]);
                    else
                        Cv2.CvtColor(view, destinations[i], ColorConversionCodes.BGRA2BGR);
                }
            }
            finally
            {
                batch.Buffer.Unmap(0);
            }

            return ReadbackState.Done;
        }
        finally
        {
            Free(batch);
        }
    }

    public void Abandon(ReadbackTicket ticket)
    {
        if (!_disposed && !ticket.IsNone && Find(ticket) is { } batch)
            batch.Abandoned = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var idle = true;
        try
        {
            var last = ++_doneValue;
            _queue.Signal(_done, last).CheckError();
            if (_done.CompletedValue < last)
            {
                var wait = new AutoResetEvent(false);
                _done.SetEventOnCompletion(last, wait).CheckError();
                idle = _done.CompletedValue >= last || wait.WaitOne(DisposeWaitMs);
                if (idle)
                    wait.Dispose();
                else
                    s_abandoned.Add(wait);
            }
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            idle = true;
        }

        if (!idle)
        {
            _log?.Invoke("Window capture: the Direct3D 12 copy queue did not finish within 0.5 s; its objects are kept until IRIS ends");
            s_abandoned.Add(this);
            Abandoned = true;
            return;
        }

        foreach (var batch in _patches)
            DisposeBatch(batch);
        foreach (var batch in _scans)
            DisposeBatch(batch);
        foreach (var (key, entry) in _opened)
        {
            TryDispose(entry.Resource);
            Marshal.Release(key);
        }

        _opened.Clear();
        foreach (var resource in _retired)
            TryDispose(resource);
        _retired.Clear();
        TryDispose(_touch);
        TryDispose(_sync11);
        TryDispose(_sync12);
        TryDispose(_done);
        TryDispose(_queue);
        TryDispose(_device);
        TryDispose(_context4);
        _event.Dispose();
    }

    private Batch[] NewBatches(int count, List<IDisposable> made)
    {
        var batches = new Batch[Math.Max(1, count)];
        for (var i = 0; i < batches.Length; i++)
        {
            var allocator = _device.CreateCommandAllocator(Vd12.CommandListType.Copy);
            made.Add(allocator);
            var list = _device.CreateCommandList<Vd12.ID3D12GraphicsCommandList>(Vd12.CommandListType.Copy, allocator);
            made.Add(list);
            list.Close();
            batches[i] = new Batch { Allocator = allocator, List = list };
        }

        return batches;
    }

    private (Vd12.ID3D12Resource Resource, Vd12.ID3D12Resource? Owned) Open(Vd.ID3D11Texture2D texture, Vd.Texture2DDescription description)
    {
        var key = texture.NativePointer;
        if (_opened.TryGetValue(key, out var entry) && entry.Width == description.Width && entry.Height == description.Height)
            return (entry.Resource, null);
        var resource = OpenShared(texture);
        if (_opened.Remove(key, out var old))
        {
            _retired.Add(old.Resource);
            Marshal.Release(key);
        }

        if (_opened.Count >= OpenedMax)
            return (resource, resource);
        Marshal.AddRef(key);
        _opened[key] = (resource, description.Width, description.Height);
        return (resource, null);
    }

    private Vd12.ID3D12Resource OpenShared(Vd.ID3D11Texture2D texture)
    {
        var iid = DxgiResource1Iid;
        var queried = Marshal.QueryInterface(texture.NativePointer, in iid, out var resource1);
        if (queried < 0)
            throw new COMException("The captured picture has no IDXGIResource1.", queried);
        IntPtr handle;
        try
        {
            var vtable = Marshal.ReadIntPtr(resource1);
            var create = Marshal.GetDelegateForFunctionPointer<CreateSharedHandleFn>(Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size));
            var hr = create(resource1, IntPtr.Zero, SharedResourceRead | SharedResourceWrite, IntPtr.Zero, out handle);
            if (hr < 0)
                hr = create(resource1, IntPtr.Zero, SharedResourceRead, IntPtr.Zero, out handle);
            if (hr < 0)
                throw new COMException("No NT handle could be made for the captured picture.", hr);
        }
        finally
        {
            Marshal.Release(resource1);
        }

        try
        {
            return _device.OpenSharedHandle<Vd12.ID3D12Resource>(handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private ReadbackTicket Fill(Batch batch, Vd12.ID3D12Resource resource, IReadOnlyList<Rect> regions, Source source, ulong syncValue, double nowMs)
    {
        batch.Regions.Clear();
        batch.Offsets.Clear();
        batch.Pitches.Clear();
        long total = 0;
        foreach (var region in regions)
        {
            var pitch = Align(region.Width * 4, PitchAlignment);
            total = Align(total, PlacementAlignment);
            batch.Regions.Add(region);
            batch.Offsets.Add((ulong)total);
            batch.Pitches.Add(pitch);
            total += (long)pitch * region.Height;
        }

        total = Math.Max(total, PlacementAlignment);
        if (batch.Buffer is null || batch.BufferBytes < total)
        {
            TryDispose(batch.Buffer);
            batch.Buffer = null;
            var bytes = Align(total, BufferStep);
            batch.Buffer = _device.CreateCommittedResource(Vd12.HeapType.Readback, Vd12.ResourceDescription.Buffer((ulong)bytes), Vd12.ResourceStates.CopyDest);
            batch.BufferBytes = bytes;
        }

        batch.Allocator!.Reset();
        var list = batch.List!;
        list.Reset(batch.Allocator);
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var footprint = new Vd12.PlacedSubresourceFootPrint
            {
                Offset = batch.Offsets[i],
                Footprint = new Vd12.SubresourceFootPrint(Vx.Format.B8G8R8A8_UNorm, (uint)region.Width, (uint)region.Height, 1, (uint)batch.Pitches[i]),
            };
            list.CopyTextureRegion(new Vd12.TextureCopyLocation(batch.Buffer, footprint), 0, 0, 0, new Vd12.TextureCopyLocation(resource, 0),
                new Vortice.Mathematics.Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1));
        }

        list.Close();
        var doneValue = ++_doneValue;
        source.Refs++;
        batch.Source = source;
        batch.SyncValue = syncValue;
        batch.DoneValue = doneValue;
        _queue.ExecuteCommandList(list);
        _queue.Signal(_done, doneValue).CheckError();
        _done.SetEventOnCompletion(doneValue, _event).CheckError();

        batch.Abandoned = false;
        batch.IssuedMs = nowMs;
        batch.SyncSeenMs = double.NaN;
        batch.Timed = false;
        batch.Ticket = RegionReadback.NewTicket();
        return new ReadbackTicket(batch.Ticket);
    }

    private static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private bool IsComplete(Batch batch) => _done.CompletedValue >= batch.DoneValue;

    private void Sweep()
    {
        var now = StopwatchClock.Shared.NowMs;
        var synced = _sync12.CompletedValue;
        var done = _done.CompletedValue;
        SweepRing(_patches, now, synced, done);
        SweepRing(_scans, now, synced, done);
        if (!_logStats)
            return;
        if (double.IsNaN(_statsStartMs))
            _statsStartMs = now;
        else if (now - _statsStartMs >= StatsEveryMs)
            LogStats(now);
    }

    private void SweepRing(Batch[] ring, double now, ulong synced, ulong done)
    {
        foreach (var batch in ring)
        {
            if (batch.Ticket == 0)
                continue;
            if (double.IsNaN(batch.SyncSeenMs) && synced >= batch.SyncValue)
                batch.SyncSeenMs = now;
            if (done < batch.DoneValue)
                continue;
            if (_logStats && !batch.Timed)
            {
                batch.Timed = true;
                var syncMs = double.IsNaN(batch.SyncSeenMs) ? now - batch.IssuedMs : batch.SyncSeenMs - batch.IssuedMs;
                _syncTimes.Add(syncMs);
                _copyTimes.Add(now - batch.IssuedMs - syncMs);
            }

            ReleaseSource(batch);
            if (batch.Abandoned)
                Free(batch);
        }
    }

    private void LogStats(double now)
    {
        if (_syncTimes.Count > 0)
            _log?.Invoke($"Window capture copy queue: {_syncTimes.Count} batches in {(now - _statsStartMs) / 1000.0:0} s; D3D11 mark passed after avg {Average(_syncTimes):0.0} / p95 {P95(_syncTimes):0.0} ms; copies done after that avg {Average(_copyTimes):0.0} / p95 {P95(_copyTimes):0.0} ms");
        _syncTimes.Clear();
        _copyTimes.Clear();
        _statsStartMs = now;
    }

    private static double Average(List<double> values)
    {
        var sum = 0.0;
        foreach (var value in values)
            sum += value;
        return sum / values.Count;
    }

    private static double P95(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
    }

    private Batch? FreeBatch(Batch[] ring)
    {
        if (_disposed)
            return null;
        Sweep();
        foreach (var batch in ring)
            if (batch.Ticket == 0)
                return batch;
        return null;
    }

    private Batch? Find(ReadbackTicket ticket)
    {
        foreach (var batch in _patches)
            if (batch.Ticket == ticket.Id && !batch.Abandoned)
                return batch;
        foreach (var batch in _scans)
            if (batch.Ticket == ticket.Id && !batch.Abandoned)
                return batch;
        return null;
    }

    private void ReleaseSource(Batch batch)
    {
        var source = batch.Source;
        batch.Source = null;
        if (source is null || --source.Refs > 0)
            return;
        TryDispose(source.Texture);
        TryDispose(source.Owner);
        TryDispose(source.Owned);
        source.Texture = null;
        source.Owner = null;
        source.Owned = null;
    }

    private void Free(Batch batch)
    {
        ReleaseSource(batch);
        batch.Ticket = 0;
        batch.Abandoned = false;
        batch.Regions.Clear();
        batch.Offsets.Clear();
        batch.Pitches.Clear();
    }

    private void DisposeBatch(Batch batch)
    {
        Free(batch);
        TryDispose(batch.Buffer);
        TryDispose(batch.List);
        TryDispose(batch.Allocator);
        batch.Buffer = null;
        batch.List = null;
        batch.Allocator = null;
    }

    private static void TryDispose(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}
