using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core;
using Iris.Core.Config;
using Iris.Core.Lookup;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Services;

public sealed partial class InteractiveOverlay : IInteractiveHost, IDisposable
{
    public const int ForegroundPollMs = 100;

    public bool IsOpening => _opening || (!_wasInFront && Environment.TickCount64 - _openedTick < ForegroundGraceMs);

    public const int ForegroundGraceMs = 500;

    public const string BusyText = "IRIS: Settings is busy, try again in a moment.";

    public const int TierWaitMs = 300;

    public const string NoGpuText = "IRIS: Interactive Overlay needs GPU drawing, which Windows does not offer right now.";

    private readonly AppServices _services;
    private readonly OverlayController _overlay;
    private readonly Func<ConfigWindow?> _settingsWindow;
    private readonly Func<bool> _importBusy;
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(ForegroundPollMs) };
    private readonly Dictionary<string, MarkerIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);

    private InteractiveWindow? _window;
    private IntPtr _gameHandle;
    private Iris.Core.Overlay.PixelRect _gameBounds;
    private IntPtr _windowHandle;
    private long _openedTick;
    private bool _wasInFront;
    private bool _opening;
    private bool _noGpuLogged;
    private bool _tierRetry;
    private bool _glassFailLogged;
    private SocialConfig? _identitiesFor;
    private bool _disposed;

    public InteractiveOverlay(AppServices services, OverlayController overlay, Func<ConfigWindow?> settingsWindow, Func<bool> importBusy)
    {
        _services = services;
        _overlay = overlay;
        _settingsWindow = settingsWindow;
        _importBusy = importBusy;
        _foregroundTimer.Tick += OnForegroundTick;
        overlay.InteractiveRequested += OnRequested;
        ScheduleWarmUp();
    }

    public const int WarmUpDelayMs = 2000;

    private void ScheduleWarmUp()
    {
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WarmUpDelayMs) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(WarmUp));
        };
        wait.Start();
    }

    private void WarmUp()
    {
        if (_disposed || _window is not null)
            return;
        try
        {
            var game = _overlay.GameWindow;
            var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 2560, 1440);
            var bounds = game.State == GameWindowState.Ready && game.Bounds.Width > 0 && game.Bounds.Height > 0
                ? game.Bounds
                : new Iris.Core.Overlay.PixelRect(0, 0, screen.Width, screen.Height);
            var start = Now();
            var cost = FirstUse.Start();
            InteractiveWindow.WarmUp(Settings, Config, bounds);
            if (ColourMenus.LoadLook() is { } problem)
                _services.Log.Write(_services.Clock.UtcNow, "Interactive Overlay: the dark right-click menu look could not be loaded (" + problem + "); the menus use WPF's own look");
            if (AppVersion.Diagnostics)
                _services.Log.Write(_services.Clock.UtcNow,
                    $"Interactive Overlay warm-up: {OpenTiming.Ms(Now() - start)} ms at {bounds.Width:0}x{bounds.Height:0}; {cost.Text()}");
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Interactive Overlay warm-up failed: " + ex.Message);
        }
    }

    public bool IsOpen => _window is not null;

    public AppSettings Settings => _services.Settings;

    public SocialConfig Config => _services.CurrentConfig;

    private void OnRequested(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        var timing = AppVersion.Diagnostics ? new OpenTiming(HotkeyManager.LastPressMs, Now()) : null;
        if (_window is { } open)
        {
            if (!open.IsTyping || !HotkeyText.TryParse(Settings.HotkeyInteractiveOverlay, out var key, out _) || key is null || !HotkeyText.EditsText(key))
                Exit(returnToGame: true);
            return;
        }
        var game = _overlay.GameWindow;
        if (game.State != GameWindowState.Ready)
            game = _overlay.CheckGameWindowNow();
        if (game.State == GameWindowState.NotBorderless)
        {
            _overlay.ShowBorderlessNoticeNow(game.Mode);
            return;
        }
        if (game.State != GameWindowState.Ready || game.Handle == IntPtr.Zero)
            return;
        if (!InteractiveWindow.CanDraw)
        {
            if (!_tierRetry)
            {
                _tierRetry = true;
                var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TierWaitMs) };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    OnRequested(this, EventArgs.Empty);
                };
                wait.Start();
                return;
            }
            _tierRetry = false;
            if (!_noGpuLogged)
            {
                _noGpuLogged = true;
                _services.Log.Write(_services.Clock.UtcNow, "Interactive Overlay not opened: WPF cannot draw with the graphics card (render tier 0)");
            }
            _overlay.ShowNotice(NoGpuText);
            return;
        }
        _tierRetry = false;

        if (_importBusy() || PromptDialog.IsAnyOpen)
        {
            _overlay.ShowNotice(BusyText);
            return;
        }
        if (_settingsWindow() is { } settings)
        {
            if (settings.HasUnsavedChanges(ignoreHostedUrl: false))
            {
                _overlay.ShowNotice("IRIS: Unsaved changes in Settings, please save and close first.");
                return;
            }
            if (settings.IsBusy)
            {
                _overlay.ShowNotice(BusyText);
                return;
            }
            settings.Close();
            if (_settingsWindow() is not null)
            {
                _overlay.ShowNotice(BusyText);
                return;
            }
        }

        _gameHandle = game.Handle;
        _gameBounds = game.Bounds;
        Open(timing);
    }

    private static double Now() => StopwatchClock.Shared.NowMs;

    private static TimeSpan ProcessorTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private sealed class OpenTiming(double pressMs, double uiMs)
    {
        public double ChecksDone, Built, Shown, Attached, Raised, Detached, Activated, SoundDone, Handed, Presented;
        public double FirstFrame = double.NaN, LastFrame, LongestGap, LongestGapAt, CpuMs, CpuSpanMs;
        public double LayoutBuilt, LayoutShown, UiJitBuilt, UiJitShown;
        public int CallsBuilt, CallsShown;
        public int Frames;
        public string RenderMode = "";
        public string InFront = "";
        public readonly List<double> FrameGaps = new();
        public string UiJobs = "";
        public string FirstUseText = "";

        public static string Ms(double ms) => ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        private double From => double.IsNaN(pressMs) ? uiMs : pressMs;

        public string Steps() =>
            "Interactive Overlay open timing (ms): key press to UI thread " + Ms(uiMs - From)
            + ", checks " + Ms(ChecksDone - uiMs)
            + ", window created and list built " + Ms(Built - ChecksDone)
            + ", rest of Show " + Ms(Shown - Built) + " (" + ShowParts() + ")"
            + ", joining the game's input " + Ms(Attached - Shown)
            + ", BringWindowToTop + SetForegroundWindow " + Ms(Raised - Attached)
            + ", leaving the game's input " + Ms(Detached - Raised)
            + ", Activate + Focus " + Ms(Activated - Detached)
            + ", sound and log " + Ms(SoundDone - Activated)
            + ", first frame handed to the render thread " + Ms(Handed - SoundDone)
            + ", first frame on screen " + Ms(Presented - Handed)
            + "; total " + Ms(Presented - From)
            + "; in front after the switch: " + InFront;

        private string ShowParts()
        {
            var layout = LayoutShown - LayoutBuilt;
            return $"layout {Ms(layout)} in {CallsShown - CallsBuilt} Measure/Arrange calls, other {Ms(Shown - Built - layout)}; "
                + $"compiling on the UI thread {Ms(UiJitShown - UiJitBuilt)}";
        }

        public static double UiJitMs() => System.Runtime.JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;

        public string FrameText() =>
            $"Interactive Overlay open timing: {Frames} frames in {Ms(LastFrame - FirstFrame)} ms after the first, longest gap {Ms(LongestGap)} ms at +{Ms(LongestGapAt)} ms; "
            + $"IRIS used {Ms(CpuMs)} ms of processor time in the {Ms(CpuSpanMs)} ms from the window's creation to here ({Ms(CpuMs / Math.Max(1, CpuSpanMs) * 100)} % of one core, all threads) "
            + $"(window drawn {RenderMode}, WPF render tier {System.Windows.Media.RenderCapability.Tier >> 16}); "
            + $"first frame gaps {string.Join(" ", FrameGaps.ConvertAll(Ms))} ms; UI thread jobs over {Ms(UiJobWatch.LongJobMs)} ms: {(UiJobs.Length == 0 ? "none" : UiJobs)}; {FirstUseText}";
    }

    private readonly record struct FirstUse(TimeSpan JitTime, long JitMethods, int Gen0, int Gen1, int Gen2, TimeSpan GcPause)
    {
        public static FirstUse Start() => new(System.Runtime.JitInfo.GetCompilationTime(), System.Runtime.JitInfo.GetCompiledMethodCount(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());

        public string Text()
        {
            var now = Start();
            return $"JIT {OpenTiming.Ms((now.JitTime - JitTime).TotalMilliseconds)} ms for {now.JitMethods - JitMethods} methods; "
                + $"GC {now.Gen0 - Gen0}/{now.Gen1 - Gen1}/{now.Gen2 - Gen2} (gen 0/1/2), paused {OpenTiming.Ms((now.GcPause - GcPause).TotalMilliseconds)} ms";
        }
    }

    private sealed class UiJobWatch
    {
        public const double LongJobMs = 8;
        public const int MaxJobs = 12;

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly Dictionary<DispatcherOperation, double> _started = new();
        private readonly List<string> _long = new();
        private readonly double _originMs = Now();

        public UiJobWatch()
        {
            _dispatcher.Hooks.OperationStarted += OnStarted;
            _dispatcher.Hooks.OperationCompleted += OnEnded;
        }

        public string Stop()
        {
            _dispatcher.Hooks.OperationStarted -= OnStarted;
            _dispatcher.Hooks.OperationCompleted -= OnEnded;
            return string.Join(", ", _long);
        }

        private void OnStarted(object? sender, DispatcherHookEventArgs e) => _started[e.Operation] = Now();

        private void OnEnded(object? sender, DispatcherHookEventArgs e)
        {
            if (!_started.Remove(e.Operation, out var startMs))
                return;
            var ms = Now() - startMs;
            if (ms > LongJobMs && _long.Count < MaxJobs)
                _long.Add($"{e.Operation.Priority} {OpenTiming.Ms(ms)} ms at +{OpenTiming.Ms(startMs - _originMs)}");
        }
    }

    private const int FrameGapsLogged = 15;

    private void WatchFrames(InteractiveWindow window, OpenTiming timing)
    {
        var handedSeen = 0;
        var done = false;
        var cpuStart = ProcessorTime();
        var wallStart = Now();
        var firstUse = FirstUse.Start();
        var jobs = new UiJobWatch();
        void Finish()
        {
            if (done)
                return;
            done = true;
            System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
            timing.CpuMs = (ProcessorTime() - cpuStart).TotalMilliseconds;
            timing.CpuSpanMs = Now() - wallStart;
            timing.UiJobs = jobs.Stop();
            timing.FirstUseText = firstUse.Text();
            var windowMode = (System.Windows.PresentationSource.FromVisual(window) as System.Windows.Interop.HwndSource)?.CompositionTarget?.RenderMode.ToString() ?? "closed";
            timing.RenderMode = $"{windowMode}, {window.DrawnAs}";
            if (timing.Presented == 0)
                timing.Presented = Now();
            _services.Log.Write(_services.Clock.UtcNow, timing.Steps());
            _services.Log.Write(_services.Clock.UtcNow, timing.FrameText());
        }
        void OnFrame(object? sender, EventArgs e)
        {
            var now = Now();
            if (double.IsNaN(timing.FirstFrame))
                timing.FirstFrame = now;
            else
            {
                if (timing.FrameGaps.Count < FrameGapsLogged)
                    timing.FrameGaps.Add(now - timing.LastFrame);
                if (now - timing.LastFrame > timing.LongestGap)
                {
                    timing.LongestGap = now - timing.LastFrame;
                    timing.LongestGapAt = timing.LastFrame - timing.FirstFrame;
                }
            }
            timing.LastFrame = now;
            timing.Frames++;
            if (timing.Handed != 0 && ++handedSeen == 2)
                timing.Presented = now;
            if (timing.Presented != 0 && now - timing.FirstFrame >= 1000)
                Finish();
        }
        window.ContentRendered += (_, _) => timing.Handed = Now();
        window.Closed += (_, _) => Finish();
        System.Windows.Media.CompositionTarget.Rendering += OnFrame;
    }

    private void OnForegroundTick(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            _foregroundTimer.Stop();
            return;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == _windowHandle)
        {
            _wasInFront = true;
            return;
        }
        if (foreground == IntPtr.Zero || (!_wasInFront && Environment.TickCount64 - _openedTick < ForegroundGraceMs))
            return;
        Exit(returnToGame: false);
    }

    private void Open(OpenTiming? timing)
    {
        if (_disposed || _window is not null)
            return;
        if (timing is not null)
            timing.ChecksDone = Now();
        var roster = _overlay.LiveIndex is { } index
            ? InteractiveRoster.Build(index.Current, StopwatchClock.Shared.NowMs)
            : Array.Empty<RosterEntry>();
        _identities.Clear();
        _identitiesFor = null;

        var window = new InteractiveWindow(this, _gameBounds, roster);
        _window = window;
        if (timing is not null)
        {
            window.SourceInitialized += (_, _) =>
            {
                timing.Built = Now();
                timing.UiJitBuilt = OpenTiming.UiJitMs();
                timing.LayoutBuilt = window.LayoutMs;
                timing.CallsBuilt = window.LayoutCalls;
            };
            WatchFrames(window, timing);
        }
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
        };
        _opening = true;
        try
        {
            window.Show();
            if (timing is not null)
            {
                timing.Shown = Now();
                timing.UiJitShown = OpenTiming.UiJitMs();
                timing.LayoutShown = window.LayoutMs;
                timing.CallsShown = window.LayoutCalls;
            }
            _windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            _openedTick = Environment.TickCount64;
            _wasInFront = false;
            TakeForeground(window, timing);
        }
        finally
        {
            _opening = false;
        }
        if (_window is null)
        {
            if (timing is not null)
                timing.SoundDone = Now();
            return;
        }
        if (window.GlassFailed && !_glassFailLogged)
        {
            _glassFailLogged = true;
            _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: {window.DrawnAs}, the window has no see-through background");
        }
        _services.Sounds.Play(UiSound.InteractiveOverlayOpen, _services.Settings.SoundVolumePercent);
        _foregroundTimer.Start();
        _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay opened ({roster.Count} players listed)");
        if (timing is not null)
            timing.SoundDone = Now();
    }

    private static void TakeForeground(InteractiveWindow window, OpenTiming? timing)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Raise(handle, timing);
        if (timing is not null)
            timing.Detached = Now();

        window.Activate();
        window.Focus();
        var inFront = NativeMethods.GetForegroundWindow() == handle;
        if (!inFront)
        {
            Raise(handle, null);
            inFront = NativeMethods.GetForegroundWindow() == handle;
        }
        if (timing is not null)
        {
            timing.Activated = Now();
            timing.InFront = inFront ? "overlay" : "other";
        }
    }

    private static void Raise(IntPtr handle, OpenTiming? timing)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var ownThread = NativeMethods.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ownThread && NativeMethods.AttachThreadInput(ownThread, foregroundThread, true);
        if (timing is not null)
            timing.Attached = Now();
        try
        {
            NativeMethods.BringWindowToTop(handle);
            NativeMethods.SetForegroundWindow(handle);
            if (timing is not null)
                timing.Raised = Now();
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(ownThread, foregroundThread, false);
        }
    }

    public void Exit(bool returnToGame)
    {
        var window = _window;
        if (window is null)
            return;
        _foregroundTimer.Stop();
        SaveBeforeExit();
        _window = null;
        _windowHandle = IntPtr.Zero;
        window.CloseQuietly();
        if (!_disposed)
            _services.Sounds.Play(UiSound.InteractiveOverlayClose, _services.Settings.SoundVolumePercent);
        if (returnToGame && _gameHandle != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(_gameHandle);
        _services.Log.Write(_services.Clock.UtcNow, returnToGame ? "Interactive Overlay closed"
            : _disposed ? "Interactive Overlay closed (IRIS is shutting down)" : "Interactive Overlay closed (another program came to the front)");
    }

    partial void SaveBeforeExit();

    public void OpenRsiPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !RsiUrls.IsAllowed(uri))
            return;
        Exit(returnToGame: false);
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: the RSI page could not be opened: {ex.Message}");
        }
    }

    public bool Edit(Func<SocialConfig, SocialConfig> change)
    {
        var saved = _services.SaveListChange(change);
        if (!saved.Success)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Interactive Overlay: " + (saved.Error ?? "the list was not saved"));
            _window?.ShowMessage("IRIS: " + (saved.Error ?? "The list was not saved."));
            return false;
        }
        _identities.Clear();
        return true;
    }

    public MarkerIdentity IdentityOf(RosterEntry entry)
    {
        var config = _services.CurrentConfig;
        if (!ReferenceEquals(config, _identitiesFor))
        {
            _identities.Clear();
            _identitiesFor = config;
        }
        if (_identities.TryGetValue(entry.Handle, out var known))
            return known;
        var orgs = KnownOrgs(entry.Handle, entry.Identity.Orgs.Orgs);
        var model = OverlayModelBuilder.Build(new[] { new DetectedPlayer(entry.Handle, default) }, config, _ => orgs);
        var bracket = model.Brackets[0];
        var identity = entry.Identity with { Kind = bracket.Kind, Color = bracket.Color, Orgs = model.Panel[0].Orgs };
        _identities[entry.Handle] = identity;
        return identity;
    }

    private IReadOnlyList<OrgMembership>? KnownOrgs(string handle, IReadOnlyList<OrgMembership>? fallback)
    {
        try
        {
            var cached = _services.Database.GetPlayer(handle)?.Orgs ?? fallback;
            return cached is null ? null : _services.Refresher.WithCachedAttributes(cached);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or System.IO.InvalidDataException)
        {
            _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: cache read for {handle} failed: {ex.Message}");
            return fallback;
        }
    }

    private void ForgetIdentity(string handle) => _identities.Remove(handle);

    public void OpenDossier(RosterEntry entry) => ShowDossier(entry);

    partial void ShowDossier(RosterEntry entry);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _foregroundTimer.Stop();
        _foregroundTimer.Tick -= OnForegroundTick;
        _overlay.InteractiveRequested -= OnRequested;
        Exit(returnToGame: false);
    }
}
