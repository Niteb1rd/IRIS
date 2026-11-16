using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;
using SharpGen.Runtime;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

public readonly record struct ReadbackTicket(long Id)
{
    public bool IsNone => Id == 0;
}

public enum ReadbackState
{
    Pending,
    Done,
    Lost,
}

internal sealed class RegionReadback : IDisposable
{
    public const int StagingBucket = 64;

    private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);

    private static long s_nextTicket;
    private static string? s_loggedMethod;

    private sealed class Slot
    {
        public Vd.ID3D11Texture2D? Staging;
        public int Width;
        public int Height;
    }

    private sealed class Batch
    {
        public readonly List<Slot> Slots = new();
        public readonly List<Slot> Used = new();
        public readonly List<Rect> Regions = new();
        public long Ticket;
        public ulong FenceValue;
        public Vd.ID3D11Query? Query;
        public bool Abandoned;
    }

    private readonly Vd.ID3D11Device _device;
    private readonly Vd.ID3D11DeviceContext _context;
    private readonly Vd.ID3D11DeviceContext4? _context4;
    private readonly Vd.ID3D11Fence? _fence;
    private readonly AutoResetEvent? _done;
    private readonly Batch[] _patches;
    private readonly Batch[] _scans;
    private ulong _fenceValue;
    private bool _disposed;

    public RegionReadback(Vd.ID3D11Device device, Vd.ID3D11DeviceContext context, int patchBatches, int scanBatches)
    {
        _device = device;
        _context = context;
        _patches = NewBatches(patchBatches);
        _scans = NewBatches(scanBatches);
        Vd.ID3D11DeviceContext4? context4 = null;
        Vd.ID3D11Fence? fence = null;
        try
        {
            context4 = context.QueryInterfaceOrNull<Vd.ID3D11DeviceContext4>();
            using var device5 = device.QueryInterfaceOrNull<Vd.ID3D11Device5>();
            if (context4 is not null && device5 is not null)
                fence = device5.CreateFence(0);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException or InvalidCastException)
        {
            fence = null;
        }

        if (fence is null)
        {
            context4?.Dispose();
            return;
        }

        _context4 = context4;
        _fence = fence;
        _done = new AutoResetEvent(false);
    }

    public static long NewTicket() => Interlocked.Increment(ref s_nextTicket);

    public static void ForgetLoggedMethod() => s_loggedMethod = null;

    public string MethodText => _fence is null ? "event queries (polled)" : "a GPU fence";

    public static void LogMethodOnce(Action<string>? log, string source, RegionReadback readback)
    {
        var text = source + ": region copies come back through " + readback.MethodText;
        if (log is null || text == s_loggedMethod)
            return;
        s_loggedMethod = text;
        log(text);
    }

    public WaitHandle? CompletionHandle => _done;

    public bool CanTakeFrame => FreeBatch(_patches) is not null;

    public bool CanTakeScan => FreeBatch(_scans) is not null;

    public int Busy
    {
        get
        {
            var busy = 0;
            foreach (var batch in _patches)
                busy += batch.Ticket != 0 ? 1 : 0;
            foreach (var batch in _scans)
                busy += batch.Ticket != 0 ? 1 : 0;
            return busy;
        }
    }

    public (ReadbackTicket Patches, ReadbackTicket Scan) Issue(Vd.ID3D11Texture2D texture, IReadOnlyList<Rect> patches, Rect? scan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var patchBatch = FreeBatch(_patches) ?? throw new InvalidOperationException("No readback batch is free for the patches.");
        var scanBatch = scan is null ? null : FreeBatch(_scans) ?? throw new InvalidOperationException("No readback batch is free for the scan region.");
        Fill(patchBatch, texture, patches);
        var patchTicket = Mark(patchBatch);
        var scanTicket = default(ReadbackTicket);
        if (scanBatch is not null)
        {
            Fill(scanBatch, texture, new[] { scan!.Value });
            scanTicket = Mark(scanBatch);
        }

        _context.Flush();
        return (patchTicket, scanTicket);
    }

    public ReadbackState Poll(ReadbackTicket ticket)
    {
        if (_disposed || ticket.IsNone || Find(ticket) is not { } batch)
            return ReadbackState.Lost;
        return IsComplete(batch) ? ReadbackState.Done : ReadbackState.Pending;
    }

    public ReadbackState Read(ReadbackTicket ticket, IReadOnlyList<Mat> destinations, bool raw)
    {
        if (_disposed || ticket.IsNone || Find(ticket) is not { } batch)
            return ReadbackState.Lost;
        if (!IsComplete(batch))
            return ReadbackState.Pending;
        var done = false;
        try
        {
            if (destinations.Count != batch.Regions.Count)
                throw new ArgumentException("One destination for each region is needed.", nameof(destinations));
            for (var i = 0; i < batch.Regions.Count; i++)
            {
                if (!ReadSlot(batch.Used[i], batch.Regions[i], destinations[i], raw))
                    return ReadbackState.Pending;
            }

            done = true;
            return ReadbackState.Done;
        }
        catch
        {
            done = true;
            throw;
        }
        finally
        {
            if (done)
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
        var pending = Busy > 0;
        _disposed = true;
        foreach (var batch in _patches)
            DisposeBatch(batch);
        foreach (var batch in _scans)
            DisposeBatch(batch);
        TryDispose(_fence);
        TryDispose(_context4);
        if (!pending)
            _done?.Dispose();
    }

    private static Batch[] NewBatches(int count)
    {
        var batches = new Batch[Math.Max(1, count)];
        for (var i = 0; i < batches.Length; i++)
            batches[i] = new Batch();
        return batches;
    }

    private Batch? FreeBatch(Batch[] ring)
    {
        if (_disposed)
            return null;
        Batch? free = null;
        foreach (var batch in ring)
        {
            if (batch.Ticket != 0 && batch.Abandoned && IsComplete(batch))
                Free(batch);
            if (batch.Ticket == 0)
                free ??= batch;
        }

        return free;
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

    private bool IsComplete(Batch batch)
    {
        if (_fence is not null)
            return _fence.CompletedValue >= batch.FenceValue;
        return batch.Query is not null && _context.IsDataAvailable(batch.Query, Vd.AsyncGetDataFlags.DoNotFlush);
    }

    private ReadbackTicket Mark(Batch batch)
    {
        batch.Ticket = NewTicket();
        batch.Abandoned = false;
        if (_fence is not null && _context4 is not null && _done is not null)
        {
            batch.FenceValue = ++_fenceValue;
            _context4.Signal(_fence, batch.FenceValue);
            _fence.SetEventOnCompletion(batch.FenceValue, _done);
        }
        else
        {
            batch.Query ??= _device.CreateQuery(Vd.QueryType.Event);
            _context.End(batch.Query);
        }

        return new ReadbackTicket(batch.Ticket);
    }

    private static void Free(Batch batch)
    {
        batch.Ticket = 0;
        batch.Abandoned = false;
        batch.Regions.Clear();
        batch.Used.Clear();
    }

    private void Fill(Batch batch, Vd.ID3D11Texture2D texture, IReadOnlyList<Rect> regions)
    {
        batch.Regions.Clear();
        batch.Used.Clear();
        var taken = new HashSet<Slot>();
        var chosen = new Slot?[regions.Count];
        for (var i = 0; i < regions.Count; i++)
        {
            var width = Bucket(regions[i].Width);
            var height = Bucket(regions[i].Height);
            foreach (var slot in batch.Slots)
            {
                if (!taken.Contains(slot) && slot.Staging is not null && slot.Width == width && slot.Height == height)
                {
                    chosen[i] = slot;
                    taken.Add(slot);
                    break;
                }
            }
        }

        for (var i = 0; i < regions.Count; i++)
        {
            if (chosen[i] is not null)
                continue;
            Slot? spare = null;
            foreach (var slot in batch.Slots)
            {
                if (!taken.Contains(slot))
                {
                    spare = slot;
                    break;
                }
            }

            if (spare is null)
            {
                spare = new Slot();
                batch.Slots.Add(spare);
            }

            chosen[i] = spare;
            taken.Add(spare);
        }

        for (var i = 0; i < regions.Count; i++)
        {
            var slot = chosen[i]!;
            var region = regions[i];
            EnsureStaging(slot, region.Width, region.Height);
            var box = new Vortice.Mathematics.Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1);
            _context.CopySubresourceRegion(slot.Staging!, 0, 0, 0, 0, texture, 0, box);
            batch.Regions.Add(region);
            batch.Used.Add(slot);
        }
    }

    private static int Bucket(int size) => (size + StagingBucket - 1) / StagingBucket * StagingBucket;

    private void EnsureStaging(Slot slot, int width, int height)
    {
        var bucketWidth = Bucket(width);
        var bucketHeight = Bucket(height);
        if (slot.Staging is not null && slot.Width == bucketWidth && slot.Height == bucketHeight)
            return;
        TryDispose(slot.Staging);
        slot.Staging = null;
        var description = new Vd.Texture2DDescription
        {
            Width = (uint)bucketWidth,
            Height = (uint)bucketHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vx.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vx.SampleDescription(1, 0),
            Usage = Vd.ResourceUsage.Staging,
            BindFlags = Vd.BindFlags.None,
            CPUAccessFlags = Vd.CpuAccessFlags.Read,
            MiscFlags = Vd.ResourceOptionFlags.None,
        };
        slot.Staging = _device.CreateTexture2D(description);
        slot.Width = bucketWidth;
        slot.Height = bucketHeight;
    }

    private bool ReadSlot(Slot slot, Rect region, Mat destination, bool raw)
    {
        var staging = slot.Staging!;
        Result result = _context.Map(staging, 0, Vd.MapMode.Read, Vd.MapFlags.DoNotWait, out Vd.MappedSubresource mapped);
        if (result.Code == DxgiErrorWasStillDrawing)
            return false;
        result.CheckError();
        try
        {
            using var source = Mat.FromPixelData(region.Height, region.Width, MatType.CV_8UC4, mapped.DataPointer, mapped.RowPitch);
            if (raw)
                source.CopyTo(destination);
            else
                Cv2.CvtColor(source, destination, ColorConversionCodes.BGRA2BGR);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        return true;
    }

    private static void DisposeBatch(Batch batch)
    {
        foreach (var slot in batch.Slots)
        {
            TryDispose(slot.Staging);
            slot.Staging = null;
        }

        batch.Slots.Clear();
        TryDispose(batch.Query);
        batch.Query = null;
        Free(batch);
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
