using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core;
using Iris.Core.Lookup;
using Iris.Core.Config;
using Iris.Core.Detection;

namespace Iris.App.Services;

public sealed partial class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _hostedHttp;
    private readonly HostedConfigClient _hostedClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _pollGate = new();
    private bool _isShuttingDown;

    private AppServices(
        DataPaths paths, ILog log, FileLog fileLog, CacheDatabase database, ExclusionList exclusions, HttpClient http,
        HttpClient hostedHttp, IRsiClient rsi, WatchedFile listFile, WatchedFile settingsFile,
        SocialListStartup listStartup, SettingsStartup settingsStartup)
    {
        Paths = paths;
        Log = log;
        FileLog = fileLog;
        Database = database;
        Exclusions = exclusions;
        _http = http;
        _hostedHttp = hostedHttp;
        _hostedClient = new HostedConfigClient(hostedHttp, Clock, log) { LogRequests = AppVersion.Diagnostics };
        Rsi = rsi;
        ListFile = listFile;
        SettingsFile = settingsFile;
        ListStartup = listStartup;
        SettingsStartup = settingsStartup;
        ListLoad = listStartup.Load;
        CurrentConfig = listStartup.Load.Config;
        SettingsLoad = settingsStartup.Load;
        Settings = settingsStartup.Load.Settings;

        if (listStartup.MainFileHasErrors)
        {
            var note = listStartup.UsedLastGood
                ? $"IRIS is using the last good version (from {FormatTime(listStartup.LastGoodTime!.Value)})."
                : "Rows with problems are left out of the lists below. Nothing is written until you press Save.";
            SetListErrors(listStartup.MainFileErrors, listStartup.MainFileHash, note);
        }
        if (settingsStartup.MainFileHasErrors)
        {
            var note = settingsStartup.UsedLastGood
                ? $"IRIS is using the last good version (from {FormatTime(settingsStartup.LastGoodTime!.Value)})."
                : "IRIS is using the default settings until you press Save.";
            SetSettingsErrors(settingsStartup.MainFileErrors, settingsStartup.MainFileHash, note);
        }

        CacheSettings = new CacheSettings(Settings.CacheTtlDays);
        Validator = new RowValidator(database, rsi, Clock, CacheSettings, log);
        Refresher = new CacheRefresher(database, rsi, Clock, CacheSettings);
    }

    public IClock Clock => SystemClock.Instance;

    public DataPaths Paths { get; }

    public ILog Log { get; }

    public FileLog FileLog { get; }

    public CacheDatabase Database { get; }

    public ExclusionList Exclusions { get; }

    public IRsiClient Rsi { get; }

    public CacheSettings CacheSettings { get; }

    public AppSettings Settings { get; private set; }

    public AppSettingsLoadResult SettingsLoad { get; }

    public WatchedFile ListFile { get; }

    public WatchedFile SettingsFile { get; }

    public SocialListStartup ListStartup { get; }

    public SettingsStartup SettingsStartup { get; }

    public IReadOnlyList<string> ListFileErrors { get; private set; } = Array.Empty<string>();

    public string ListFileNote { get; private set; } = string.Empty;

    public string? ListFileErrorHash { get; private set; }

    public bool ListFileHasErrors => ListFileErrors.Count > 0;

    public bool ListFileMissing { get; private set; }

    public IReadOnlyList<string> SettingsFileErrors { get; private set; } = Array.Empty<string>();

    public string SettingsFileNote { get; private set; } = string.Empty;

    public string? SettingsFileErrorHash { get; private set; }

    public bool SettingsFileHasErrors => SettingsFileErrors.Count > 0;

    public bool SettingsFileNeedsSave => SettingsFileHasErrors;

    public Action<string>? Notify { get; set; }

    public event EventHandler? ListApplied;

    public CancellationToken ShutdownToken => _shutdown.Token;

    public bool IsShuttingDown => _isShuttingDown;

    public RowValidator Validator { get; }

    public CacheRefresher Refresher { get; }

    public SocialListLoadResult ListLoad { get; private set; }

    public SocialConfig CurrentConfig { get; private set; }

    public bool IsOverlayDisabled => Settings.OverlayDisabled;

    public event EventHandler? OverlayDisabledChanged;

    public event EventHandler? SettingsApplied;

    public static AppServices Create(string exeDirectory)
    {
        var paths = DataPaths.FromBaseDirectory(exeDirectory);
        paths.EnsureCreated();

        var clock = SystemClock.Instance;

        FileLog.DeleteOlderThan(paths.LogsFolder, TimeSpan.FromDays(30), clock.UtcNow);
        var fileLog = new FileLog(paths.LogsFolder, clock) { Enabled = false };
        var log = new TeeLog(new InMemoryLog(), fileLog);

        var settingsFile = new WatchedFile(paths.SettingsFile, paths.SettingsLastGoodFile, clock, log);
        var settingsStartup = ConfigStartup.LoadSettings(settingsFile);
        var settingsLoad = settingsStartup.Load;
        fileLog.Enabled = settingsLoad.Settings.SessionLogEnabled;
        fileLog.WriteBlankLine(clock.UtcNow);
        log.Write(clock.UtcNow, "IRIS " + AppVersion.Text + " started");
        log.Write(clock.UtcNow, "IRIS folder: " + paths.ProgramFolder);
        if (NativeLibraries.Loaded.Count > 0)
            log.Write(clock.UtcNow, "Native libraries loaded from the Data folder: " + string.Join(", ", NativeLibraries.Loaded));

        foreach (var error in settingsStartup.MainFileErrors)
            log.Write(clock.UtcNow, settingsStartup.UsedLastGood
                ? "settings.txt has errors, the last good copy is used instead: " + error
                : "settings.txt could not be used, default settings apply: " + error);
        foreach (var warning in settingsLoad.Warnings)
            log.Write(clock.UtcNow, "settings.txt: " + warning);

        var database = CacheDatabase.OpenFile(paths.CacheFile, clock);
        HttpClient? http = null;
        HttpClient? hostedHttp = null;
        try
        {
            var exclusions = ExclusionList.Load(paths.ExclusionsFile, clock, log);

            var listFile = new WatchedFile(paths.SocialListFile, paths.SocialListLastGoodFile, clock, log);
            var listStartup = ConfigStartup.LoadSocialList(listFile);
            foreach (var error in listStartup.MainFileErrors.Take(10))
                log.Write(clock.UtcNow, listStartup.UsedLastGood
                    ? "social-config.txt has errors, the last good copy is used instead: " + error
                    : "social-config.txt has errors: " + error);

            http = RsiClient.CreateHttpClient();
            var limiter = new RsiRateLimiter(clock);
            var rsi = new RsiClient(http, limiter, clock, AppVersion.Diagnostics ? log : null);
            hostedHttp = HostedConfigClient.CreateHttpClient();

            return new AppServices(
                paths, log, fileLog, database, exclusions, http, hostedHttp, rsi, listFile, settingsFile, listStartup, settingsStartup);
        }
        catch
        {
            hostedHttp?.Dispose();
            http?.Dispose();
            database.Dispose();
            throw;
        }
    }
}
