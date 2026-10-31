
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

public sealed partial class WgcFrameSource
{
    private FrameArrival Unavailable(string message)
    {
        _lastProblem = message;
        return new FrameArrival(FrameWait.Unavailable, Message: message);
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

    private static void ReleaseRcw(object value)
    {
        if (Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    private void OnFrameArrived(Wgc.Direct3D11CaptureFramePool sender, object args) => Signal();

    private void OnItemClosed(Wgc.GraphicsCaptureItem sender, object args)
    {
        _itemClosed = true;
        Signal();
    }

    private void Signal()
    {
        try
        {
            _arrived.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string? SetUp(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
            return "the game window does not exist";
        var levels = new[] { Vc.FeatureLevel.Level_11_1, Vc.FeatureLevel.Level_11_0, Vc.FeatureLevel.Level_10_1, Vc.FeatureLevel.Level_10_0 };
        if (Vd.D3D11.D3D11CreateDevice((Vx.IDXGIAdapter?)null, Vc.DriverType.Hardware, Vd.DeviceCreationFlags.BgraSupport, levels, out Vd.ID3D11Device? device).Failure || device is null)
            return "a Direct3D 11 device could not be created";
        _device = device;
        _context = device.ImmediateContext;
        try
        {
            using var multithread = device.QueryInterface<Vd.ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException or InvalidCastException)
        {
        }

        using (var dxgi = device.QueryInterface<Vx.IDXGIDevice>())
        {
            var result = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable);
            if (result < 0 || inspectable == IntPtr.Zero)
                return $"the Direct3D device could not be handed to Windows capture (HRESULT 0x{result:X8})";
            try
            {
                _winrtDevice = WinRT.MarshalInterface<Wgd11.IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        IntPtr itemPointer;
        try
        {
            var iid = GraphicsCaptureItemIid;
            itemPointer = interop.CreateForWindow(hwnd, ref iid);
        }
        finally
        {
            ReleaseRcw(interop);
        }

        Wgc.GraphicsCaptureItem item;
        try
        {
            item = Wgc.GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }

        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return "the game window has no size";

        _itemClosed = false;
        _arrived.Reset();
        var pool = Wgc.Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, Wgd.DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
        _pool = pool;
        _poolSize = size;
        pool.FrameArrived += OnFrameArrived;
        _item = item;
        item.Closed += OnItemClosed;
        var session = pool.CreateCaptureSession(item);
        _session = session;
        session.IsCursorCaptureEnabled = false;
        var hideBorder = HideBorder;
        _borderless = CaptureBorder.WantsBorderless(hideBorder);
        CaptureBorder.Apply(session, hideBorder, _borderless, Log);
        session.StartCapture();
        return null;
    }

    private FrameArrival Acquire(long tick)
    {
        Wgc.Direct3D11CaptureFrame? newest = null;
        _arrived.Reset();
        Drain(ref newest);
        if (newest is null)
        {
            _arrived.WaitOne(LiveSettings.CaptureAcquireTimeoutMs);
            Drain(ref newest);
        }

        if (newest is null)
        {
            if (_itemClosed)
            {
                Release();
                _retryAtTick = 0;
                return Unavailable("the game window was closed");
            }

            if (tick - _lastFrameTick > LiveSettings.CaptureNoFrameRestartMs)
            {
                Release();
                _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                return Unavailable($"window capture delivered no frames for {LiveSettings.CaptureNoFrameRestartMs / 1000} s and is set up again");
            }

            return new FrameArrival(FrameWait.Unchanged);
        }

        var acquiredAtMs = StopwatchClock.Shared.NowMs;
        _lastFrameTick = Environment.TickCount64;
        var content = newest.ContentSize;
        if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
        {
            TryDispose(newest);
            if (content.Width > 0 && content.Height > 0)
            {
                _pool!.Recreate(_winrtDevice!, Wgd.DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, content);
                _poolSize = content;
            }

            return new FrameArrival(FrameWait.Unchanged);
        }

        var qpc = (long)((Int128)newest.SystemRelativeTime.Ticks * Stopwatch.Frequency / 10_000_000L);
        var origin = StopwatchClock.Shared.OriginTimestamp;
        var rawMs = FrameClock.QpcToMs(qpc, origin, Stopwatch.Frequency);
        if (qpc != 0 && !double.IsNaN(rawMs) && acquiredAtMs - rawMs > LiveSettings.FrameMaxBehindMs)
        {
            TryDispose(newest);
            return new FrameArrival(FrameWait.Unchanged);
        }

        Vd.ID3D11Texture2D texture;
        try
        {
            var access = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(newest.Surface);
            try
            {
                var iid = Texture2DIid;
                texture = new Vd.ID3D11Texture2D(access.GetInterface(ref iid));
            }
            finally
            {
                ReleaseRcw(access);
            }
        }
        catch
        {
            TryDispose(newest);
            throw;
        }

        _held = newest;
        _heldTexture = texture;
        _heldMs = Math.Min(FrameClock.FrameMs(qpc, origin, Stopwatch.Frequency, acquiredAtMs), acquiredAtMs);
        if (!NativeMethods.TryGetClientOffset(_hwnd, out _heldOffsetX, out _heldOffsetY))
        {
            _heldOffsetX = 0;
            _heldOffsetY = 0;
        }

        return new FrameArrival(FrameWait.NewFrame, _heldMs);
    }

    private void Drain(ref Wgc.Direct3D11CaptureFrame? newest)
    {
        while (_pool!.TryGetNextFrame() is { } frame)
        {
            TryDispose(newest);
            newest = frame;
        }
    }
}
