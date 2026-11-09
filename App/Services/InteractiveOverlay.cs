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

    public const int ForegroundGraceMs = 500;

    private readonly AppServices _services;
    private readonly OverlayController _overlay;
    private readonly Func<ConfigWindow?> _settingsWindow;
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(ForegroundPollMs) };
    private readonly Dictionary<string, MarkerIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);

    private InteractiveWindow? _window;
    private IntPtr _gameHandle;
    private Iris.Core.Overlay.PixelRect _gameBounds;
    private IntPtr _windowHandle;
    private long _openedTick;
    private bool _wasInFront;
    private SocialConfig? _identitiesFor;
    private bool _disposed;

    public InteractiveOverlay(AppServices services, OverlayController overlay, Func<ConfigWindow?> settingsWindow)
    {
        _services = services;
        _overlay = overlay;
        _settingsWindow = settingsWindow;
        _foregroundTimer.Tick += OnForegroundTick;
        overlay.InteractiveRequested += OnRequested;
    }

    public bool IsOpen => _window is not null;

    public AppSettings Settings => _services.Settings;

    public SocialConfig Config => _services.CurrentConfig;

    private void OnRequested(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (_window is { } open)
        {
            if (!open.IsTyping)
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

        if (_settingsWindow() is { } settings)
        {
            if (settings.HasUnsavedChanges(ignoreHostedUrl: false))
            {
                _overlay.ShowNotice("IRIS: Unsaved changes in Settings, please save and close first.");
                return;
            }
            settings.Close();
        }

        _gameHandle = game.Handle;
        _gameBounds = game.Bounds;
        Open();
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

    private void Open()
    {
        if (_disposed || _window is not null)
            return;
        var roster = _overlay.LiveIndex is { } index
            ? InteractiveRoster.Build(index.Current, StopwatchClock.Shared.NowMs)
            : Array.Empty<RosterEntry>();
        _identities.Clear();
        _identitiesFor = null;

        var window = new InteractiveWindow(this, _gameBounds, roster);
        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
        };
        window.Show();
        _windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        _openedTick = Environment.TickCount64;
        _wasInFront = false;
        TakeForeground(window);
        _foregroundTimer.Start();
        _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay opened ({roster.Count} players listed)");
    }

    private void TakeForeground(InteractiveWindow window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var ownThread = NativeMethods.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ownThread && NativeMethods.AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(handle);
            NativeMethods.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(ownThread, foregroundThread, false);
        }

        window.Activate();
        window.Focus();
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
        if (returnToGame && _gameHandle != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(_gameHandle);
        _services.Log.Write(_services.Clock.UtcNow, returnToGame ? "Interactive Overlay closed" : "Interactive Overlay closed (another program came to the front)");
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
        _window?.CloseQuietly();
        _window = null;
    }
}
