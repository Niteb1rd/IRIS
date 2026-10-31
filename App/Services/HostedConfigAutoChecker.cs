using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core.Config;
using Iris.Core.Lookup;

namespace Iris.App.Services;

public sealed class HostedConfigAutoChecker : IDisposable
{
    private const string Headline = "An update to your hosted config is available.";
    private const string Question = Headline + " Do you want to update now?";

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly DispatcherTimer _timer;

    private bool _running;
    private bool _disposed;

    private DateTimeOffset? _lastAttempt;

    private PendingUpdate? _pending;

    private sealed record PendingUpdate(string Url, string Text, string Hash);

    public HostedConfigAutoChecker(AppServices services, ImportCoordinator imports)
    {
        _services = services;
        _imports = imports;
        _timer = new DispatcherTimer { Interval = CacheSettings.HostedWakeInterval };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_disposed)
            return;
        _timer.Start();
        Wake();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => Wake();

    private bool CanRun() => !_disposed && !_services.IsShuttingDown && !_imports.IsQuitting;

    private bool IsFree()
    {
        if (_imports.IsBusy)
            return false;
        var host = _imports.Host;
        return host is null || host.IsIdleForPrompt;
    }

    private async void Wake()
    {
        if (_running || !CanRun())
            return;

        var now = _services.Clock.UtcNow;
        var settings = _services.Settings;
        if (!HostedConfigSchedule.IsDue(settings, now))
            return;
        if (_lastAttempt is { } last && now >= last && now - last < CacheSettings.HostedCheckInterval)
            return;
        if (!IsFree())
            return;

        _running = true;
        try
        {
            await CheckAsync(settings.HostedConfigUrl);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Hosted config auto-check failed unexpectedly: " + ex);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task CheckAsync(string url)
    {
        string text;
        string hash;
        var current = _services.Settings;

        if (_pending is { } pending && string.Equals(pending.Url, url, StringComparison.Ordinal))
        {
            Detail("Hosted config auto-check: asking again about the update found earlier (not fetched again).");
            text = pending.Text;
            hash = pending.Hash;
        }
        else
        {
            _pending = null;
            Detail("Hosted config auto-check: looking for an update.");
            var fetched = await _services.FetchHostedConfigAsync(url);

            if (!CanRun())
                return;

            current = _services.Settings;
            if (!current.HostedConfigAutoCheck || !string.Equals(current.HostedConfigUrl, url, StringComparison.Ordinal))
            {
                Detail("Hosted config auto-check: the setting changed meanwhile; the answer was dropped.");
                return;
            }

            if (!fetched.Success || fetched.Text is null || fetched.Hash is null)
            {
                Log("Hosted config auto-check failed: " + fetched.Error);
                RememberAttempt();
                return;
            }

            text = fetched.Text;
            hash = fetched.Hash;
        }

        if (!HostedConfigSchedule.IsUpdate(current, hash))
        {
            _pending = null;
            Detail("Hosted config auto-check: no change since the last import.");
            RememberAttempt();
            return;
        }

        if (!IsFree() || !_imports.TryBeginQuestion())
        {
            _pending = new PendingUpdate(url, text, hash);
            Log("Hosted config auto-check: an update is available, but IRIS is busy with another question; will ask again later.");
            return;
        }

        _pending = null;

        bool yes;
        try
        {
            Log("Hosted config auto-check: an update is available; asking the user.");
            RememberAttempt();
            yes = PromptDialog.Ask(Question, Headline, _services.Notify, _imports.Host?.DialogOwner);
        }
        finally
        {
            _imports.EndQuestion();
        }

        if (!yes)
        {
            Detail("Hosted config auto-check: the user chose not to update now.");
            return;
        }

        await ImportAsync(url, text, hash);
    }

    private async Task ImportAsync(string url, string text, string hash)
    {
        if (!CanRun())
            return;

        var attempt = await _imports.ImportTextAsync(text, "Import from " + HostOf(url), ignoreHostedUrl: false, userJustActed: true);
        if (attempt.Outcome != ImportOutcome.Imported)
        {
            Log("Hosted config auto-check: not imported (" + attempt.Outcome + "): " + attempt.Message);
            if (attempt.Outcome != ImportOutcome.Cancelled)
                _services.Notify?.Invoke(attempt.Message);
            return;
        }

        var message = attempt.Message;
        if (string.Equals(_services.Settings.HostedConfigUrl, url, StringComparison.Ordinal))
        {
            var remembered = _services.UpdateSettings(s => s with { HostedConfigLastHash = hash });
            if (!remembered.Success)
                message += " (the version could not be saved: " + remembered.Error + ")";
        }
        _services.Notify?.Invoke(message);
    }

    private void RememberAttempt()
    {
        var stamp = AppSettingsToml.TruncateToSeconds(_services.Clock.UtcNow);
        _lastAttempt = stamp;
        var saved = _services.UpdateSettings(s => s with { HostedConfigLastCheck = stamp });
        if (!saved.Success)
            Log("Hosted config auto-check: the time of this check could not be saved: " + saved.Error);
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void Log(string message) => _services.Log.Write(_services.Clock.UtcNow, message);

    private void Detail(string message)
    {
        if (AppVersion.Diagnostics)
            Log(message);
    }
}
