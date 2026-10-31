using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Iris.App.Services;
using Iris.App.Views;
using Iris.Core.Config;

namespace Iris.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "IRIS.SingleInstance";

    private Mutex? _mutex;
    private AppServices? _services;
    private TrayIcon? _tray;
    private ConfigWindow? _configWindow;
    private ImportCoordinator? _imports;
    private CacheUpdater? _updater;
    private HostedConfigAutoChecker? _hostedChecker;
    private UpdateNotifier? _updateNotifier;
    private OverlayController? _overlay;
    private LiveDetectionService? _live;
    private bool _shuttingDown;

    private const string AnswerFirstText = "Answer the open IRIS question first.";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!WindowsRequirement.IsMet)
        {
            PromptDialog.Tell(WindowsRequirement.Message, balloonText: string.Empty, notify: null, owner: null, userJustActed: true, okText: "Close");
            Shutdown(1);
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show(
                "IRIS is already running. Look for its icon in the system tray (bottom right, maybe under the ^ arrow).",
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            StartUp();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "IRIS could not start:\n\n" + ex.Message + "\n\nIRIS folder: " + AppContext.BaseDirectory,
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartUp()
    {
        NativeLibraries.LoadFrom(DataPaths.FromBaseDirectory(AppContext.BaseDirectory).DataFolder);

        var services = AppServices.Create(AppContext.BaseDirectory);
        _services = services;

        DispatcherUnhandledException += OnUnhandledException;

        var imports = new ImportCoordinator(services);
        var updater = new CacheUpdater(services);
        _imports = imports;
        _updater = updater;

        var tray = new TrayIcon();
        _tray = tray;
        services.Notify = text => tray.ShowBalloon(text);
        updater.RefreshingChanged += (_, _) => tray.SetActivity(updater.IsRefreshing ? "refreshing the cache..." : null);
        updater.FillMissingFinished += (_, message) => ShowStatus(message);
        tray.SettingsRequested += (_, _) => OpenSettings();

        var overlay = new OverlayController(services, imports);
        _overlay = overlay;
        overlay.Start();

        void RequestBorderlessCapture()
        {
            if (services.Settings.HideCaptureBorder)
                CaptureBorder.RequestAccess(text => services.Log.Write(services.Clock.UtcNow, text));
        }
        services.SettingsApplied += (_, _) => RequestBorderlessCapture();
        RequestBorderlessCapture();

        var live = new LiveDetectionService(services, overlay);
        _live = live;
        live.Start();

        void SyncTrayChecks()
        {
            tray.SetOverlayDisabled(services.Settings.OverlayDisabled);
            tray.SetPanelDisabled(!services.Settings.PanelEnabled);
        }
        tray.ToggleOverlayRequested += (_, _) =>
        {
            overlay.ToggleFromTray(HotkeyAction.ToggleOverlay);
            SyncTrayChecks();
        };
        tray.TogglePanelRequested += (_, _) =>
        {
            overlay.ToggleFromTray(HotkeyAction.TogglePanel);
            SyncTrayChecks();
        };
        services.SettingsApplied += (_, _) => SyncTrayChecks();
        SyncTrayChecks();
        tray.OpenLogRequested += (_, _) => OpenLog();
        tray.OpenConfigLocationRequested += (_, _) => OpenConfigLocation();
        tray.QuitRequested += (_, _) => Quit();

        var load = services.ListLoad;
        if (!load.IsValid)
            ShowListProblems(load.Errors.ToList(), load.Warnings.ToList(), services.Notify);

        if (_shuttingDown)
            return;

        updater.StartFillMissing();

        var problems = new List<string>();
        if (services.ListStartup.UsedLastGood)
            problems.Add(ConfigMessages.UsingLastGood(DataPaths.SocialListFileName, services.ListStartup.LastGoodTime!.Value));
        if (services.SettingsStartup.UsedLastGood)
            problems.Add(ConfigMessages.UsingLastGood(DataPaths.SettingsFileName, services.SettingsStartup.LastGoodTime!.Value));
        else if (!services.SettingsLoad.IsValid)
            problems.Add("Could not read Config\\settings.txt (" + services.SettingsLoad.Errors[0] + "). Default settings are used until you save in the Settings tab.");

        if (problems.Count > 0)
        {
            var answer = PromptDialog.Ask(
                "Errors were detected in your config files:\n\n- " + string.Join(".\n- ", problems) + ".\n\n"
                + "Your files were not changed. Open Settings to see the errors and fix them now?",
                "Errors were detected in your config files.", services.Notify, owner: null, userJustActed: true);
            if (_shuttingDown)
                return;
            if (answer && load.IsValid)
                OpenSettings();
        }
        else if (load.IsValid && load.Warnings.Count > 0)
        {
            var extra = load.Warnings.Count > 1 ? $" (and {load.Warnings.Count - 1} more)" : string.Empty;
            _tray?.ShowBalloon("IRIS is running in the system tray. Note about your list file: " + load.Warnings[0] + extra);
        }
        else if (load.IsValid)
        {
            _tray?.ShowBalloon("IRIS is running in the system tray. Right-click the icon for Settings.");
        }

        var checker = new HostedConfigAutoChecker(services, imports);
        _hostedChecker = checker;
        checker.Start();

        var notifier = new UpdateNotifier(services, () => !_shuttingDown && !imports.IsBusy && !PromptDialog.IsAnyOpen);
        _updateNotifier = notifier;
        notifier.Start();

        if (!load.IsValid)
            OpenSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hostedChecker?.Dispose();
        _hostedChecker = null;
        _updateNotifier?.Dispose();
        _updateNotifier = null;
        _live?.Dispose();
        _live = null;
        _overlay?.Dispose();
        _overlay = null;
        _tray?.Dispose();
        _tray = null;
        _services?.Dispose();
        _services = null;

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            _mutex.Dispose();
            _mutex = null;
        }

        base.OnExit(e);
    }

    private void OpenSettings()
    {
        if (_services is null || _shuttingDown)
            return;

        if (_imports?.IsBusy == true)
        {
            _tray?.ShowBalloon(AnswerFirstText);
            return;
        }

        if (_configWindow is not null)
        {
            if (_configWindow.WindowState == WindowState.Minimized)
                _configWindow.WindowState = WindowState.Normal;
            _configWindow.Activate();
            return;
        }

        var window = new ConfigWindow(_services, _imports!, _updater!, _overlay!);
        window.Closed += (_, _) => _configWindow = null;
        _configWindow = window;
        window.Show();
        window.Activate();
    }

    private void ShowStatus(string message)
    {
        if (_shuttingDown)
            return;
        if (_configWindow is not null)
            _configWindow.ShowStatus(message);
        else
            _tray?.ShowBalloon(message);
    }

    private void OpenLog()
    {
        var services = _services;
        if (services is null || _shuttingDown)
            return;

        try
        {
            var path = services.FileLog.CurrentFilePath;
            if (!File.Exists(path))
                services.Log.Write(DateTimeOffset.UtcNow, "Log opened");
            if (!File.Exists(path))
                File.WriteAllBytes(path, Array.Empty<byte>());

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Could not open the log: " + ex.Message);
        }
    }

    private void OpenConfigLocation()
    {
        var services = _services;
        if (services is null || _shuttingDown)
            return;

        try
        {
            var folder = services.Paths.ConfigFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Could not open the config location: " + ex.Message);
        }
    }

    private void Quit()
    {
        if (_shuttingDown)
            return;

        if (_imports?.IsBusy == true)
        {
            _tray?.ShowBalloon(AnswerFirstText);
            return;
        }

        var window = _configWindow;
        if (window is not null)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                window.Activate();
                return;
            }
            if (_configWindow is not null)
            {
                if (window.IsBusy)
                    _tray?.ShowBalloon(AnswerFirstText);
                return;
            }
        }

        _shuttingDown = true;
        if (_imports is not null)
            _imports.IsQuitting = true;
        _overlay?.CloseBorderlessNotice();
        _hostedChecker?.Stop();
        _updateNotifier?.Dispose();
        _services?.RequestShutdown();
        _updater?.StopFillMissing(TimeSpan.FromMilliseconds(500));
        Shutdown();
    }

    private static void ShowListProblems(IReadOnlyList<string> errors, IReadOnlyList<string> warnings, Action<string>? notify)
    {
        var text = new StringBuilder();
        if (errors.Count > 0)
        {
            text.AppendLine("IRIS found problems in your list file (Config\\social-config.txt):");
            text.AppendLine();
            foreach (var error in errors.Take(12))
                text.AppendLine("  - " + error);
            if (errors.Count > 12)
                text.AppendLine($"  ...and {errors.Count - 12} more.");
            text.AppendLine();
            text.AppendLine("The file has not been changed. Entries with problems are left out of the Settings window; nothing is written until you press Save.");
        }
        if (warnings.Count > 0)
        {
            if (errors.Count > 0)
                text.AppendLine();
            text.AppendLine("Notes:");
            foreach (var warning in warnings.Take(8))
                text.AppendLine("  - " + warning);
            if (warnings.Count > 8)
                text.AppendLine($"  ...and {warnings.Count - 8} more.");
        }

        PromptDialog.Tell(text.ToString(), "IRIS found problems in your list file.", notify, owner: null, userJustActed: true);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Write(DateTimeOffset.UtcNow, "Unexpected error: " + e.Exception);
        var message = "Something unexpected went wrong:\n\n" + e.Exception.Message + "\n\nIRIS keeps running.";
        var claimed = _imports?.TryBeginQuestion() == true;
        try
        {
            PromptDialog.Tell(message, "Something unexpected went wrong in IRIS.", _services?.Notify, _configWindow);
        }
        catch (Exception)
        {
            MessageBox.Show(message, "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (claimed)
                _imports?.EndQuestion();
        }
        e.Handled = true;
    }
}
