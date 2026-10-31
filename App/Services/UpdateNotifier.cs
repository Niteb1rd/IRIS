using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Views;
using Iris.Core.Lookup;
using Iris.Core.Updates;

namespace Iris.App.Services;

public sealed class UpdateNotifier : IDisposable
{
    private readonly AppServices _services;
    private readonly Func<bool> _canAsk;
    private readonly CancellationTokenSource _stop = new();
    private HttpClient? _http;
    private bool _disposed;

    public UpdateNotifier(AppServices services, Func<bool> canAsk)
    {
        _services = services;
        _canAsk = canAsk;
    }

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

        if (_disposed || result.Newer is null || !_canAsk())
            return;

        var message =
            $"IRIS {result.Newer} is available (you have {current}).\n\n"
            + "To update: download the zip, then copy IRIS.exe and the Data folder from it into your IRIS folder and replace the files there. "
            + "Your Config and Logs folders stay as they are, so your lists and settings are kept. Quit IRIS first.\n\n"
            + "Open the download page now?";
        var yes = PromptDialog.Ask(message, $"IRIS {result.Newer} is available.", _services.Notify, owner: null);
        if (!yes || _disposed)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(CacheSettings.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _services.Notify?.Invoke("Could not open the browser: " + ex.Message);
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
    }
}
