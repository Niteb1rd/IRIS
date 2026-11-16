using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Pipeline;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

public sealed partial class DuplicationFrameSource : IFrameSource
{
    private RegionReadback? _readback;
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
    private string? _lastLayout;
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

    public WaitHandle? ArrivalHandle => null;

    public WaitHandle? CompletionHandle => _readback?.CompletionHandle;

    public bool CanTakeFrame => _readback?.CanTakeFrame ?? true;

    public bool CanTakeScan => _readback?.CanTakeScan ?? true;

    public double LastUpdatedShare { get; private set; } = double.NaN;

    private int _skippedUpdates;

    public int TakeSkippedUpdates()
    {
        var skipped = _skippedUpdates;
        _skippedUpdates = 0;
        return skipped;
    }

    public FrameArrival AcquireNextFrame(IntPtr hwnd, int timeoutMs)
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
            return Acquire(hwnd, timeoutMs);
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

    public CopyIssue IssueHeldFrameCopies(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = _held;
        if (resource is null || _duplication is null || _device is null || _context is null)
        {
            ReleaseHeldFrame();
            return CopyIssue.Unavailable("no screen frame was held");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            (ReadbackTicket Patches, ReadbackTicket Scan) tickets;
            using (var texture = resource.QueryInterface<Vd.ID3D11Texture2D>())
            {
                var description = texture.Description;
                CaptureSharing.LogOnce(Log, "Desktop duplication", description.MiscFlags);
                var width = (int)description.Width;
                var height = (int)description.Height;
                if (width != _monitorBounds.Width || height != _monitorBounds.Height)
                {
                    ReleaseHeldFrame();
                    Release();
                    _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                    return CopyIssue.Unavailable($"the screen picture is {width}x{height}, not the {_monitorBounds.Width}x{_monitorBounds.Height} of the monitor (a rotated screen is not supported)");
                }

                if (description.Format != Vx.Format.B8G8R8A8_UNorm)
                {
                    ReleaseHeldFrame();
                    Release();
                    _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                    return CopyIssue.Unavailable($"the screen picture has pixel format {description.Format}, which IRIS cannot read (only 8 bit B8G8R8A8 is supported)");
                }

                var inTexture = new List<Rect>(regions.Count);
                foreach (var region in regions)
                {
                    var left = region.X + _heldOffsetX;
                    var top = region.Y + _heldOffsetY;
                    if (left < 0 || top < 0 || left + region.Width > width || top + region.Height > height)
                    {
                        ReleaseHeldFrame();
                        return CopyIssue.Unavailable(
                            $"the game window reaches past its screen ({width}x{height} at {_monitorBounds.X},{_monitorBounds.Y}; game picture from {_heldOffsetX + _monitorBounds.X},{_heldOffsetY + _monitorBounds.Y}): it must be completely on one monitor");
                    }

                    inTexture.Add(new Rect(left, top, region.Width, region.Height));
                }

                var readback = EnsureReadback();
                tickets = readback.Issue(texture, firstIsScan ? inTexture.GetRange(1, inTexture.Count - 1) : inTexture, firstIsScan && inTexture.Count > 0 ? inTexture[0] : null);
            }

            ReleaseHeldFrame();
            return new CopyIssue(GrabStatus.Ok, null, tickets.Patches, tickets.Scan, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            ReleaseHeldFrame();
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            return CopyIssue.Unavailable("desktop duplication failed and will be set up again: " + ex.Message);
        }
    }

    public ReadbackState Poll(ReadbackTicket ticket) => _readback?.Poll(ticket) ?? ReadbackState.Lost;

    public ReadbackState ReadInto(ReadbackTicket ticket, IReadOnlyList<Mat> destinations, bool raw, out string? problem)
    {
        problem = null;
        try
        {
            var state = _readback?.Read(ticket, destinations, raw) ?? ReadbackState.Lost;
            if (state == ReadbackState.Lost)
                problem = "the copy of a capture region is gone";
            return state;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            problem = "reading a capture region failed and desktop duplication will be set up again: " + ex.Message;
            return ReadbackState.Lost;
        }
    }

    public void Abandon(ReadbackTicket ticket) => _readback?.Abandon(ticket);

    private RegionReadback EnsureReadback()
    {
        if (_readback is not null)
            return _readback;
        _readback = new RegionReadback(_device!, _context!, LiveSettings.ReadbackFrames, LiveSettings.ReadbackScanSlots);
        RegionReadback.LogMethodOnce(Log, "Desktop duplication", _readback);
        return _readback;
    }

    public void Release()
    {
        ReleaseHeldFrame();
        _readback?.Dispose();
        _readback = null;

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
                            LogLayout(factory, monitor);
                            return null;
                        }
                    }
                }
            }
        }

        return "DXGI has no output for the monitor that shows the game";
    }

    private void LogLayout(Vx.IDXGIFactory1 factory, IntPtr gameMonitor)
    {
        if (Log is null)
            return;
        var screens = new List<string>();
        var game = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var card = adapter.Description1.Description.Trim();
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var r = d.DesktopCoordinates;
                        var rotated = d.Rotation is Vx.ModeRotation.Rotate90 or Vx.ModeRotation.Rotate270 ? ", rotated" : "";
                        screens.Add($"{screens.Count + 1}: {r.Right - r.Left}x{r.Bottom - r.Top} at {r.Left},{r.Top}{rotated} on {card}");
                        if (d.Monitor == gameMonitor)
                            game = screens.Count;
                    }
                }
            }
        }

        var layout = $"the game is on screen {game} of {screens.Count} ({string.Join("; ", screens)})";
        if (layout == _lastLayout)
            return;
        _lastLayout = layout;
        Log("Desktop duplication: " + layout);
    }

    private FrameArrival Acquire(IntPtr hwnd, int timeoutMs)
    {
        var duplication = _duplication!;
        Result acquired;
        Vx.IDXGIResource? resource = null;
        long presentTicks = 0;
        long accumulated = 0;
        try
        {
            acquired = duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out resource);
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
