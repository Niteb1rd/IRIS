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
using Wg = Windows.Graphics;
using Wgc = Windows.Graphics.Capture;
using Wgd = Windows.Graphics.DirectX;
using Wgd11 = Windows.Graphics.DirectX.Direct3D11;

namespace Iris.App.Services;

public sealed partial class WgcFrameSource : IFrameSource
{
    private const int PoolBuffers = 2;
    private const int CopyQueuePoolBuffers = PoolBuffers + LiveSettings.ReadbackFrames + LiveSettings.ReadbackScanSlots;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private RegionReadback? _readback;
    private CopyQueueReadback? _copyQueue;
    private bool _copyQueueOn;
    private bool _copyQueueWanted;
    private readonly AutoResetEvent _arrived = new(false);
    private Vd.ID3D11Device? _device;
    private Vd.ID3D11DeviceContext? _context;
    private Wgd11.IDirect3DDevice? _winrtDevice;
    private Wgc.GraphicsCaptureItem? _item;
    private Wgc.Direct3D11CaptureFramePool? _pool;
    private Wgc.GraphicsCaptureSession? _session;
    private Wg.SizeInt32 _poolSize;
    private volatile bool _itemClosed;
    private IntPtr _hwnd;
    private Wgc.Direct3D11CaptureFrame? _held;
    private Vd.ID3D11Texture2D? _heldTexture;
    private double _heldMs;
    private int _heldOffsetX;
    private int _heldOffsetY;
    private long _retryAtTick;
    private long _lastFrameTick;
    private long _lastCallTick;
    private string? _lastProblem;
    private bool _borderless;
    private bool _disposed;

    public bool HideBorder { get; set; }

    public bool UseCopyQueue { get; set; }

    public Action<string>? Log { get; set; }

    public WaitHandle? ArrivalHandle => _arrived;

    public WaitHandle? CompletionHandle => _copyQueue?.CompletionHandle ?? _readback?.CompletionHandle;

    public bool CanTakeFrame => _copyQueue?.CanTakeFrame ?? _readback?.CanTakeFrame ?? true;

    public bool CanTakeScan => _copyQueue?.CanTakeScan ?? _readback?.CanTakeScan ?? true;

    private int PoolBufferCount => _copyQueueOn ? CopyQueuePoolBuffers : PoolBuffers;

    public string Name => "window capture";

    public FrameArrival AcquireNextFrame(IntPtr hwnd, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseHeldFrame();
        if (hwnd == IntPtr.Zero)
            return Unavailable("there is no game window");
        if (_pool is not null && (_hwnd != hwnd || _itemClosed || _borderless != CaptureBorder.WantsBorderless(HideBorder) || _copyQueueWanted != UseCopyQueue))
        {
            Release();
            _retryAtTick = 0;
        }

        var tick = Environment.TickCount64;
        if (tick - _lastCallTick > LiveSettings.CaptureNoFrameRestartMs)
            _lastFrameTick = tick;
        _lastCallTick = tick;
        if (_pool is null)
        {
            if (tick < _retryAtTick)
                return Unavailable(_lastProblem ?? "window capture is not ready");
            string? problem;
            try
            {
                problem = SetUp(hwnd);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
            }

            if (problem is not null)
            {
                Release();
                _retryAtTick = tick + LiveSettings.CaptureRetryAfterFailureMs;
                return Unavailable(problem);
            }

            _hwnd = hwnd;
            _lastFrameTick = tick;
        }

        try
        {
            return Acquire(tick, timeoutMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Release();
            _retryAtTick = tick + LiveSettings.CaptureRetryAfterFailureMs;
            return Unavailable("window capture failed and will be set up again: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void ReleaseHeldFrame()
    {
        var texture = _heldTexture;
        var frame = _held;
        _heldTexture = null;
        _held = null;
        TryDispose(texture);
        TryDispose(frame);
    }

    public CopyIssue IssueHeldFrameCopies(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var texture = _heldTexture;
        if (texture is null || _device is null || _context is null)
        {
            ReleaseHeldFrame();
            return CopyIssue.Unavailable("no window frame was held");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var description = texture.Description;
            CaptureSharing.LogOnce(Log, "Window capture", description.MiscFlags);
            if (description.Format != Vx.Format.B8G8R8A8_UNorm)
            {
                ReleaseHeldFrame();
                Release();
                _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                return CopyIssue.Unavailable($"the window picture has pixel format {description.Format}, which IRIS cannot read (only 8 bit B8G8R8A8 is supported)");
            }

            var width = (int)description.Width;
            var height = (int)description.Height;
            var inTexture = new List<Rect>(regions.Count);
            foreach (var region in regions)
            {
                var left = region.X + _heldOffsetX;
                var top = region.Y + _heldOffsetY;
                if (left < 0 || top < 0 || left + region.Width > width || top + region.Height > height)
                {
                    ReleaseHeldFrame();
                    return CopyIssue.Unavailable($"a capture region is not inside the captured game window ({width}x{height}, client area at {_heldOffsetX},{_heldOffsetY})");
                }

                inTexture.Add(new Rect(left, top, region.Width, region.Height));
            }

            var patches = firstIsScan ? inTexture.GetRange(1, inTexture.Count - 1) : inTexture;
            Rect? scan = firstIsScan && inTexture.Count > 0 ? inTexture[0] : null;
            var readback = EnsureReadback();
            (ReadbackTicket Patches, ReadbackTicket Scan) tickets;
            if (_copyQueue is { } copyQueue)
            {
                var frame = _held!;
                _held = null;
                _heldTexture = null;
                try
                {
                    tickets = copyQueue.Issue(texture, frame, patches, scan);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    s_copyQueueBroken = true;
                    _copyQueueOn = false;
                    LogCopyQueueOnce("Window capture: the Direct3D 12 copy queue failed (" + ex.Message + "); D3D11 copies are used until IRIS starts again");
                    _copyQueue = null;
                    copyQueue.Dispose();
                    return CopyIssue.Unavailable("the Direct3D 12 copy queue failed; D3D11 copies are used from the next picture");
                }
            }
            else
            {
                try
                {
                    tickets = readback!.Issue(texture, patches, scan);
                }
                finally
                {
                    ReleaseHeldFrame();
                }
            }

            return new CopyIssue(GrabStatus.Ok, null, tickets.Patches, tickets.Scan, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            ReleaseHeldFrame();
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            return CopyIssue.Unavailable("window capture failed and will be set up again: " + ex.Message);
        }
    }

    public ReadbackState Poll(ReadbackTicket ticket) => _copyQueue?.Poll(ticket) ?? _readback?.Poll(ticket) ?? ReadbackState.Lost;

    public ReadbackState ReadInto(ReadbackTicket ticket, IReadOnlyList<Mat> destinations, bool raw, out string? problem)
    {
        problem = null;
        try
        {
            var state = _copyQueue?.Read(ticket, destinations, raw) ?? _readback?.Read(ticket, destinations, raw) ?? ReadbackState.Lost;
            if (state == ReadbackState.Lost)
                problem = "the copy of a capture region is gone";
            return state;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            problem = "reading a capture region failed and window capture will be set up again: " + ex.Message;
            return ReadbackState.Lost;
        }
    }

    public void Abandon(ReadbackTicket ticket)
    {
        _copyQueue?.Abandon(ticket);
        _readback?.Abandon(ticket);
    }

    private RegionReadback? EnsureReadback()
    {
        if (_copyQueue is not null)
            return null;
        if (_readback is not null)
            return _readback;
        if (_copyQueueOn)
        {
            try
            {
                _copyQueue = new CopyQueueReadback(_device!, _context!, LiveSettings.ReadbackFrames, LiveSettings.ReadbackScanSlots, Log, logStats: AppVersion.Diagnostics);
                LogCopyQueueOnce("Window capture: region copies come back through " + _copyQueue.MethodText);
                RegionReadback.ForgetLoggedMethod();
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                s_copyQueueBroken = true;
                _copyQueueOn = false;
                LogCopyQueueOnce("Window capture: the Direct3D 12 copy queue could not be set up (" + ex.GetType().Name + ": " + ex.Message + "); D3D11 copies are used");
            }
        }

        _readback = new RegionReadback(_device!, _context!, LiveSettings.ReadbackFrames, LiveSettings.ReadbackScanSlots);
        RegionReadback.LogMethodOnce(Log, "Window capture", _readback);
        return _readback;
    }

    private static string? s_copyQueueLogged;
    private static bool s_copyQueueBroken;

    private void LogCopyQueueOnce(string text)
    {
        if (Log is null || text == s_copyQueueLogged)
            return;
        s_copyQueueLogged = text;
        Log(text);
    }

    public void Release()
    {
        ReleaseHeldFrame();
        _readback?.Dispose();
        _readback = null;
        _copyQueue?.Dispose();
        _copyQueue = null;
        if (CopyQueueReadback.Abandoned)
            s_copyQueueBroken = true;

        var session = _session;
        var pool = _pool;
        var item = _item;
        _session = null;
        _pool = null;
        _item = null;
        try
        {
            if (pool is not null)
                pool.FrameArrived -= OnFrameArrived;
            if (item is not null)
                item.Closed -= OnItemClosed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        TryDispose(session);
        TryDispose(pool);
        TryDispose(_winrtDevice);
        _winrtDevice = null;
        TryDispose(_context);
        _context = null;
        TryDispose(_device);
        _device = null;
        _hwnd = IntPtr.Zero;
        _itemClosed = false;
        _arrived.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Release();
        _disposed = true;
        _arrived.Dispose();
    }
}
