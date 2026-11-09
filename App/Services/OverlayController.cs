using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;
using Iris.Core.Config;

namespace Iris.App.Services;

public sealed record CaptureGate(bool Active, PixelRect Bounds, IntPtr Handle = default)
{
    public static readonly CaptureGate Off = new(false, default);
}

public sealed partial class OverlayController : IDisposable
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
    private OverlayWindow? _miningWindow;
    private OverlayModel _liveModel = OverlayModel.Empty;
    private MarkerIndex? _index;
    private PipelineSettings _pipeSettings = PipelineSettings.Default;
    private LiveSettings _liveSettings = LiveSettings.Default;
    private int _evaluatePending;
    private long _pickedVersion;
    private ITraceSink? _trace;
    private long _modelNumber;
    private double _modelFrameMs = double.NaN;
    private volatile CaptureGate _gate = CaptureGate.Off;
    private bool _disposed;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(ToastSeconds) };
    private string? _toastText;

    private readonly TimedOverlayRun _timedRun = new();
    private readonly DispatcherTimer _timedTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _timedShownSeconds = -1;

    private OverlayModel? _testModel;
    private PixelRect _testBounds;
    private AppSettings? _testSettings;
    private long _testEndTick;

    public OverlayController(AppServices services, ImportCoordinator imports)
    {
        _services = services;
        _imports = imports;
        _tracker = new GameWindowTracker(services.Log, services.Clock, () => !services.IsOverlayDisabled);
        _tracker.Changed += OnGameWindowChanged;
        _tracker.Checked += OnGameWindowChecked;
        _testTimer.Tick += OnTestTick;
        _toastTimer.Tick += OnToastTick;
        _timedTimer.Tick += OnTimedTick;
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

    public TestOverlayPart TestPart { get; private set; }

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

    public event EventHandler? InteractiveRequested;

    public GameWindowSnapshot GameWindow => _tracker.Current;

    public MarkerIndex? LiveIndex => _index;

    public void ShowNotice(string text)
    {
        if (_disposed || _tracker.Current.State != GameWindowState.Ready)
            return;
        ShowToast(text);
        Evaluate();
    }

    public void Start()
    {
        Hotkeys.Apply(_services.Settings);
        _tracker.Start();
    }

    public void AttachLive(MarkerIndex index, PipelineSettings pipe, LiveSettings live, ITraceSink? trace = null)
    {
        _trace = trace;
        _index = index;
        _pipeSettings = pipe;
        _liveSettings = live;
    }

    public void LiveChanged() => Evaluate();

    public void LogRenderSummary() => _bracketRenderer?.LogRenderSummary();

    public double TakeRenderCpuMs() => _bracketRenderer?.TakeCpuMs() ?? 0.0;

    public bool BracketsShown => (_bracketRenderer?.IsActive ?? false) && _bracketsWanted;

    private volatile bool _bracketsWanted;

    private OverlayModel ModelAtNow(OverlayModel? previous, double refreshPeriodMs, bool renderLoop = false)
    {
        var index = _index;
        if (index is null)
            return OverlayModel.Empty;
        var snapshot = index.Current;
        var nowMs = StopwatchClock.Shared.NowMs;
        if (renderLoop)
            NotePickup(index, snapshot, nowMs);
        return IndexModel.ModelAt(snapshot, nowMs + refreshPeriodMs, _pipeSettings, _liveSettings, previous, _services.Settings.TrackingLossDisplaySeconds * 1000.0);
    }

    private void TraceModel(OverlayModel model, double refreshPeriodMs)
    {
        _modelNumber++;
        var index = _index;
        if (index is null)
            return;
        var snapshot = index.Current;
        _modelFrameMs = snapshot.TrackedFrameMs;
        var nowMs = StopwatchClock.Shared.NowMs;
        if (_trace.Line(nowMs, "rnd", "draw") is not { } t)
            return;
        var at = nowMs + refreshPeriodMs;
        t.Int("r", _modelNumber).Num("at", at).Num("period", refreshPeriodMs).Int("ver", snapshot.Version).Num("tracked", snapshot.TrackedFrameMs).Num("interval", snapshot.NominalFrameIntervalMs)
            .Str("note", model.Note).Int("panel", model.Panel.Count).Array("brackets");
        foreach (var item in model.Brackets)
            t.Item().Int("mid", item.MarkerId).Box("b", item.Box);
        t.EndArray().Array("markers");
        var withheld = IndexModel.WithheldDuplicates(snapshot.Markers);
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status != PlateStatus.Confirmed)
                continue;
            var (cx, top) = IndexModel.PositionAt(marker, snapshot, at, _pipeSettings, _liveSettings);
            var state = !IndexModel.IsListed(marker) ? "notListed" : !marker.HasTrackingFix ? "noFix" : !IndexModel.IsDrawable(marker, at, _pipeSettings) ? "stale"
                : (withheld is not null && withheld.Contains(marker.Id)) ? "duplicateName" : "drawnOrOutside";
            t.Item().Int("mid", marker.Id).Str("name", marker.Name).Str("state", state).Num("age", at - marker.LastFoundMs, 1).Int("missed", marker.MissedFrames).Num("lineMs", marker.LineMs)
                .Num("shiftX", cx - marker.CentreX, 1).Num("shiftY", top - marker.Top, 1).Str("rsi", marker.Identity?.Rsi.ToString());
        }

        _trace!.Write(t.EndArray().Text());
    }

    private string StampText() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"R{_modelNumber} F{(double.IsFinite(_modelFrameMs) ? _modelFrameMs : -1):0} T{StopwatchClock.Shared.NowMs:0}");

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
        if (!ReferenceEquals(model, shown))
            TraceModel(model, refreshPeriodMs);
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

    public void ToggleFromTray() => ToggleOverlay(announce: false);

    private void OnHotkeyPressed(object? sender, HotkeyAction action)
    {
        if (action == HotkeyAction.InteractiveOverlay)
        {
            InteractiveRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (action == HotkeyAction.TimedOverlay)
            PressTimedOverlay();
        else if (action == HotkeyAction.ToggleOverlay)
            ToggleOverlay(announce: true);
    }

    private void ToggleOverlay(bool announce)
    {
        _toastText = null;
        _toastTimer.Stop();

        var onScreen = announce && _testModel is null && _tracker.Current.State == GameWindowState.Ready;
        var disable = !_services.Settings.OverlayDisabled;
        var text = disable ? "Overlay OFF" : "Overlay ON";
        if (onScreen)
            ShowToast(text);
        var saved = _services.SetOverlayDisabled(disable);
        if (!saved.Success)
        {
            ClearToast();
            _services.Log.Write(_services.Clock.UtcNow, "Could not save the setting: " + saved.Error);
            return;
        }
        if (!disable)
            EndTimedRun(expired: false);

        Evaluate();
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
        PromptDialog.ShowNotice(BorderlessNoticeTracker.Text);
    }

    public void CloseBorderlessNotice() => PromptDialog.CloseNotice();

    public GameWindowSnapshot CheckGameWindowNow() => _tracker.CheckWithModeNow();

    public void ShowBorderlessNoticeNow(WindowMode? gameMode)
    {
        if (_disposed || _imports.IsQuitting || PromptDialog.IsAnyOpen)
            return;
        ShowBorderlessNotice(gameMode);
    }

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
        var visibleInCapture = _services.Settings.OverlayShownInCapture;
        foreach (var window in new[] { _testWindow, _panelWindow, _miningWindow })
            window?.SetVisibleInCapture(visibleInCapture);
        _bracketRenderer?.SetVisibleInCapture(visibleInCapture);
        if (_timedRun.IsActive && !_services.Settings.OverlayDisabled)
            EndTimedRun(expired: false);
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
            _miningWindow?.HideOverlay();
            EnsureTestWindow().Present(_testModel, _testBounds, _testSettings);
            return;
        }

        _testWindow?.HideOverlay();

        var snapshot = _tracker.Current;
        _liveModel = ModelAtNow(_liveModel, DisplayTiming.RefreshPeriodMs(snapshot.Handle));
        var settings = _services.Settings;
        var ready = snapshot.State == GameWindowState.Ready;
        var disabled = _services.IsOverlayDisabled;
        SetGate(ready && !disabled ? new CaptureGate(true, snapshot.Bounds, snapshot.Handle) : CaptureGate.Off);
        var toast = _toastText ?? TimedOverlayText();

        if (!ready)
        {
            HideLive();
            return;
        }

        if (disabled)
        {
            _bracketRenderer?.Hide();
            _miningWindow?.HideOverlay();
            if (toast is null)
                _panelWindow?.HideOverlay();
            else
                EnsurePanelWindow().Present(OverlayModel.Empty, snapshot.Bounds, settings, toast);
            return;
        }

        var liveModel = _liveModel;
        _bracketsWanted = liveModel.Brackets.Count > 0;
        if (liveModel.Brackets.Count > 0 || _trace is not null)
            EnsureBracketRenderer().Show(snapshot.Bounds, snapshot.Handle, liveModel);
        else
            _bracketRenderer?.Hide();

        if (toast is not null || liveModel.Note is not null || liveModel.Panel.Count > 0)
            EnsurePanelWindow().Present(WithNotes(liveModel), snapshot.Bounds, settings, toast);
        else
            _panelWindow?.HideOverlay();

        if (liveModel.Mining is not null)
            EnsureMiningWindow().Present(liveModel, snapshot.Bounds, settings);
        else
            _miningWindow?.HideOverlay();
    }

    private OverlayModel WithNotes(OverlayModel model)
    {
        if (model.Panel.Count == 0)
            return model;
        var notes = _services.Notes;
        PanelEntry[]? changed = null;
        for (int i = 0; i < model.Panel.Count; i++)
        {
            var entry = model.Panel[i];
            var note = notes.PanelText(entry.Handle);
            if (note == entry.Note)
                continue;
            changed ??= System.Linq.Enumerable.ToArray(model.Panel);
            changed[i] = entry with { Note = note };
        }
        return changed is null ? model : new OverlayModel(model.Brackets, changed, model.Boundary, model.Note, model.Mining);
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
        _miningWindow?.HideOverlay();
    }

    private void WarnWindow(string text) => _services.Log.Write(_services.Clock.UtcNow, text);

    private OverlayWindow EnsureTestWindow() => _testWindow ??= new OverlayWindow(OverlayLayer.All, WarnWindow, _services.Settings.OverlayShownInCapture);

    private BracketRenderer EnsureBracketRenderer() =>
        _bracketRenderer ??= new BracketRenderer(
            RenderModel, WarnWindow, _services.Settings.OverlayShownInCapture, _services.Milestones.BracketDrawn, _trace is null ? null : new Func<string>(StampText));

    private OverlayWindow EnsurePanelWindow() => _panelWindow ??= new OverlayWindow(OverlayLayer.Panel, WarnWindow, _services.Settings.OverlayShownInCapture);

    private OverlayWindow EnsureMiningWindow() => _miningWindow ??= new OverlayWindow(OverlayLayer.Mining, WarnWindow, _services.Settings.OverlayShownInCapture);

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
        _timedTimer.Stop();
        _timedTimer.Tick -= OnTimedTick;
        _tracker.Changed -= OnGameWindowChanged;
        _tracker.Checked -= OnGameWindowChecked;
        PromptDialog.CloseNotice();
        _tracker.Dispose();
        _testTimer.Stop();
        _testTimer.Tick -= OnTestTick;
        foreach (var window in new[] { _testWindow, _panelWindow, _miningWindow })
            window?.Close();
        _testWindow = null;
        _panelWindow = null;
        _miningWindow = null;
        var renderer = _bracketRenderer;
        _bracketRenderer = null;
        renderer?.Dispose();
    }
}
