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
    private readonly HashSet<(int Epoch, int Id)> _sightings = new();
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Sid, string Name)> _orgQueue = new();
    private readonly HashSet<string> _orgQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _refreshQueue = new();
    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _nearestMetres = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _retryAtMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<LookupResult> _results = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly List<Task> _liveLookups = new();
    private Task? _backgroundJob;
    private SocialConfig? _config;

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

    public ITraceSink? Trace { get; set; }

    private double _stepNowMs;

    public Task? Running
    {
        get
        {
            var active = ActiveLookups().ToList();
            return active.Count == 0 ? null : Task.WhenAll(active);
        }
    }

    public bool LookupRunning => ActiveLookups().Any();

    public int QueuedLookups => _queue.Count;

    public void Step(IndexSnapshot snapshot, SocialConfig config, double nowMs, CancellationToken ct = default)
    {
        if (!ReferenceEquals(config, _config))
        {
            _config = config;
            _identities.Clear();
            _provisional.Clear();
        }

        _stepNowMs = nowMs;
        DrainResults(nowMs);
        _wanted.Clear();
        _nearestMetres.Clear();
        var updates = new List<IndexUpdate>();
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status != PlateStatus.Confirmed || marker.Name is not { } name)
                continue;
            _wanted.Add(name);
            NoteDistance(name, marker.DistanceMetres);
            var identity = IdentityOf(name, Resolve(name, nowMs), config);
            if (identity.Rsi == RsiState.Verified && _source is not null && _sightings.Add((snapshot.Epoch, marker.Id)))
            {
                if (_sightings.Count > _live.CacheRemembered)
                {
                    _sightings.Clear();
                    _sightings.Add((snapshot.Epoch, marker.Id));
                }
                _source.NoteSeenOnPlate(name);
            }
            if (marker.Identity != identity)
            {
                updates.Add(new IdentityUpdate(PipelineGroup.Cache, marker.NameMs, snapshot.Epoch, marker.Id, identity));
                if (Trace.Line(nowMs, "cch", "identity") is { } t)
                    Trace!.Write(t.Int("mid", marker.Id).Str("name", name).Str("rsi", identity.Rsi.ToString()).Str("kind", identity.Kind.ToString())
                        .Str("color", identity.Color.ToString()).Num("fms", marker.NameMs).Text());
            }
        }

        if (updates.Count > 0)
            _sink.Submit(updates);
        StartNextLookup(ct);
        _source?.SetLiveWaiting(LiveDemand());
    }

    private int LiveDemand()
    {
        var count = _liveLookups.Count(lookup => !lookup.IsCompleted);
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
        if (_source is null)
            return;

        ForgetNamesThatLeft();
        PutNearestFirst();
        _liveLookups.RemoveAll(lookup => lookup.IsCompleted);
        var turns = _source.LiveTurnsAvailable;
        while (turns > 0 && _liveLookups.Count < CacheSettings.RsiLiveBurst && _queue.TryDequeue(out var name))
        {
            _liveLookups.Add(SignalWhenDone(RunLookupAsync(name, ct)));
            TraceLookup("live", name);
            turns--;
        }

        if (_queue.Count == 0 && _backgroundJob is not { IsCompleted: false })
            StartBackgroundJob(ct);
    }

    private void NoteDistance(string name, double metres)
    {
        var known = double.IsNaN(metres) ? double.PositiveInfinity : metres;
        _nearestMetres[name] = _nearestMetres.TryGetValue(name, out var seen) ? Math.Min(seen, known) : known;
    }

    private void PutNearestFirst()
    {
        if (_queue.Count < 2)
            return;
        var nearestFirst = _queue.OrderBy(name => _nearestMetres.GetValueOrDefault(name, double.PositiveInfinity)).ToList();
        _queue.Clear();
        foreach (var name in nearestFirst)
            _queue.Enqueue(name);
    }

    private void ForgetNamesThatLeft()
    {
        for (var remaining = _queue.Count; remaining > 0; remaining--)
        {
            var name = _queue.Dequeue();
            if (_wanted.Contains(name))
                _queue.Enqueue(name);
            else
                _queued.Remove(name);
        }
    }

    private void StartBackgroundJob(CancellationToken ct)
    {
        while (_orgQueue.TryDequeue(out var item))
        {
            if (!_wanted.Contains(item.Name))
            {
                _orgQueued.Remove(item.Sid);
                continue;
            }

            _backgroundJob = SignalWhenDone(RunTopUpAsync(item.Sid, ct));
            TraceLookup("topup", item.Sid);
            return;
        }

        if (_refreshQueue.TryDequeue(out var stale))
        {
            _backgroundJob = SignalWhenDone(RunRefreshAsync(stale, ct));
            TraceLookup("refresh", stale);
        }
    }

    private IEnumerable<Task> ActiveLookups()
    {
        foreach (var lookup in _liveLookups)
            if (!lookup.IsCompleted)
                yield return lookup;
        if (_backgroundJob is { IsCompleted: false } job)
            yield return job;
    }

    private Task SignalWhenDone(Task lookup)
    {
        lookup.ContinueWith(
            static (_, state) =>
            {
                try
                {
                    ((AutoResetEvent)state!).Set();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            _signal, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return lookup;
    }

    private void TraceLookup(string kind, string name)
    {
        if (Trace.Line(_stepNowMs, "cch", "lookup") is { } t)
            Trace!.Write(t.Str("kind", kind).Str("name", name).Text());
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
    }

    private void DrainResults(double nowMs)
    {
        while (_results.TryDequeue(out var result))
        {
            if (Trace.Line(nowMs, "cch", "answer") is { } t)
                Trace!.Write(t.Str("name", result.Name).Str("state", result.Answer?.State.ToString()).Bool("fromRsi", result.Answer?.FromRsi ?? false)
                    .Str("error", result.Error).Bool("refresh", result.Refreshed).Str("org", result.OrgSid).Text());
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
        var hadIdentity = _identities.TryGetValue(name, out var known);
        if (hadIdentity && ReferenceEquals(known.Answer, answer))
            return known.Identity;
        var model = OverlayModelBuilder.Build(new[] { new DetectedPlayer(name, default) }, config, _ => answer.Orgs);
        var bracket = model.Brackets[0];
        var identity = new MarkerIdentity(name, bracket.Kind, bracket.Color, model.Panel[0].Orgs, answer.State);
        if (hadIdentity && SameContent(known.Identity, identity))
            identity = known.Identity;
        if (_identities.Count >= _live.CacheRemembered)
            _identities.Clear();
        _identities[name] = (answer, identity);
        return identity;
    }

    private static bool SameContent(MarkerIdentity a, MarkerIdentity b) =>
        a.Handle == b.Handle && a.Kind == b.Kind && a.Color == b.Color && a.Rsi == b.Rsi && Equals(a.Orgs.Match, b.Orgs.Match) && SameOrgs(a.Orgs.Orgs, b.Orgs.Orgs);

    private static bool SameOrgs(IReadOnlyList<OrgMembership>? a, IReadOnlyList<OrgMembership>? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Sid != y.Sid || x.IsMain != y.IsMain || x.Archetype != y.Archetype || x.Name != y.Name || !x.ActivityList.SequenceEqual(y.ActivityList))
                return false;
        }

        return true;
    }

    public void Dispose()
    {
        _source?.SetLiveWaiting(0);
        _signal.Dispose();
    }
}
