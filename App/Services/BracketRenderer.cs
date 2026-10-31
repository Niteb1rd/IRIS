using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;

namespace Iris.App.Services;

internal sealed class BracketRenderer : IDisposable
{
    private const int MaxFailuresShown = 3;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private sealed record Target(bool Active, PixelRect Bounds, IntPtr GameWindow, OverlayModel? Model, bool VisibleInCapture);

    private readonly Func<OverlayModel, double, OverlayModel> _modelProvider;
    private readonly Action<string> _warn;
    private readonly AutoResetEvent _wake = new(false);
    private readonly RenderStats? _stats = AppVersion.Diagnostics ? new(StopwatchClock.Shared.NowMs) : null;
    private readonly BracketRasterizer _rasterizer = new();
    private readonly object _cpuGate = new();
    private Thread? _thread;
    private volatile Target _target;
    private volatile bool _stop;
    private ThreadCpuMeter? _cpu;

    private LayeredWindow? _window;
    private bool _failed;
    private bool _visibleInCapture;
    private bool _excluded = true;
    private bool _updateFailedLogged;
    private bool _retryUpdate;
    private int _updateFailures;
    private long _retryNotBeforeTick;
    private bool _dirty;
    private PixelRect _bounds;
    private PixelRect _fit;
    private OverlayModel _model = OverlayModel.Empty;
    private PixelRect _shownBounds;
    private byte[] _pixels = Array.Empty<byte>();
    private readonly List<PixelRect> _parts = new();
    private string? _loopProblem;

    public BracketRenderer(Func<OverlayModel, double, OverlayModel> modelProvider, Action<string> warn, bool visibleInCapture)
    {
        _modelProvider = modelProvider;
        _warn = warn;
        _visibleInCapture = visibleInCapture;
        _target = new Target(false, default, IntPtr.Zero, null, visibleInCapture);
    }

    public void Show(PixelRect bounds, IntPtr gameWindow, OverlayModel model)
    {
        if (_stop)
            return;
        var current = _target;
        if (current.Active && current.Bounds == bounds && current.GameWindow == gameWindow)
            return;
        _target = current with { Active = true, Bounds = bounds, GameWindow = gameWindow, Model = model };
        EnsureThread();
        Wake();
    }

    public bool IsActive => _target.Active;

    public void Hide()
    {
        var current = _target;
        if (!current.Active)
            return;
        _target = current with { Active = false, Model = null };
        Wake();
    }

    public void SetVisibleInCapture(bool visible)
    {
        var current = _target;
        if (current.VisibleInCapture == visible)
            return;
        _target = current with { VisibleInCapture = visible };
        Wake();
    }

    public void LogRenderSummary()
    {
        if (_stats is null)
            return;
        var line = _stats.TakeSummary(StopwatchClock.Shared.NowMs);
        if (_stats.LastSummaryFrames > 0)
            _warn(line);
    }

    public double TakeCpuMs()
    {
        lock (_cpuGate)
            return _cpu?.TakeDeltaMs() ?? 0.0;
    }

    public void Dispose()
    {
        if (_stop)
            return;
        _stop = true;
        Wake();
        var thread = _thread;
        if (thread is not null && !thread.Join(JoinTimeout))
        {
            _warn("The brackets render thread did not end within 2 s of shutdown; it is a background thread and ends with the process");
            return;
        }

        _wake.Dispose();
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EnsureThread()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Run) { Name = "IRIS render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var meter = AppVersion.Diagnostics ? ThreadCpuMeter.ForCurrentThread() : null;
        lock (_cpuGate)
            _cpu = meter;

        var clock = new VBlankClock(ms => _wake.WaitOne(ms), _wake);
        Target? applied = null;
        try
        {
            while (!_stop)
            {
                try
                {
                    Pump();
                    var target = _target;
                    if (!ReferenceEquals(target, applied))
                    {
                        Apply(target, applied);
                        applied = target;
                    }

                    if (!target.Active || _failed)
                    {
                        _wake.WaitOne(LiveSettings.RenderIdleWaitMs);
                        continue;
                    }

                    var period = DisplayTiming.RefreshPeriodMs(target.GameWindow);
                    var started = Stopwatch.GetTimestamp();
                    var model = _modelProvider(_model, period);
                    var changed = _dirty || !ReferenceEquals(model, _model);
                    var redraw = changed || (_retryUpdate && Environment.TickCount64 >= _retryNotBeforeTick);
                    if (redraw)
                        Present(model);
                    _stats?.AddFrame((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, redraw);

                    clock.WaitNextFrame(target.GameWindow, period);
                    if (clock.LastWaitLate)
                        _stats?.AddLateWait();
                    _stats?.SetClock(clock.Kind, 1000.0 / period);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var text = ex.GetType().Name + ": " + ex.Message;
                    if (text != _loopProblem)
                    {
                        _loopProblem = text;
                        _warn("The brackets render thread had a problem (" + text + "); it goes on");
                    }

                    _wake.WaitOne(50);
                }
            }
        }
        finally
        {
            try
            {
                DestroyWindow();
            }
            catch (Exception ex)
            {
                _warn("The brackets overlay window could not be closed cleanly (" + ex.Message + ")");
            }

            clock.Dispose();
            lock (_cpuGate)
            {
                _cpu = null;
                meter?.Dispose();
            }
        }
    }

    private static void Pump()
    {
        while (NativeMethods.PeekMessage(out var message, IntPtr.Zero, 0, 0, NativeMethods.PmRemove))
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }

    private void Apply(Target target, Target? previous)
    {
        if (target.VisibleInCapture != _visibleInCapture)
        {
            _visibleInCapture = target.VisibleInCapture;
            if (_window is not null)
            {
                _excluded = CaptureExclusion.Apply(_window.Handle, _visibleInCapture);
                if (!_excluded && !_visibleInCapture)
                    _warn("The Brackets overlay window could not be excluded from screen capture; it shows in screenshots and recordings");
            }
        }

        if (!target.Active)
        {
            HideWindow();
            return;
        }

        if (previous is null || !previous.Active || previous.Bounds != target.Bounds || previous.GameWindow != target.GameWindow)
        {
            _bounds = target.Bounds;
            if (previous is null || !previous.Active)
                _model = target.Model ?? OverlayModel.Empty;
            _dirty = true;
        }
    }

    private bool EnsureWindow()
    {
        if (_window is not null)
            return true;
        if (_failed || _stop)
            return false;

        try
        {
            _window = new LayeredWindow();
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _failed = true;
            _warn($"The Brackets overlay window could not be created ({ex.Message}); the overlay stays off");
            return false;
        }

        _excluded = CaptureExclusion.Apply(_window.Handle, _visibleInCapture);
        if (!_excluded && !_visibleInCapture)
            _warn("The Brackets overlay window could not be excluded from screen capture; it shows in screenshots and recordings");
        return true;
    }

    private void DestroyWindow()
    {
        var window = _window;
        _window = null;
        if (window is null)
            return;
        window.Hide();
        window.Dispose();
    }

    private void HideWindow()
    {
        _window?.Hide();
        _retryUpdate = false;
        _updateFailures = 0;
        _dirty = false;
    }

    private void Present(OverlayModel model)
    {
        var bounds = _bounds;
        _model = model;
        _dirty = false;

        var fit = Fit(model, bounds);
        if (fit.IsEmpty)
        {
            HideWindow();
            return;
        }

        var fitChanged = fit != _fit;
        _fit = fit;
        if (!EnsureWindow())
            return;

        var boundsChanged = bounds != _shownBounds;
        _retryUpdate = false;
        if (!Update(model, bounds, fit))
        {
            _retryUpdate = true;
            _retryNotBeforeTick = Environment.TickCount64 + (IsShown ? 0 : LiveSettings.RenderRetryDelayMs);
            _updateFailures++;
            if (_updateFailures >= MaxFailuresShown && IsShown)
            {
                _warn($"The Brackets overlay could not be updated {_updateFailures} times in a row; it is hidden until it works again");
                _window!.Hide();
                _retryNotBeforeTick = Environment.TickCount64 + LiveSettings.RenderRetryDelayMs;
            }

            return;
        }

        _updateFailures = 0;
        _shownBounds = bounds;

        var window = _window!;
        var firstShow = !window.IsShown;
        if (firstShow)
            window.Show();
        if (firstShow || boundsChanged || fitChanged)
            window.AssertTopmost();
    }

    private bool IsShown => _window is { IsShown: true };

    private bool Update(OverlayModel model, PixelRect bounds, PixelRect fit)
    {
        var window = _window!;
        var width = (int)Math.Round(fit.Width);
        var height = (int)Math.Round(fit.Height);
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
                _warn($"The Brackets overlay window's bitmap could not be made ({surfaceWidth}x{surfaceHeight}); it is tried again at the next change");
            }

            return false;
        }

        var stride = window.Stride;
        var needed = stride * surfaceHeight;
        if (_pixels.Length != needed)
            _pixels = new byte[needed];
        else
            Array.Clear(_pixels, 0, stride * height);

        _rasterizer.Draw(_pixels, stride, width, height, model.Brackets, fit.X, fit.Y, bounds.Height);
        Marshal.Copy(_pixels, 0, bits, stride * height);

        var x = (int)Math.Round(bounds.X + fit.X);
        var y = (int)Math.Round(bounds.Y + fit.Y);
        if (window.Commit(x, y, width, height))
            return true;
        if (!_updateFailedLogged)
        {
            _updateFailedLogged = true;
            _warn("Windows refused to update the Brackets overlay window (UpdateLayeredWindow failed); it is tried again at the next change");
        }

        return false;
    }

    private static int Bucket(int size) => (size + LiveSettings.SurfaceBucketPx - 1) / LiveSettings.SurfaceBucketPx * LiveSettings.SurfaceBucketPx;

    private PixelRect Fit(OverlayModel model, PixelRect bounds)
    {
        _parts.Clear();
        foreach (var item in model.Brackets)
            _parts.Add(BracketMetrics.ExtentOf(item.Box, bounds.Height));
        var needed = OverlayWindowFit.Union(_parts);
        if (needed is null)
            return default;
        var whole = new PixelRect(0, 0, bounds.Width, bounds.Height);
        return OverlayWindowFit.Choose(_fit.IsEmpty ? null : _fit, needed.Value, whole, LiveSettings.RenderRoomAroundPx);
    }
}
