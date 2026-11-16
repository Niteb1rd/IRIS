using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Services;
using Iris.Core.Overlay;
using Iris.Core.Config;
using Iris.Core.Mining;
using Iris.Core.Pipeline;

namespace Iris.App.Views;

public sealed class OverlayWindow : IDisposable
{
    private const int MaxFailuresShown = 3;

    private readonly OverlayLayer _layer;
    private readonly Action<string>? _warn;
    private readonly OverlayPainter _painter;
    private LayeredWindow? _window;
    private bool _failed;
    private bool _closed;
    private bool _updateFailedLogged;
    private bool _retryUpdate;
    private int _updateFailures;
    private bool _visibleInCapture;
    private bool _excluded = true;
    private PixelRect _bounds;
    private bool _hasBounds;
    private PixelRect _fit;
    private OverlayModel? _model;
    private AppSettings? _settings;
    private string? _toast;
    private readonly PanelEntryMotion? _motion;
    private DispatcherTimer? _motionTimer;
    private bool _motionFrame;

    private DrawingVisual? _visual;
    private RenderTargetBitmap? _target;
    private PanelCurve? _curve;
    private bool _curveFailed;
    private int _targetWidth;
    private int _targetHeight;

    public OverlayWindow(OverlayLayer layer, Action<string>? warn = null, bool visibleInCapture = false)
    {
        _layer = layer;
        _warn = warn;
        _visibleInCapture = visibleInCapture;
        _painter = new OverlayPainter { Layer = layer };
        if (layer == OverlayLayer.Panel)
            _motion = new PanelEntryMotion();
    }

    private bool IsShown => _window is { IsShown: true };

    private IntPtr Handle => _window?.Handle ?? IntPtr.Zero;

    public void Close() => Dispose();

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _motionTimer?.Stop();
        var window = _window;
        _window = null;
        if (window is not null)
        {
            window.Dispose();
        }

        _visual = null;
        _target = null;
        _curve?.Dispose();
        _curve = null;
    }

    private bool EnsureWindow()
    {
        if (_window is not null)
            return true;
        if (_failed || _closed)
            return false;

        try
        {
            _window = new LayeredWindow();
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            Fail($"The {_layer} overlay window could not be created ({ex.Message})");
            return false;
        }

        _excluded = CaptureExclusion.Apply(Handle, _visibleInCapture);
        if (!_excluded && !_visibleInCapture)
        {
            _warn?.Invoke($"The {_layer} overlay window could not be excluded from screen capture; it shows in screenshots and recordings");
        }

        return true;
    }

    private void Fail(string text)
    {
        _failed = true;
        _warn?.Invoke(text + "; the overlay stays off");
    }

    public void SetVisibleInCapture(bool visible)
    {
        if (visible == _visibleInCapture)
            return;
        _visibleInCapture = visible;
        if (_window is null)
            return;

        _excluded = CaptureExclusion.Apply(Handle, _visibleInCapture);
        if (!_excluded && !_visibleInCapture)
            _warn?.Invoke($"The {_layer} overlay window could not be excluded from screen capture; it shows in screenshots and recordings");
    }

    public void Present(OverlayModel model, PixelRect bounds, AppSettings settings, string? toast = null)
    {
        if (_closed || _failed)
            return;

        var boundsChanged = !_hasBounds || bounds != _bounds;
        var contentChanged = boundsChanged || !SameContent(model) || settings != _settings || toast != _toast;
        var motionFrame = _motionFrame;
        _motionFrame = false;
        if (!contentChanged && IsShown && !_retryUpdate && !motionFrame)
            return;

        _bounds = bounds;
        _hasBounds = true;
        _model = model;
        _settings = settings;
        _toast = toast;

        var showsPanel = _layer is OverlayLayer.All or OverlayLayer.Panel;
        var now = (long)StopwatchClock.Shared.NowMs;
        var entries = _motion?.Entries(model.Panel, now) ?? model.Panel;
        var layout = showsPanel ? PanelLayout.Compute(entries, bounds.Width, bounds.Height, settings, model.Note, toast) : PanelLayoutResult.Empty;

        _painter.Motion = _motion;
        _painter.NowMs = now;
        _painter.MiningScale = settings.MiningLabelZoomPercent / 100.0;
        _painter.Accent = settings.UiAccentColor;
        var fit = Fit(model, layout, bounds);
        if (fit.IsEmpty)
        {
            HideWindow();
            return;
        }

        var fitChanged = fit != _fit;
        _fit = fit;
        if (!EnsureWindow())
            return;

        _painter.SetContent(model, layout, fit, bounds.Height);
        _retryUpdate = false;
        if (!Update())
        {
            _retryUpdate = true;
            _updateFailures++;
            if (_updateFailures >= MaxFailuresShown && IsShown)
            {
                _warn?.Invoke($"The {_layer} overlay could not be updated {_updateFailures} times in a row; it is hidden until it works again");
                HideOverlay();
            }

            return;
        }

        _updateFailures = 0;

        var window = _window!;
        var firstShow = !window.IsShown;
        if (firstShow)
            window.Show();
        if (firstShow || boundsChanged || fitChanged)
            window.AssertTopmost();
        KeepMoving(now);
    }

    private void KeepMoving(long now)
    {
        if (_motion is null || !_motion.IsMoving(now))
        {
            _motionTimer?.Stop();
            return;
        }
        if (_motionTimer is null)
        {
            _motionTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _motionTimer.Tick += (_, _) =>
            {
                if (_closed || _model is null || _settings is null || !IsShown)
                {
                    _motionTimer?.Stop();
                    return;
                }
                _motionFrame = true;
                Present(_model, _bounds, _settings, _toast);
            };
        }
        if (!_motionTimer.IsEnabled)
            _motionTimer.Start();
    }

    public void HideOverlay()
    {
        _motion?.Settle();
        HideWindow();
    }

    private void HideWindow()
    {
        _motionTimer?.Stop();
        _window?.Hide();
        _retryUpdate = false;
        _updateFailures = 0;
    }

    private bool Update()
    {
        var window = _window!;
        var width = (int)Math.Round(_fit.Width);
        var height = (int)Math.Round(_fit.Height);
        if (width <= 0 || height <= 0)
            return false;

        var surfaceWidth = Bucket(width);
        var surfaceHeight = Bucket(height);
        var bits = window.BeginSurface(surfaceWidth, surfaceHeight);
        if (bits == IntPtr.Zero)
        {
            if (!_updateFailedLogged)
            {
                _updateFailedLogged = true;
                _warn?.Invoke($"The {_layer} overlay window's bitmap could not be made ({surfaceWidth}x{surfaceHeight}); it is tried again at the next change");
            }

            return false;
        }

        try
        {
            if (_visual is null || _target is null || _targetWidth != surfaceWidth || _targetHeight != surfaceHeight)
            {
                _visual ??= new DrawingVisual();
                _target = new RenderTargetBitmap(surfaceWidth, surfaceHeight, 96.0, 96.0, PixelFormats.Pbgra32);
                _targetWidth = surfaceWidth;
                _targetHeight = surfaceHeight;
            }

            _target.Clear();
            using (var context = _visual.RenderOpen())
                _painter.Paint(context);
            _target.Render(_visual);
            if (!DrawCurved(bits, window.Stride, width, height))
                _target.CopyPixels(new Int32Rect(0, 0, width, height), bits, window.Stride * surfaceHeight, window.Stride);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException or COMException)
        {
            if (!_updateFailedLogged)
            {
                _updateFailedLogged = true;
                _warn?.Invoke($"The {_layer} overlay could not be drawn ({ex.GetType().Name}: {ex.Message}); it is tried again at the next change");
            }

            _target = null;
            return false;
        }

        var x = (int)Math.Round(_bounds.X + _fit.X);
        var y = (int)Math.Round(_bounds.Y + _fit.Y);
        if (window.Commit(x, y, width, height))
            return true;
        if (!_updateFailedLogged)
        {
            _updateFailedLogged = true;
            _warn?.Invoke($"Windows refused to update the {_layer} overlay window (UpdateLayeredWindow failed); it is tried again at the next change");
        }

        return false;
    }

    private bool DrawCurved(IntPtr bits, int stride, int width, int height)
    {
        var depth = CurveDepth();
        if (depth <= 0 || _curveFailed)
            return false;
        try
        {
            (_curve ??= new PanelCurve()).Draw(_target!, bits, stride, width, height, _fit, _bounds.Width, _bounds.Height, depth);
            return true;
        }
        catch (Exception ex) when (ex is OpenCvSharp.OpenCVException or OpenCvSharp.OpenCvSharpException or DllNotFoundException or ArgumentException)
        {
            _curveFailed = true;
            _warn?.Invoke($"The {_layer} overlay could not be bent onto the curved glass ({ex.GetType().Name}: {ex.Message}); it is drawn flat");
            return false;
        }
    }

    private double CurveDepth() => _layer == OverlayLayer.Panel && _settings is { } settings ? ScreenCurve.DepthFor(settings.UiCurvePercent) : 0;

    private static int Bucket(int size) => (size + LiveSettings.SurfaceBucketPx - 1) / LiveSettings.SurfaceBucketPx * LiveSettings.SurfaceBucketPx;

    private bool SameContent(OverlayModel model) =>
        _model is not null && _layer switch
        {
            OverlayLayer.Panel => ReferenceEquals(model.Panel, _model.Panel) && model.Note == _model.Note,
            OverlayLayer.Mining => Equals(model.Mining, _model.Mining),
            _ => ReferenceEquals(model, _model),
        };

    private PixelRect Fit(OverlayModel model, PanelLayoutResult layout, PixelRect bounds)
    {
        var whole = new PixelRect(0, 0, bounds.Width, bounds.Height);
        if (_layer == OverlayLayer.All)
            return model.IsEmpty && layout.IsEmpty ? default : whole;

        var parts = new List<PixelRect>();
        if (_layer == OverlayLayer.Mining)
        {
            if (model.Mining is not { } label)
                return default;
            var box = OverlayPainter.MiningLayout(label, _painter.MiningScale).Box;
            return OverlayWindowFit.Choose(_fit.IsEmpty ? null : _fit, new PixelRect(box.X, box.Y, box.Width, box.Height), whole, 4.0);
        }

        if (!layout.Frame.IsEmpty)
            parts.Add(OverlayWindowFit.Inflate(layout.Frame, PanelLook.HaloPx * PanelLook.Scale(bounds.Height) + ScreenCurve.MaxShiftPx(bounds.Width, CurveDepth())));

        var needed = OverlayWindowFit.Union(parts);
        if (needed is null)
            return default;
        return OverlayWindowFit.Choose(_fit.IsEmpty ? null : _fit, needed.Value, whole, 4.0);
    }
}
