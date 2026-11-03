using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.App.Services;

public sealed class CacheUpdater
{
    private readonly AppServices _services;

    private CancellationTokenSource? _fillCts;

    private Task? _fillTask;

    private bool _fillPending;

    public CacheUpdater(AppServices services)
    {
        _services = services;
        services.ListApplied += (_, _) => StartFillMissing();
    }

    public bool IsRefreshing { get; private set; }

    public event EventHandler? RefreshingChanged;

    public event EventHandler<string>? FillMissingFinished;

    public async Task<string?> RunFullRefreshAsync()
    {
        if (IsRefreshing || _services.IsShuttingDown)
            return null;

        _fillCts?.Cancel();
        SetRefreshing(true);
        try
        {
            var message = await _services.RefreshAllAsync();
            _services.Log.Write(_services.Clock.UtcNow, "Refresh Cache: " + message);
            return message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Refresh Cache failed: " + ex.Message);
            return "The cache refresh failed: " + ex.Message;
        }
        finally
        {
            SetRefreshing(false);
            if (_fillPending && !_services.IsShuttingDown)
            {
                _fillPending = false;
                StartFillMissing();
            }
        }
    }

    public void StartFillMissing()
    {
        if (_services.IsShuttingDown)
            return;
        if (IsRefreshing)
        {
            _fillPending = true;
            return;
        }

        _fillCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken);
        _fillCts = cts;
        _ = RunFillMissingAsync(cts);
    }

    public void StopFillMissing(TimeSpan timeout)
    {
        var cts = _fillCts;
        var work = _fillTask;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (work is null || work.IsCompleted)
            return;
        try
        {
            work.Wait(timeout);
        }
        catch (AggregateException)
        {
        }
    }

    private async Task RunFillMissingAsync(CancellationTokenSource cts)
    {
        try
        {
            var config = _services.CurrentConfig;
            if (config.Players.Count == 0 && config.Orgs.Count == 0)
                return;

            var work = _services.FillMissingAsync(config, cts.Token);
            _fillTask = work;
            var summary = await work.ConfigureAwait(true);
            if (summary.Attempted > 0 && !cts.IsCancellationRequested)
            {
                var message = AppServices.Describe("Cache filled:", summary);
                _services.Log.Write(_services.Clock.UtcNow, "Fill missing: " + message);
                FillMissingFinished?.Invoke(this, message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Fill missing failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_fillCts, cts))
                _fillCts = null;
            cts.Dispose();
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        IsRefreshing = refreshing;
        RefreshingChanged?.Invoke(this, EventArgs.Empty);
    }
}
