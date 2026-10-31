
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

public sealed partial class AppServices
{
    public void SetCurrentConfig(SocialConfig config)
    {
        CurrentConfig = config;
        ClearListErrors();
        ClearListMissing();
    }

    public SaveResult SaveSettings(AppSettings settings)
    {
        var saved = AppSettingsToml.Save(Paths.SettingsFile, settings, SettingsFile);
        if (saved.Success)
        {
            ClearSettingsErrors();
            ApplySettings(settings);
        }
        else
            Log.Write(Clock.UtcNow, "Settings not saved: " + saved.Error);
        return saved;
    }

    public SaveResult UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var changed = change(Settings);
        if (!SettingsFileHasErrors)
            return SaveSettings(changed);

        ApplySettings(changed);
        Log.Write(Clock.UtcNow,
            "settings.txt has errors, so it was not overwritten; the hosted config address/version is kept in memory only until you save in the Settings tab.");
        return SaveResult.Ok();
    }

    private void ApplySettings(AppSettings settings)
    {
        var ttl = settings.CacheTtlDays == 7 ? 7 : 30;
        if (CacheSettings.TtlDays != ttl)
            Log.Write(Clock.UtcNow, $"Cache refresh interval is now {ttl} days");

        var logWasOn = FileLog.Enabled;
        if (logWasOn && !settings.SessionLogEnabled)
            Log.Write(Clock.UtcNow, "Log file switched off (setting); this is the last line written to it");
        FileLog.Enabled = settings.SessionLogEnabled;
        if (!logWasOn && settings.SessionLogEnabled)
            Log.Write(Clock.UtcNow, "Log file switched on (setting)");

        CacheSettings.TtlDays = ttl;
        var disabledChanged = Settings.OverlayDisabled != settings.OverlayDisabled;
        Settings = settings;
        if (disabledChanged)
            Log.Write(Clock.UtcNow, settings.OverlayDisabled ? "Overlay disabled (screen reading stopped)" : "Overlay enabled");
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        if (disabledChanged)
            OverlayDisabledChanged?.Invoke(this, EventArgs.Empty);
    }

    public SaveResult SetOverlayDisabled(bool disabled) =>
        Settings.OverlayDisabled == disabled ? SaveResult.Ok() : UpdateSettings(s => s with { OverlayDisabled = disabled });

    public SaveResult SetPanelEnabled(bool enabled) =>
        Settings.PanelEnabled == enabled ? SaveResult.Ok() : UpdateSettings(s => s with { PanelEnabled = enabled });

    public Task<HostedConfigResult> FetchHostedConfigAsync(string url) => _hostedClient.FetchAsync(url, _shutdown.Token);

    public ImportResult ApplyImport(SocialConfig imported, ImportMode mode)
    {
        var result = ConfigImporter.Import(Paths.SocialListFile, imported, mode, Clock, ListFile);
        if (result.Success && result.Config is not null)
        {
            ListLoad = new SocialListLoadResult(new ConfigLoadResult { Config = result.Config }, isNew: false);
            SetCurrentConfig(result.Config);
            Log.Write(Clock.UtcNow, $"Import ({mode}): {result.Summary}");
            ListApplied?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Log.Write(Clock.UtcNow, $"Import ({mode}) failed: {result.Error}");
        }
        return result;
    }

    public async Task<string> RefreshAllAsync()
    {
        var config = CurrentConfig;
        if (config.Players.Count == 0 && config.Orgs.Count == 0)
            return "Nothing to refresh: your player and org lists are empty.";

        var token = _shutdown.Token;
        var summary = await Task.Run(() => Refresher.RefreshAllAsync(config, token)).ConfigureAwait(true);
        return Describe("Refreshed", summary);
    }

    public Task<RefreshSummary> FillMissingAsync(SocialConfig config, CancellationToken ct) =>
        Task.Run(() => Refresher.RefreshMissingOrStaleAsync(config, ct), ct);

    public static string Describe(string verb, RefreshSummary summary)
    {
        var parts = new List<string>
        {
            $"{verb} {Count(summary.PlayersRefreshed, "player")} and {Count(summary.OrgsRefreshed, "org")}.",
        };
        var notFound = summary.PlayersNotFound + summary.OrgsNotFound;
        if (notFound > 0)
            parts.Add($"Not found on RSI: {notFound}.");
        if (summary.Failures.Count > 0)
            parts.Add($"Could not update: {summary.Failures.Count}.");
        return string.Join(" ", parts);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    public void RequestShutdown()
    {
        _isShuttingDown = true;
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _isShuttingDown = true;
        _shutdown.Cancel();
        _http.Dispose();
        _hostedHttp.Dispose();
        Database.Dispose();
        _shutdown.Dispose();
    }
}
