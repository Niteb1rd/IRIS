using System.Collections.Concurrent;
using Iris.Core.Lookup;
using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class CacheWorker : IDisposable
{
    private sealed record Known(NameAnswer Answer, double AtMs);

    private sealed record LookupResult(string Name, NameAnswer? Answer, string? Error, string? OrgSid = null, bool Refreshed = false);

    private static readonly NameAnswer PendingAnswer = new(RsiState.Pending, null);
    private static readonly NameAnswer NotFoundAnswer = new(RsiState.NotFound, null);

    private readonly INameSource? _source;
    private readonly SessionNames _names;
    private readonly IUpdateSink _sink;
    private readonly LiveSettings _live;
    private readonly ILog _log;
    private readonly IClock _wall;
    private readonly LruCache<string, Known> _known;
    private readonly LruCache<string, NameAnswer> _provisional;
    private readonly Dictionary<string, (NameAnswer Answer, MarkerIdentity Identity)> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Sid, string Name)> _orgQueue = new();
    private readonly HashSet<string> _orgQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _refreshQueue = new();
    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _retryAtMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<LookupResult> _results = new();
    private readonly AutoResetEvent _signal = new(false);
    private Task? _running;
    private SocialConfig? _config;
    private bool _runningLive;

    public CacheWorker(INameSource? source, SessionNames names, IUpdateSink sink, LiveSettings live, ILog? log = null, IClock? wallClock = null)
    {
        _source = source;
        _names = names;
        _sink = sink;
        _live = live;
        _known = new(live.CacheRemembered, comparer: StringComparer.OrdinalIgnoreCase);
        _provisional = new(live.CacheRemembered, comparer: StringComparer.OrdinalIgnoreCase);
        _log = log ?? NullLog.Instance;
        _wall = wallClock ?? SystemClock.Instance;
    }

    public WaitHandle Signal => _signal;

    public Task? Running => _running;

    public bool LookupRunning => _running is { IsCompleted: false };

    public int QueuedLookups => _queue.Count;

    public void Step(IndexSnapshot snapshot, SocialConfig config, double nowMs, CancellationToken ct = default)
    {
        if (!ReferenceEquals(config, _config))
        {
            _config = config;
            _identities.Clear();
            _provisional.Clear();
        }

        DrainResults(nowMs);
        _wanted.Clear();
        var updates = new List<IndexUpdate>();
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status != PlateStatus.Confirmed || marker.Name is not { } name)
                continue;
            _wanted.Add(name);
            var identity = IdentityOf(name, Resolve(name, nowMs), config);
            if (marker.Identity != identity)
                updates.Add(new IdentityUpdate(PipelineGroup.Cache, marker.NameMs, snapshot.Epoch, marker.Id, identity));
        }

        if (updates.Count > 0)
            _sink.Submit(updates);
        StartNextLookup(ct);
        _source?.SetLiveWaiting(LiveDemand());
    }

    private int LiveDemand()
    {
        int count = _runningLive && LookupRunning ? 1 : 0;
        foreach (var name in _queue)
            if (_wanted.Contains(name))
                count++;
        return count;
    }

    private NameAnswer Resolve(string name, double nowMs)
    {
        if (_names.IsNotFound(name))
            return NotFoundAnswer;
        if (_known.TryGet(name, out var known))
        {
            if (known.Answer.State != RsiState.Verified || nowMs - known.AtMs < _live.OrgCacheMs)
                return known.Answer;
            if (_source?.Stored(name) is { } refreshed)
            {
                Remember(name, refreshed, nowMs);
                QueueTopUps(name, refreshed);
                return refreshed;
            }

            _known.Set(name, known with { AtMs = nowMs });
            return known.Answer;
        }

        if (_queued.Contains(name) || (_retryAtMs.TryGetValue(name, out var retryAt) && nowMs < retryAt))
            return ProvisionalOrPending(name);

        if (_source?.Stored(name) is { } stored)
        {
            Remember(name, stored, nowMs);
            if (stored.State == RsiState.Verified)
            {
                _log.Write(_wall.UtcNow, $"Found in cache: {name}");
                QueueStaleRefresh(name);
            }

            QueueTopUps(name, stored);
            return stored;
        }

        if (_source is not null && _config is { } config && _source.Provisional(name, config) is { } provisional)
            _provisional.Set(name, provisional);
        Enqueue(name, nowMs);
        return ProvisionalOrPending(name);
    }

    private NameAnswer ProvisionalOrPending(string name) => _provisional.TryGet(name, out var provisional) ? provisional : PendingAnswer;

    private void QueueStaleRefresh(string name)
    {
        if (_source is not null && _source.WantsBackgroundRefresh(name))
            _refreshQueue.Enqueue(name);
    }

    private void Remember(string name, NameAnswer answer, double nowMs)
    {
        _provisional.Remove(name);
        _known.Set(name, new Known(answer, nowMs));
        if (answer.State == RsiState.Verified)
            _names.AddVerified(name);
        else if (answer.State == RsiState.NotFound)
            _names.AddNotFound(name);
    }

    private void QueueTopUps(string name, NameAnswer answer)
    {
        if (_source is null || answer.State != RsiState.Verified || answer.Orgs is null)
            return;
        foreach (var membership in answer.Orgs)
        {
            if (membership.IsMain || membership.Sid is not { } sid || membership.Archetype is not null || membership.ActivityList.Count > 0
                || _orgQueued.Contains(sid) || !_source.NeedsAffiliateTopUp(sid))
                continue;
            _orgQueued.Add(sid);
            _orgQueue.Enqueue((sid, name));
        }
    }

    private void Enqueue(string name, double nowMs)
    {
        if (_source is null || _queued.Contains(name))
            return;
        if (_retryAtMs.TryGetValue(name, out var at) && nowMs < at)
            return;
        _queued.Add(name);
        _queue.Enqueue(name);
    }

    private void StartNextLookup(CancellationToken ct)
    {
        if (_source is null || LookupRunning)
            return;
        while (_queue.TryDequeue(out var name))
        {
            if (!_wanted.Contains(name))
            {
                _queued.Remove(name);
                continue;
            }

            _running = RunLookupAsync(name, ct);
            _runningLive = true;
            return;
        }

        while (_orgQueue.TryDequeue(out var item))
        {
            if (!_wanted.Contains(item.Name))
            {
                _orgQueued.Remove(item.Sid);
                continue;
            }

            _running = RunTopUpAsync(item.Sid, ct);
            _runningLive = false;
            return;
        }

        if (_refreshQueue.TryDequeue(out var stale))
        {
            _running = RunRefreshAsync(stale, ct);
            _runningLive = false;
        }
    }

    private async Task RunRefreshAsync(string name, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await _source!.RefreshInBackgroundAsync(name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }

        _results.Enqueue(new LookupResult(name, null, error, Refreshed: true));
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunTopUpAsync(string sid, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await _source!.TopUpAffiliateAsync(sid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }

        _results.Enqueue(new LookupResult(sid, null, error, sid));
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Refresh(string sid)
    {
        foreach (var (name, known) in _known.Snapshot())
        {
            if (known.Answer.Orgs is { } orgs && orgs.Any(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase)))
                _known.Set(name, known with { AtMs = double.NegativeInfinity });
        }
    }

    private async Task RunLookupAsync(string name, CancellationToken ct)
    {
        NameAnswer? answer = null;
        string? error = null;
        try
        {
            answer = await _source!.AskRsiAsync(name, _config ?? new SocialConfig(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }

        _results.Enqueue(new LookupResult(name, answer, error));
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DrainResults(double nowMs)
    {
        while (_results.TryDequeue(out var result))
        {
            if (result.Refreshed)
            {
                if (result.Error is not null)
                    _log.Write(_wall.UtcNow, $"cache: refresh of {result.Name} failed: {result.Error}");
                else if (_known.TryGet(result.Name, out var refreshed))
                    _known.Set(result.Name, refreshed with { AtMs = double.NegativeInfinity });
                continue;
            }

            if (result.OrgSid is { } orgSid)
            {
                _orgQueued.Remove(orgSid);
                if (result.Error is not null)
                    _log.Write(_wall.UtcNow, $"cache: affiliate {orgSid} top-up failed: {result.Error}");
                else
                    Refresh(orgSid);
                continue;
            }

            _queued.Remove(result.Name);
            if (result.Answer is not { } answer)
            {
                if (_retryAtMs.Count >= _live.CacheRemembered)
                    _retryAtMs.Clear();
                _retryAtMs[result.Name] = nowMs + _live.LookupRetryMs;
                if (result.Error is not null)
                    _log.Write(_wall.UtcNow, $"cache: lookup of {result.Name} failed: {result.Error}");
                continue;
            }

            Remember(result.Name, answer, nowMs);
            if (answer.State == RsiState.Verified)
            {
                _log.Write(_wall.UtcNow, answer.FromRsi ? $"Verified on RSI: {result.Name}" : $"Found in cache: {result.Name}");
                QueueTopUps(result.Name, answer);
            }
            else if (answer.FromRsi)
                _log.Write(_wall.UtcNow, $"Not found on RSI: {result.Name}");
        }
    }

    private MarkerIdentity IdentityOf(string name, NameAnswer answer, SocialConfig config)
    {
        if (_identities.TryGetValue(name, out var known) && ReferenceEquals(known.Answer, answer))
            return known.Identity;
        var model = OverlayModelBuilder.Build(new[] { new DetectedPlayer(name, default) }, config, _ => answer.Orgs);
        var bracket = model.Brackets[0];
        var identity = new MarkerIdentity(name, bracket.Kind, bracket.Color, model.Panel[0].OrgsLine, answer.State);
        if (_identities.Count >= _live.CacheRemembered)
            _identities.Clear();
        _identities[name] = (answer, identity);
        return identity;
    }

    public void Dispose()
    {
        _source?.SetLiveWaiting(0);
        _signal.Dispose();
    }
}
