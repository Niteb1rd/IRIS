using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;
using Iris.Core.Config;

namespace Iris.App.Services;

public sealed record CaptureGate(bool Active, PixelRect Bounds, IntPtr Handle = default)
{
    public static readonly CaptureGate Off = new(false, default);
}

public sealed class OverlayController : IDisposable
{
    public const int TestSeconds = 15;
    public const double ToastSeconds = 2;

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly GameWindowTracker _tracker;
    private readonly BorderlessNoticeTracker _notice = new();
    private readonly DispatcherTimer _testTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private OverlayWindow? _testWindow;
    private volatile BracketRenderer? _bracketRenderer;
    private OverlayWindow? _panelWindow;
    private OverlayModel _liveModel = OverlayModel.Empty;
    private MarkerIndex? _index;
    private PipelineSettings _pipeSettings = PipelineSettings.Default;
    private LiveSettings _liveSettings = LiveSettings.Default;
    private int _evaluatePending;
    private long _pickedVersion;
    private volatile CaptureGate _gate = CaptureGate.Off;
    private bool _disposed;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(ToastSeconds) };
    private string? _toastText;

    private OverlayModel? _testModel;
    private PixelRect _testBounds;
    private AppSettings? _testSettings;
    private long _testEndTick;

    public OverlayController(AppServices services, ImportCoordinator imports)
    {
        _services = services;
        _imports = imports;
        _tracker = new GameWindowTracker(services.Log, services.Clock);
        _tracker.Changed += OnGameWindowChanged;
        _tracker.Checked += OnGameWindowChecked;
        _testTimer.Tick += OnTestTick;
        _toastTimer.Tick += OnToastTick;
        services.SettingsApplied += OnSettingsApplied;
        Hotkeys = new HotkeyManager(services.Log, services.Clock);
        Hotkeys.Pressed += OnHotkeyPressed;
    }

    public HotkeyManager Hotkeys { get; }

    public event EventHandler? TestStateChanged;

    public bool IsOverlayDisabled => _services.IsOverlayDisabled;

    public event EventHandler OverlayDisabledChanged
    {
        add => _services.OverlayDisabledChanged += value;
        remove => _services.OverlayDisabledChanged -= value;
    }

    public bool IsTestRunning => _testModel is not null;

    public int TestSecondsLeft
    {
        get
        {
            if (_testModel is null)
                return 0;
            var left = _testEndTick - Environment.TickCount64;
            return left <= 0 ? 0 : (int)((left + 999) / 1000);
        }
    }

    public CaptureGate Gate => _gate;

    public event EventHandler? GateChanged;

    public void Start()
    {
        Hotkeys.Apply(_services.Settings);
        _tracker.Start();
    }

    public void AttachLive(MarkerIndex index, PipelineSettings pipe, LiveSettings live)
    {
        _index = index;
        _pipeSettings = pipe;
        _liveSettings = live;
    }

    public void LiveChanged() => Evaluate();

    public void LogRenderSummary() => _bracketRenderer?.LogRenderSummary();

    public double TakeRenderCpuMs() => _bracketRenderer?.TakeCpuMs() ?? 0.0;

    public bool BracketsShown => _bracketRenderer?.IsActive ?? false;

    private OverlayModel ModelAtNow(OverlayModel? previous, double refreshPeriodMs, bool renderLoop = false)
    {
        var index = _index;
        if (index is null)
            return OverlayModel.Empty;
        var snapshot = index.Current;
        var nowMs = StopwatchClock.Shared.NowMs;
        if (renderLoop)
            NotePickup(index, snapshot, nowMs);
        return IndexModel.ModelAt(snapshot, nowMs + refreshPeriodMs, _pipeSettings, _liveSettings, previous);
    }

    private void NotePickup(MarkerIndex index, IndexSnapshot snapshot, double nowMs)
    {
        var seen = Interlocked.Read(ref _pickedVersion);
        if (snapshot.Version <= seen || Interlocked.CompareExchange(ref _pickedVersion, snapshot.Version, seen) != seen)
            return;
        if (snapshot.CarriesTrackedFrame && double.IsFinite(snapshot.PublishedMs))
            index.Counters.AddRenderPickup(Math.Max(0.0, nowMs - snapshot.PublishedMs));
    }

    private OverlayModel RenderModel(OverlayModel shown, double refreshPeriodMs)
    {
        var model = ModelAtNow(shown, refreshPeriodMs, renderLoop: true);
        var previous = Interlocked.Exchange(ref _liveModel, model);
        if (ReferenceEquals(model, previous))
            return model;
        var emptied = model.Brackets.Count == 0 && previous.Brackets.Count > 0;
        if (emptied && Interlocked.Exchange(ref _evaluatePending, 1) == 0)
        {
            _testTimer.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                Interlocked.Exchange(ref _evaluatePending, 0);
                Evaluate();
            }));
        }

        return model;
    }

    public void ToggleFromTray(HotkeyAction action) => Toggle(action, announce: false);

    private void OnHotkeyPressed(object? sender, HotkeyAction action) => Toggle(action, announce: true);

    private void Toggle(HotkeyAction action, bool announce)
    {
        _toastText = null;
        _toastTimer.Stop();

        var settings = _services.Settings;
        string text;
        var onScreen = announce && _testModel is null && _tracker.Current.State == GameWindowState.Ready;

        if (action == HotkeyAction.ToggleOverlay)
        {
            var disable = !settings.OverlayDisabled;
            text = disable ? "Overlay off" : "Overlay on";
            if (onScreen)
                ShowToast(text);
            var saved = _services.SetOverlayDisabled(disable);
            if (!saved.Success)
            {
                ClearToast();
                _services.Notify?.Invoke("Could not save the setting: " + saved.Error);
                return;
            }
        }
        else
        {
            var enable = !settings.PanelEnabled;
            text = enable ? "Info panel on" : "Info panel off";
            if (onScreen)
                ShowToast(text);
            var saved = _services.SetPanelEnabled(enable);
            if (!saved.Success)
            {
                ClearToast();
                _services.Notify?.Invoke("Could not save the setting: " + saved.Error);
                return;
            }
        }

        if (announce && _toastText is null)
            _services.Notify?.Invoke(text);
        Evaluate();
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ClearToast()
    {
        _toastText = null;
        _toastTimer.Stop();
        Evaluate();
    }

    private void OnToastTick(object? sender, EventArgs e) => ClearToast();

    public string StartTest(AppSettings settings, Window anchor)
    {
        string where;
        if (_tracker.TryFindGameWindow(out var gameBounds))
        {
            _testBounds = gameBounds;
            where = "over the Star Citizen window";
        }
        else
        {
            var handle = new WindowInteropHelper(anchor).Handle;
            if (!NativeMethods.TryGetMonitorBounds(handle, out var monitorBounds))
                throw new InvalidOperationException("Windows did not report the size of the screen.");
            _testBounds = monitorBounds;
            where = "over this screen (Star Citizen is not running)";
        }

        _testModel = BuildTestModel(settings);
        _testSettings = settings;
        _testEndTick = Environment.TickCount64 + TestSeconds * 1000L;
        _testTimer.Start();
        _services.Log.Write(_services.Clock.UtcNow, $"Test overlay started {where} ({_testBounds.Width}x{_testBounds.Height})");

        Evaluate();
        TestStateChanged?.Invoke(this, EventArgs.Empty);
        return where;
    }

    public void StopTest()
    {
        if (_testModel is null)
            return;
        _testModel = null;
        _testSettings = null;
        _testTimer.Stop();
        _services.Log.Write(_services.Clock.UtcNow, "Test overlay ended");

        Evaluate();
        TestStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateTestSettings(AppSettings settings)
    {
        if (_testModel is null)
            return;
        _testSettings = settings;
        _testModel = BuildTestModel(settings);
        Evaluate();
    }

    private OverlayModel BuildTestModel(AppSettings settings) =>
        TestOverlayBuilder.Build(_services.CurrentConfig, _testBounds.Width, _testBounds.Height, TrackingBoundary.From(settings));

    private void OnTestTick(object? sender, EventArgs e)
    {
        if (TestSecondsLeft <= 0)
            StopTest();
        else
            TestStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGameWindowChanged(object? sender, GameWindowSnapshot snapshot) => Evaluate();

    private void OnGameWindowChecked(object? sender, GameWindowSnapshot snapshot)
    {
        if (_disposed || _imports.IsQuitting)
            return;
        if (_notice.ShouldShow(snapshot.State, _services.IsOverlayDisabled, PromptDialog.IsAnyOpen, snapshot.Mode))
            ShowBorderlessNotice(snapshot.Mode);
    }

    private void ShowBorderlessNotice(WindowMode? gameMode)
    {
        _notice.MarkShown();
        _services.Log.Write(_services.Clock.UtcNow, "Told the user that Star Citizen is not in borderless windowed mode (game display mode: " + (gameMode?.ToString() ?? "unknown") + ")");
        PromptDialog.ShowNotice(BorderlessNoticeTracker.TextFor(gameMode));
    }

    public void CloseBorderlessNotice() => PromptDialog.CloseNotice();

    private void OnSettingsApplied(object? sender, EventArgs e)
    {
        var dispatcher = _testTimer.Dispatcher;
        if (dispatcher.CheckAccess())
            ApplySettingsChange();
        else
            dispatcher.BeginInvoke(new Action(ApplySettingsChange));
    }

    private void ApplySettingsChange()
    {
        if (_disposed)
            return;
        Hotkeys.Apply(_services.Settings);
        var visibleInCapture = _services.Settings.OverlayVisibleInCapture;
        foreach (var window in new[] { _testWindow, _panelWindow })
            window?.SetVisibleInCapture(visibleInCapture);
        _bracketRenderer?.SetVisibleInCapture(visibleInCapture);
        if (_testSettings is not null)
            _testSettings = _testSettings with { PanelEnabled = _services.Settings.PanelEnabled };
        Evaluate();
    }

    private void Evaluate()
    {
        if (_disposed)
            return;

        if (_testModel is not null && _testSettings is not null)
        {
            SetGate(CaptureGate.Off);
            _bracketRenderer?.Hide();
            _panelWindow?.HideOverlay();
            EnsureTestWindow().Present(_testModel, _testBounds, _testSettings);
            return;
        }

        _testWindow?.HideOverlay();

        var snapshot = _tracker.Current;
        _liveModel = ModelAtNow(_liveModel, DisplayTiming.RefreshPeriodMs(snapshot.Handle));
        var settings = _services.Settings;
        var ready = snapshot.State == GameWindowState.Ready;
        SetGate(ready && !settings.OverlayDisabled ? new CaptureGate(true, snapshot.Bounds, snapshot.Handle) : CaptureGate.Off);

        if (!ready)
        {
            HideLive();
            return;
        }

        if (settings.OverlayDisabled)
        {
            _bracketRenderer?.Hide();
            if (_toastText is null)
                _panelWindow?.HideOverlay();
            else
                EnsurePanelWindow().Present(OverlayModel.Empty, snapshot.Bounds, settings, _toastText);
            return;
        }

        var liveModel = _liveModel;
        if (liveModel.Brackets.Count > 0)
            EnsureBracketRenderer().Show(snapshot.Bounds, snapshot.Handle, liveModel);
        else
            _bracketRenderer?.Hide();

        if (_toastText is not null || (settings.PanelEnabled && (liveModel.Panel.Count > 0 || liveModel.Note is not null)))
            EnsurePanelWindow().Present(liveModel, snapshot.Bounds, settings, _toastText);
        else
            _panelWindow?.HideOverlay();
    }

    private void SetGate(CaptureGate gate)
    {
        if (gate == _gate)
            return;
        _gate = gate;
        GateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideLive()
    {
        _bracketRenderer?.Hide();
        _panelWindow?.HideOverlay();
    }

    private void WarnWindow(string text) => _services.Log.Write(_services.Clock.UtcNow, text);

    private OverlayWindow EnsureTestWindow() => _testWindow ??= new OverlayWindow(OverlayLayer.All, WarnWindow, _services.Settings.OverlayVisibleInCapture);

    private BracketRenderer EnsureBracketRenderer() =>
        _bracketRenderer ??= new BracketRenderer(RenderModel, WarnWindow, _services.Settings.OverlayVisibleInCapture);

    private OverlayWindow EnsurePanelWindow() => _panelWindow ??= new OverlayWindow(OverlayLayer.Panel, WarnWindow, _services.Settings.OverlayVisibleInCapture);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _services.SettingsApplied -= OnSettingsApplied;
        Hotkeys.Pressed -= OnHotkeyPressed;
        Hotkeys.Dispose();
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTick;
        _tracker.Changed -= OnGameWindowChanged;
        _tracker.Checked -= OnGameWindowChecked;
        PromptDialog.CloseNotice();
        _tracker.Dispose();
        _testTimer.Stop();
        _testTimer.Tick -= OnTestTick;
        foreach (var window in new[] { _testWindow, _panelWindow })
            window?.Close();
        _testWindow = null;
        _panelWindow = null;
        var renderer = _bracketRenderer;
        _bracketRenderer = null;
        renderer?.Dispose();
    }
}
