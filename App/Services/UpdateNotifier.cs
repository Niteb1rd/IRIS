using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Views;
using Iris.Core.Lookup;
using Iris.Core.Updates;

namespace Iris.App.Services;

public sealed class UpdateNotifier : IDisposable
{
    public const string StagingFolderName = "Update";

    private readonly AppServices _services;
    private readonly Func<bool> _canAsk;
    private readonly Action<string?> _setActivity;
    private readonly Action<string, ReleaseVersion> _applyUpdate;
    private readonly CancellationTokenSource _stop = new();
    private HttpClient? _http;
    private HttpClient? _downloadHttp;
    private bool _disposed;

    public UpdateNotifier(AppServices services, Func<bool> canAsk, Action<string?> setActivity, Action<string, ReleaseVersion> applyUpdate)
    {
        _services = services;
        _canAsk = canAsk;
        _setActivity = setActivity;
        _applyUpdate = applyUpdate;
    }

    public static string StagingFolder => Path.Combine(AppContext.BaseDirectory, StagingFolderName);

    public async void Start()
    {
        var current = AppVersion.Version;
        if (_disposed || !_services.Settings.CheckForUpdates || current is null || current.IsDev)
            return;

        UpdateCheckResult result;
        try
        {
            _http = UpdateCheckClient.CreateHttpClient();
            var client = new UpdateCheckClient(_http, _services.Clock, _services.Log);
            result = await client.CheckAsync(current, _stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Update check failed: " + ex.Message);
            return;
        }

        if (_disposed || result.Release is not { } release || !_canAsk())
            return;

        if (!release.CanInstall)
        {
            OfferReleasePage($"IRIS {release.Version} is available (you have {current}).\n\n"
                + "To update: download the zip, then copy IRIS.exe and the Data folder from it into your IRIS folder and replace the files there. "
                + "Your Config and Logs folders stay as they are, so your lists and settings are kept. Quit IRIS first.");
            return;
        }

        var yes = PromptDialog.Ask(
            $"IRIS {release.Version} is available (you have {current}).\n\n"
            + "Update now? IRIS downloads it from GitHub, checks it, replaces its program files and restarts. "
            + "Your lists, settings, logs and cache are kept.",
            owner: null);
        if (!yes || _disposed)
            return;

        await DownloadAndApplyAsync(release, current);
    }

    private async Task DownloadAndApplyAsync(LatestRelease release, ReleaseVersion current)
    {
        var staging = StagingFolder;
        var zipPath = Path.Combine(staging, CacheSettings.UpdateAssetName);
        try
        {
            Directory.CreateDirectory(staging);
            var probe = Path.Combine(staging, "write-test.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Update: no write access to the IRIS folder: " + ex.Message);
            OfferReleasePage("IRIS cannot write to its own folder, so it cannot update itself there.");
            return;
        }

        string? newRoot;
        string? error;
        _setActivity("downloading the update...");
        try
        {
            _downloadHttp = UpdateDownloader.CreateHttpClient();
            var downloader = new UpdateDownloader(_downloadHttp, _services.Clock, _services.Log);
            var download = await downloader.DownloadAsync(release, zipPath, "IRIS/" + current, _stop.Token);
            if (!download.Success)
            {
                OfferReleasePage("The update could not be downloaded: " + download.Error);
                return;
            }

            var extractFolder = Path.Combine(staging, "files");
            var exeName = Path.GetFileName(Environment.ProcessPath) ?? "IRIS.exe";
            (newRoot, error) = await Task.Run(() =>
            {
                var root = UpdateInstaller.Unpack(zipPath, extractFolder, exeName, out var problem);
                return (root, problem);
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return;
        }
        finally
        {
            if (!_disposed)
                _setActivity(null);
        }

        if (_disposed)
            return;
        if (newRoot is null)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Update: " + error);
            OfferReleasePage("The update could not be unpacked: " + error);
            return;
        }

        _applyUpdate(newRoot, release.Version);
    }

    public void OfferReleasePage(string reason)
    {
        if (_disposed)
            return;
        var yes = PromptDialog.Ask(reason + "\n\nOpen the download page now?", owner: null);
        if (!yes || _disposed)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(CacheSettings.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Could not open the browser: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
        _http?.Dispose();
        _downloadHttp?.Dispose();
    }
}
