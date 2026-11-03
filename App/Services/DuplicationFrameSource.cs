using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Iris.Core.Pipeline;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

public sealed class DuplicationFrameSource : IFrameSource
{
    private sealed class Slot
    {
        public Vd.ID3D11Texture2D? Staging;
        public int StagingWidth;
        public int StagingHeight;
    }

    private readonly List<Slot> _slots = new();
    private readonly Slot _scan = new();
    private Rect _pendingScanRegion;
    private Mat? _pendingScanDestination;
    private Vd.ID3D11Device? _device;
    private Vd.ID3D11DeviceContext? _context;
    private Vx.IDXGIOutputDuplication? _duplication;
    private IntPtr _monitor;
    private PixelRectInt _monitorBounds;
    private Vx.IDXGIResource? _held;
    private double _heldMs;
    private int _heldOffsetX;
    private int _heldOffsetY;
    private long _retryAtTick;
    private string? _lastProblem;
    private bool _disposed;
    private double _lastGameFrameMs = double.NegativeInfinity;
    private NativeMethods.NativeRect[] _dirtyRects = new NativeMethods.NativeRect[16];
    private MoveRect[] _moveRects = new MoveRect[4];
    private GetFrameDirtyRects? _getDirtyRects;
    private GetFrameMoveRects? _getMoveRects;

    private readonly record struct PixelRectInt(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct MoveRect
    {
        public NativeMethods.NativePoint SourcePoint;
        public NativeMethods.NativeRect DestinationRect;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrameDirtyRects(IntPtr self, uint bufferSizeBytes, [Out] NativeMethods.NativeRect[] buffer, out uint requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrameMoveRects(IntPtr self, uint bufferSizeBytes, [Out] MoveRect[] buffer, out uint requiredBytes);

    private const int DirtyRectsSlot = 9;
    private const int MoveRectsSlot = 10;
    private const int DxgiErrorMoreData = unchecked((int)0x887A0003);

    public string Name => "desktop duplication";

    public Action<string>? Log { get; set; }

    public CopyTimes LastCopyTimes { get; private set; }

    public double LastUpdatedShare { get; private set; } = double.NaN;

    private int _skippedUpdates;

    public int TakeSkippedUpdates()
    {
        var skipped = _skippedUpdates;
        _skippedUpdates = 0;
        return skipped;
    }

    public FrameArrival AcquireNextFrame(IntPtr hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseHeldFrame();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return Unavailable("there is no game window");
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !NativeMethods.TryGetMonitorBounds(hwnd, out var monitorRect))
            return Unavailable("the game window is not on any screen");
        var bounds = new PixelRectInt(
            (int)Math.Round(monitorRect.X), (int)Math.Round(monitorRect.Y), (int)Math.Round(monitorRect.Width), (int)Math.Round(monitorRect.Height));
        if (_duplication is not null && (monitor != _monitor || bounds != _monitorBounds))
        {
            Release();
            _retryAtTick = 0;
        }

        var tick = Environment.TickCount64;
        if (_duplication is null)
        {
            if (tick < _retryAtTick)
                return Unavailable(_lastProblem ?? "desktop duplication is not ready");
            string? problem;
            try
            {
                problem = SetUp(monitor, bounds);
            }
            catch (Exception ex) when (ex is SharpGenException or COMException)
            {
                problem = "desktop duplication could not be started: " + ex.Message;
            }

            if (problem is not null)
            {
                Release();
                _retryAtTick = tick + LiveSettings.CaptureRetryAfterFailureMs;
                return Unavailable(problem);
            }
        }

        try
        {
            return Acquire(hwnd);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Release();
            _retryAtTick = tick + LiveSettings.CaptureRetryAfterFailureMs;
            return Unavailable("desktop duplication failed and will be set up again: " + ex.Message);
        }
    }

    public void ReleaseHeldFrame()
    {
        var resource = _held;
        _held = null;
        if (resource is null)
            return;
        try
        {
            _duplication?.ReleaseFrame();
        }
        catch (SharpGenException)
        {
        }

        TryDispose(resource);
    }

    public GrabRegionsResult CopyHeldFrameInto(IReadOnlyList<Rect> regions, bool firstIsScan, IReadOnlyList<Mat> destinations, bool deferScan = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destinations.Count != regions.Count)
            throw new ArgumentException("One destination for each region is needed.", nameof(destinations));
        var resource = _held;
        if (resource is null || _duplication is null)
        {
            ReleaseHeldFrame();
            return GrabRegionsResult.Unavailable("no screen frame was held");
        }

        _pendingScanDestination = null;
        LastCopyTimes = default;
        var frameMs = _heldMs;
        if (regions.Count == 0)
        {
            ReleaseHeldFrame();
            return new GrabRegionsResult(GrabStatus.Ok, Array.Empty<Mat?>(), null, frameMs);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            Slot[] slots;
            using (var texture = resource.QueryInterface<Vd.ID3D11Texture2D>())
            {
                var description = texture.Description;
                var width = (int)description.Width;
                var height = (int)description.Height;
                if (width != _monitorBounds.Width || height != _monitorBounds.Height)
                {
                    ReleaseHeldFrame();
                    Release();
                    _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                    return GrabRegionsResult.Unavailable($"the screen picture is {width}x{height}, not the {_monitorBounds.Width}x{_monitorBounds.Height} of the monitor (a rotated screen is not supported)");
                }

                if (description.Format != Vx.Format.B8G8R8A8_UNorm)
                {
                    ReleaseHeldFrame();
                    Release();
                    _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                    return GrabRegionsResult.Unavailable($"the screen picture has pixel format {description.Format}, which IRIS cannot read (only 8 bit B8G8R8A8 is supported)");
                }

                foreach (var region in regions)
                {
                    var left = region.X + _heldOffsetX;
                    var top = region.Y + _heldOffsetY;
                    if (left < 0 || top < 0 || left + region.Width > width || top + region.Height > height)
                    {
                        ReleaseHeldFrame();
                        return GrabRegionsResult.Unavailable("a capture region is not on the game's screen (the game window must be completely on one monitor)");
                    }
                }

                slots = AssignSlots(regions, firstIsScan);
                for (var i = firstIsScan ? 1 : 0; i < regions.Count; i++)
                    CopyRegion(texture, regions[i], slots[i]);
                if (firstIsScan)
                {
                    if (regions.Count > 1)
                        _context!.Flush();
                    CopyRegion(texture, regions[0], slots[0]);
                }
            }

            ReleaseHeldFrame();
            var issued = Stopwatch.GetTimestamp();

            var images = new List<Mat?>(regions.Count);
            for (var i = 0; i < regions.Count; i++)
                images.Add(destinations[i]);
            for (var i = firstIsScan ? 1 : 0; i < regions.Count; i++)
                ReadStaging(slots[i], regions[i], destinations[i], false);
            LastCopyTimes = new CopyTimes(Stopwatch.GetElapsedTime(started, issued).TotalMilliseconds, Stopwatch.GetElapsedTime(issued).TotalMilliseconds);
            if (firstIsScan)
            {
                if (deferScan)
                {
                    _pendingScanRegion = regions[0];
                    _pendingScanDestination = destinations[0];
                }
                else
                {
                    ReadStaging(slots[0], regions[0], destinations[0], true);
                }
            }

            return new GrabRegionsResult(GrabStatus.Ok, images, null, frameMs);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            ReleaseHeldFrame();
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            return GrabRegionsResult.Unavailable("desktop duplication failed and will be set up again: " + ex.Message);
        }
    }

    public bool ReadPendingScan(out string? problem)
    {
        problem = null;
        var destination = _pendingScanDestination;
        _pendingScanDestination = null;
        if (destination is null)
            return true;
        try
        {
            if (_scan.Staging is null || _context is null)
            {
                problem = "the scan region's copy is gone";
                return false;
            }

            ReadStaging(_scan, _pendingScanRegion, destination, true);
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            problem = "reading the scan region failed and desktop duplication will be set up again: " + ex.Message;
            return false;
        }
    }

    public void Release()
    {
        _pendingScanDestination = null;
        ReleaseHeldFrame();
        foreach (var slot in AllSlots())
        {
            TryDispose(slot.Staging);
            slot.Staging = null;
            slot.StagingWidth = 0;
            slot.StagingHeight = 0;
        }

        TryDispose(_duplication);
        _duplication = null;
        _getDirtyRects = null;
        _getMoveRects = null;
        TryDispose(_context);
        _context = null;
        TryDispose(_device);
        _device = null;
        _monitor = IntPtr.Zero;
        _monitorBounds = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Release();
        _slots.Clear();
        _disposed = true;
    }

    private FrameArrival Unavailable(string message)
    {
        _lastProblem = message;
        return new FrameArrival(FrameWait.Unavailable, Message: message);
    }

    private string? SetUp(IntPtr monitor, PixelRectInt bounds)
    {
        if (Vx.DXGI.CreateDXGIFactory1<Vx.IDXGIFactory1>(out var factory).Failure || factory is null)
            return "DXGI could not be started";
        using (factory)
        {
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            if (output.Description.Monitor != monitor)
                                continue;
                            var levels = new[] { Vc.FeatureLevel.Level_11_1, Vc.FeatureLevel.Level_11_0, Vc.FeatureLevel.Level_10_1, Vc.FeatureLevel.Level_10_0 };
                            if (Vd.D3D11.D3D11CreateDevice(adapter, Vc.DriverType.Unknown, Vd.DeviceCreationFlags.BgraSupport, levels, out Vd.ID3D11Device? device).Failure || device is null)
                                return "a Direct3D 11 device could not be created on the graphics adapter of the game's monitor";
                            _device = device;
                            _context = device.ImmediateContext;
                            CaptureGpuPriority.Raise(device, Log);
                            using var output1 = output.QueryInterface<Vx.IDXGIOutput1>();
                            _duplication = output1.DuplicateOutput(device);
                            _monitor = monitor;
                            _monitorBounds = bounds;
                            return null;
                        }
                    }
                }
            }
        }

        return "DXGI has no output for the monitor that shows the game";
    }

    private FrameArrival Acquire(IntPtr hwnd)
    {
        var duplication = _duplication!;
        Result acquired;
        Vx.IDXGIResource? resource = null;
        long presentTicks = 0;
        long accumulated = 0;
        try
        {
            acquired = duplication.AcquireNextFrame(LiveSettings.CaptureAcquireTimeoutMs, out var info, out resource);
            presentTicks = info.LastPresentTime;
            accumulated = info.AccumulatedFrames;
        }
        catch (SharpGenException ex)
        {
            acquired = ex.ResultCode;
            resource = null;
        }

        if (acquired == Vx.ResultCode.WaitTimeout)
        {
            TryDispose(resource);
            return new FrameArrival(FrameWait.Unchanged);
        }

        if (acquired.Failure || resource is null)
        {
            TryDispose(resource);
            var lost = acquired == Vx.ResultCode.AccessLost;
            Release();
            _retryAtTick = Environment.TickCount64 + (lost ? 0 : LiveSettings.CaptureRetryAfterFailureMs);
            return Unavailable(lost ? "desktop duplication was lost (screen mode change, secure desktop or exclusive full screen) and is set up again" : "desktop duplication failed: " + acquired);
        }

        _held = resource;
        if (accumulated == 0 && presentTicks == 0)
        {
            ReleaseHeldFrame();
            return new FrameArrival(FrameWait.Unchanged);
        }

        var acquiredAtMs = StopwatchClock.Shared.NowMs;
        _heldMs = Math.Min(FrameClock.FrameMs(presentTicks, StopwatchClock.Shared.OriginTimestamp, Stopwatch.Frequency, acquiredAtMs), acquiredAtMs);
        var origin = new NativeMethods.NativePoint();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            ReleaseHeldFrame();
            return Unavailable("the game window's position is not known");
        }

        _heldOffsetX = origin.X - _monitorBounds.X;
        _heldOffsetY = origin.Y - _monitorBounds.Y;

        var share = double.NaN;
        if (NativeMethods.GetClientRect(hwnd, out var client) && client.Right > client.Left && client.Bottom > client.Top)
        {
            share = UpdatedShare(_heldOffsetX, _heldOffsetY, client.Right - client.Left, client.Bottom - client.Top);
            if (share < LiveSettings.DuplicationGameUpdateShare && acquiredAtMs - _lastGameFrameMs < LiveSettings.DuplicationFallbackMs)
            {
                ReleaseHeldFrame();
                _skippedUpdates++;
                return new FrameArrival(FrameWait.Unchanged);
            }
        }

        LastUpdatedShare = share;

        _lastGameFrameMs = acquiredAtMs;
        return new FrameArrival(FrameWait.NewFrame, _heldMs);
    }

    private double UpdatedShare(int left, int top, int width, int height)
    {
        var right = Math.Min(left + width, _monitorBounds.Width);
        var bottom = Math.Min(top + height, _monitorBounds.Height);
        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        width = right - left;
        height = bottom - top;
        if (width <= 0 || height <= 0)
            return 1.0;
        var self = _duplication!.NativePointer;
        var getDirty = _getDirtyRects ??= VtableMethod<GetFrameDirtyRects>(self, DirtyRectsSlot);
        var getMoves = _getMoveRects ??= VtableMethod<GetFrameMoveRects>(self, MoveRectsSlot);
        var dirty = ReadRects((uint b, NativeMethods.NativeRect[] buffer, out uint r) => getDirty(self, b, buffer, out r), ref _dirtyRects);
        var moves = ReadRects((uint b, MoveRect[] buffer, out uint r) => getMoves(self, b, buffer, out r), ref _moveRects);
        if (dirty < 0 || moves < 0)
            return 1.0;
        var covered = 0.0;
        for (var i = 0; i < dirty; i++)
            covered += Inside(_dirtyRects[i], left, top, width, height);
        for (var i = 0; i < moves; i++)
            covered += Inside(_moveRects[i].DestinationRect, left, top, width, height);
        return covered / ((double)width * height);
    }

    private delegate int RectsCall<T>(uint bufferSizeBytes, T[] buffer, out uint requiredBytes);

    private static int ReadRects<T>(RectsCall<T> call, ref T[] buffer) where T : struct
    {
        var size = (uint)Marshal.SizeOf<T>();
        var result = call((uint)buffer.Length * size, buffer, out var required);
        if (result == DxgiErrorMoreData)
        {
            buffer = new T[(required + size - 1) / size];
            result = call((uint)buffer.Length * size, buffer, out required);
        }

        return result < 0 ? -1 : (int)Math.Min(buffer.Length, required / size);
    }

    private static double Inside(NativeMethods.NativeRect r, int left, int top, int width, int height)
    {
        var w = Math.Min(r.Right, left + width) - Math.Max(r.Left, left);
        var h = Math.Min(r.Bottom, top + height) - Math.Max(r.Top, top);
        return w > 0 && h > 0 ? (double)w * h : 0.0;
    }

    private static T VtableMethod<T>(IntPtr self, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(self);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private IEnumerable<Slot> AllSlots()
    {
        yield return _scan;
        foreach (var slot in _slots)
            yield return slot;
    }

    private Slot[] AssignSlots(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        var result = new Slot?[regions.Count];
        var used = new HashSet<Slot>();
        var start = 0;
        if (firstIsScan && regions.Count > 0)
        {
            result[0] = _scan;
            used.Add(_scan);
            start = 1;
        }

        for (var i = start; i < regions.Count; i++)
        {
            var width = Bucket(regions[i].Width);
            var height = Bucket(regions[i].Height);
            result[i] = _slots.FirstOrDefault(slot => !used.Contains(slot) && slot.Staging is not null && slot.StagingWidth == width && slot.StagingHeight == height);
            if (result[i] is not null)
                used.Add(result[i]!);
        }

        for (var i = start; i < regions.Count; i++)
        {
            if (result[i] is not null)
                continue;
            var slot = _slots.FirstOrDefault(s => !used.Contains(s));
            if (slot is null)
            {
                slot = new Slot();
                _slots.Add(slot);
            }

            result[i] = slot;
            used.Add(slot);
        }

        return result!;
    }

    private static int Bucket(int size) => (size + WgcFrameSource.StagingBucket - 1) / WgcFrameSource.StagingBucket * WgcFrameSource.StagingBucket;

    private void CopyRegion(Vd.ID3D11Texture2D texture, Rect region, Slot slot)
    {
        EnsureStaging(slot, region.Width, region.Height);
        var left = region.X + _heldOffsetX;
        var top = region.Y + _heldOffsetY;
        var box = new Vortice.Mathematics.Box(left, top, 0, left + region.Width, top + region.Height, 1);
        _context!.CopySubresourceRegion(slot.Staging!, 0, 0, 0, 0, texture, 0, box);
    }

    private void EnsureStaging(Slot slot, int width, int height)
    {
        var bucketWidth = Bucket(width);
        var bucketHeight = Bucket(height);
        if (slot.Staging is not null && slot.StagingWidth == bucketWidth && slot.StagingHeight == bucketHeight)
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
        slot.Staging = _device!.CreateTexture2D(description);
        slot.StagingWidth = bucketWidth;
        slot.StagingHeight = bucketHeight;
    }

    private void ReadStaging(Slot slot, Rect region, Mat destination, bool raw)
    {
        var staging = slot.Staging!;
        var context = _context!;
        var mapped = context.Map(staging, 0, Vd.MapMode.Read, Vd.MapFlags.None);
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
            context.Unmap(staging, 0);
        }
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
