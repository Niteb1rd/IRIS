using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Iris.Core.Overlay;

namespace Iris.App.Services;

internal sealed class ResourceOverlay : IDisposable
{
    private const int SurfaceWidthStep = 64;
    private const int ScreenWidthMetric = 0;
    private const int ScreenHeightMetric = 1;

    private readonly AppServices _services;
    private readonly Func<GameWindowSnapshot> _game;
    private readonly Dispatcher _dispatcher;
    private LayeredWindow? _window;
    private ResourceMonitor? _monitor;
    private byte[] _pixels = Array.Empty<byte>();
    private ResourceReading? _last;
    private PixelRect? _box;
    private (int RightPx, int TopPx)? _dragging;
    private PixelRect? _lastGameBounds;
    private bool _failed;
    private bool _disposed;

    public ResourceOverlay(AppServices services, Func<GameWindowSnapshot> game, Dispatcher dispatcher)
    {
        _services = services;
        _game = game;
        _dispatcher = dispatcher;
    }

    public PixelRect? Box => _monitor is not null && _window is { IsShown: true } ? _box : null;

    public void Apply()
    {
        if (_disposed)
            return;
        var settings = _services.Settings;
        if (!settings.ShowResourceOverlay)
        {
            Stop();
            return;
        }

        if (_window is { } window)
            CaptureExclusion.Apply(window.Handle, settings.OverlayShownInCapture);
        _monitor ??= new ResourceMonitor(reading => _dispatcher.BeginInvoke(new Action(() => Present(reading))));
        if (_last is { } last)
            Present(last);
    }

    public void MoveTo(double x, double y, bool save)
    {
        if (_disposed || _box is not { } box)
            return;
        var offsets = ResourceOverlayPlacement.Offsets(Screen(), (int)box.Width, (int)box.Height, x, y);
        _dragging = offsets;
        if (save)
        {
            _services.UpdateSettings(settings => settings with { ResourceOverlayRightPx = offsets.RightPx, ResourceOverlayTopPx = offsets.TopPx });
            _dragging = null;
        }

        if (_last is { } last)
            Present(last);
    }

    public void CancelDrag()
    {
        if (_disposed || _dragging is null)
            return;
        _dragging = null;
        if (_last is { } last)
            Present(last);
    }

    private void Present(ResourceReading reading)
    {
        if (_disposed || _monitor is null || _failed)
            return;
        _last = reading;
        var settings = _services.Settings;
        if (_window is null && !TryCreateWindow(settings.OverlayShownInCapture))
            return;

        var window = _window!;
        var screen = Screen();
        var scale = ResourceOverlayPlacement.GlyphScale(screen.Height);
        var text = ResourceUsage.Text(reading);
        var (width, height) = StampFont.Measure(text, scale);
        var surfaceWidth = (width + SurfaceWidthStep - 1) / SurfaceWidthStep * SurfaceWidthStep;
        var bits = window.BeginSurface(surfaceWidth, height);
        if (bits == IntPtr.Zero)
            return;

        var stride = window.Stride;
        if (_pixels.Length != stride * height)
            _pixels = new byte[stride * height];
        else
            Array.Clear(_pixels);
        StampFont.DrawOutlined(_pixels, stride, surfaceWidth, height, text, scale);
        Marshal.Copy(_pixels, 0, bits, _pixels.Length);

        var right = _dragging?.RightPx ?? settings.ResourceOverlayRightPx;
        var top = _dragging?.TopPx ?? settings.ResourceOverlayTopPx;
        var (x, y) = ResourceOverlayPlacement.Place(screen, width, height, right, top);
        if (!window.Commit(x, y, width, height))
            return;
        _box = new PixelRect(x, y, width, height);
        if (!window.IsShown)
            window.Show();
        window.AssertTopmost();
    }

    private bool TryCreateWindow(bool visibleInCapture)
    {
        try
        {
            _window = new LayeredWindow();
            CaptureExclusion.Apply(_window.Handle, visibleInCapture);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _failed = true;
            _services.Log.Write(_services.Clock.UtcNow, "The resource readout could not be shown: " + ex.Message);
            return false;
        }
    }

    private PixelRect Screen()
    {
        var game = _game();
        if (game.State == GameWindowState.Ready)
            _lastGameBounds = game.Bounds;
        return _lastGameBounds ?? new PixelRect(0, 0, NativeMethods.GetSystemMetrics(ScreenWidthMetric), NativeMethods.GetSystemMetrics(ScreenHeightMetric));
    }

    private void Stop()
    {
        _monitor?.Dispose();
        _monitor = null;
        _last = null;
        _box = null;
        _dragging = null;
        _window?.Hide();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _window?.Dispose();
        _window = null;
    }
}
