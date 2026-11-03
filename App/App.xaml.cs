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
using Iris.Core.Mining;
using Iris.Core.Updates;

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

    private const string RunningInTrayText = "IRIS is running in the system tray. Right-click the icon for Settings.";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!WindowsRequirement.IsMet)
        {
            PromptDialog.Tell(WindowsRequirement.Message, owner: null, userJustActed: true, okText: "Close");
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

        var leftovers = UpdateInstaller.RemoveLeftovers(AppContext.BaseDirectory, UpdateNotifier.StagingFolder);
        if (leftovers > 0)
            services.Log.Write(services.Clock.UtcNow, $"Update: removed {leftovers} file(s) left from the previous version");

        DispatcherUnhandledException += OnUnhandledException;

        var imports = new ImportCoordinator(services);
        var updater = new CacheUpdater(services);
        _imports = imports;
        _updater = updater;

        var tray = new TrayIcon();
        _tray = tray;
        updater.RefreshingChanged += (_, _) => tray.SetActivity(updater.IsRefreshing ? "refreshing the cache..." : null);
        updater.FillMissingFinished += (_, message) => ShowStatus(message);
        tray.SettingsRequested += (_, _) => OpenSettings();

        var overlay = new OverlayController(services, imports);
        _overlay = overlay;
        overlay.Start();

        void RequestBorderlessCapture()
        {
            if (services.Settings.HideCaptureBorder && services.Settings.CaptureMethod == CaptureMethod.WindowCapture)
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
            ShowListProblems(load.Errors.ToList(), load.Warnings.ToList());

        if (_shuttingDown)
            return;

        updater.StartFillMissing();

        if (leftovers > 0 && IsMiningFileOutdated(services))
        {
            PromptDialog.Tell(MiningDataUpdatedText, owner: null, userJustActed: true);
            if (_shuttingDown)
                return;
        }

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
                owner: null, userJustActed: true);
            if (_shuttingDown)
                return;
            if (answer && load.IsValid)
                OpenSettings();
        }
        else if (load.IsValid)
        {
            _tray?.ShowBalloon(RunningInTrayText);
        }

        var checker = new HostedConfigAutoChecker(services, imports);
        _hostedChecker = checker;
        checker.Start();

        var notifier = new UpdateNotifier(
            services, () => !_shuttingDown && !imports.IsBusy && !PromptDialog.IsAnyOpen, text => _tray?.SetActivity(text), ApplyUpdate);
        _updateNotifier = notifier;
        notifier.Start();

        if (!load.IsValid)
            OpenSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseAll();
        base.OnExit(e);
    }

    private void ReleaseAll()
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
    }

    private void OpenSettings()
    {
        if (_services is null || _shuttingDown)
            return;

        if (_imports?.IsBusy == true)
        {
            if (!DialogHelper.ActivateModal())
                _configWindow?.Activate();
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
        _configWindow?.ShowStatus(message);
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
            services.Log.Write(DateTimeOffset.UtcNow, "Could not open the log: " + ex.Message);
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
            services.Log.Write(DateTimeOffset.UtcNow, "Could not open the config location: " + ex.Message);
        }
    }

    private void Quit() => Quit(restartExe: null);

    private void ApplyUpdate(string newRoot, ReleaseVersion version)
    {
        if (_shuttingDown || _services is null)
            return;

        var installFolder = AppContext.BaseDirectory;
        var result = UpdateInstaller.Install(newRoot, installFolder);
        if (!result.Success)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Update: install failed: " + result.Error);
            _updateNotifier?.OfferReleasePage("The update could not be installed: " + result.Error);
            return;
        }

        var addedNames = UpdateInstaller.MergeExclusions(newRoot, installFolder, out var mergeError);
        _services.Log.Write(_services.Clock.UtcNow, addedNames is null
            ? "Update: the new exclusion names could not be added to Config\\exclusions.txt: " + mergeError
            : $"Update: {addedNames} new exclusion name(s) added to Config\\exclusions.txt");
        _services.Log.Write(_services.Clock.UtcNow, $"Update: installed {result.Files} file(s); restarting");
        if (InstallerRegistration.SetVersion(installFolder, version.ToString()))
            _services.Log.Write(_services.Clock.UtcNow, $"Update: the installed version shown in Windows' Apps list is now {version}");
        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "IRIS.exe";
        Quit(restartExe: Path.Combine(installFolder, exeName));
    }

    private void Quit(string? restartExe)
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        if (_imports is not null)
            _imports.IsQuitting = true;
        _overlay?.CloseBorderlessNotice();
        _hostedChecker?.Stop();
        _updateNotifier?.Dispose();
        _services?.RequestShutdown();
        _updater?.StopFillMissing(TimeSpan.FromMilliseconds(500));

        if (restartExe is not null)
        {
            DialogHelper.CloseModals();
            _configWindow?.DiscardOnClose();
            ReleaseAll();
            try
            {
                Process.Start(new ProcessStartInfo(restartExe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }
            Environment.Exit(0);
            return;
        }

        if (DialogHelper.IsSystemDialogOpen())
        {
            _configWindow?.DiscardOnClose();
            ReleaseAll();
            Environment.Exit(0);
            return;
        }

        DialogHelper.CloseModals();
        if (_configWindow is { } window)
        {
            window.DiscardOnClose();
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
        Shutdown();
    }

    private const string MiningDataUpdatedText = "Mining data was updated, delete or back-up config mining.txt to use newest data.";

    private static bool IsMiningFileOutdated(AppServices services)
    {
        try
        {
            var path = services.Paths.MiningFile;
            return File.Exists(path) && MiningTable.IsOutdated(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ShowListProblems(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
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

        PromptDialog.Tell(text.ToString(), owner: null, userJustActed: true);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Write(DateTimeOffset.UtcNow, "Unexpected error: " + e.Exception);
        var message = "Something unexpected went wrong:\n\n" + e.Exception.Message + "\n\nIRIS keeps running.";
        var claimed = _imports?.TryBeginQuestion() == true;
        try
        {
            PromptDialog.Tell(message, _configWindow);
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
