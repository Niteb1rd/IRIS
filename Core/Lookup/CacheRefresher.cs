using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.Core.Lookup;

public enum PlayerLookupStatus
{
    Found,

    NotFound,

    Failed,
}

public sealed class PlayerLookup
{
    private PlayerLookup(PlayerLookupStatus status, string handle, IReadOnlyList<OrgMembership> orgs, string? reason, bool fromCache, bool isStale)
    {
        Status = status;
        Handle = handle;
        Orgs = orgs;
        Reason = reason;
        FromCache = fromCache;
        IsStale = isStale;
    }

    public PlayerLookupStatus Status { get; }

    public string Handle { get; }

    public IReadOnlyList<OrgMembership> Orgs { get; }

    public string? Reason { get; }

    public bool FromCache { get; }

    public bool IsStale { get; }

    public static PlayerLookup Found(string handle, IReadOnlyList<OrgMembership> orgs, bool fromCache, bool isStale) =>
        new(PlayerLookupStatus.Found, handle, orgs, null, fromCache, isStale);

    public static PlayerLookup NotFound(string handle, bool fromCache) =>
        new(PlayerLookupStatus.NotFound, handle, Array.Empty<OrgMembership>(), null, fromCache, false);

    public static PlayerLookup Failed(string handle, string reason) =>
        new(PlayerLookupStatus.Failed, handle, Array.Empty<OrgMembership>(), reason, false, false);
}

public sealed record StaleEntries(IReadOnlyList<string> Players, IReadOnlyList<string> Orgs)
{
    public bool IsEmpty => Players.Count == 0 && Orgs.Count == 0;
}

public sealed class RefreshSummary
{
    public int PlayersRefreshed { get; set; }
    public int PlayersNotFound { get; set; }
    public int OrgsRefreshed { get; set; }
    public int OrgsNotFound { get; set; }
    public List<string> Failures { get; } = new();

    public int Attempted => PlayersRefreshed + PlayersNotFound + OrgsRefreshed + OrgsNotFound + Failures.Count;
}

public sealed class CacheRefresher
{
    private readonly CacheDatabase _database;
    private readonly IRsiClient _rsi;
    private readonly IClock _clock;
    private readonly CacheSettings _settings;
    private readonly OrgFetchGate _orgGate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly HashSet<string> _staleRefreshed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _staleLock = new();

    private readonly object _liveLock = new();
    private int _liveWaiting;
    private DateTimeOffset _liveWaitingAt;
    private int _liveRunning;

    public CacheRefresher(CacheDatabase database, IRsiClient rsi, IClock clock, CacheSettings settings,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _database = database;
        _rsi = rsi;
        _clock = clock;
        _settings = settings;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public void SetLiveWaiting(int count)
    {
        lock (_liveLock)
        {
            _liveWaiting = Math.Max(0, count);
            _liveWaitingAt = _clock.UtcNow;
        }
    }

    public bool LiveLookupWaiting
    {
        get
        {
            if (Volatile.Read(ref _liveRunning) > 0)
                return true;
            lock (_liveLock)
                return _liveWaiting > 0 && _clock.UtcNow - _liveWaitingAt < CacheSettings.LiveWaitingExpiry;
        }
    }

    public async Task YieldToLiveAsync(CancellationToken ct)
    {
        while (LiveLookupWaiting)
        {
            ct.ThrowIfCancellationRequested();
            await _delay(CacheSettings.YieldPoll, ct).ConfigureAwait(false);
        }
    }

    public bool TryClaimStaleRefresh(string handle)
    {
        if (!RsiNames.IsValidHandle(handle))
            return false;
        var cached = _database.GetPlayer(handle);
        if (cached is null || !IsStale(cached.LastRefreshed, _clock.UtcNow))
            return false;
        lock (_staleLock)
            return _staleRefreshed.Add(handle);
    }

    public Task RefreshStalePlayerAsync(string handle, CancellationToken ct = default) =>
        FetchAndStorePlayerAsync(handle, CacheSource.OrgListMember, ct);

    public StaleEntries FindStale(SocialConfig config)
    {
        var now = _clock.UtcNow;
        var players = ListedHandles(config).Where(handle => NeedsPlayerFetch(handle, now)).ToList();
        var orgs = ListedSids(config).Where(sid => NeedsOrgFetch(sid, now)).ToList();
        return new StaleEntries(players, orgs);
    }

    private bool NeedsPlayerFetch(string handle, DateTimeOffset now)
    {
        if (_database.GetNotFoundTime(NotFoundKind.Player, handle) is not null)
            return false;
        var cached = _database.GetPlayer(handle);
        return cached is null || IsStale(cached.LastRefreshed, now);
    }

    private bool NeedsOrgFetch(string sid, DateTimeOffset now)
    {
        if (_database.GetNotFoundTime(NotFoundKind.Org, sid) is not null)
            return false;
        var cached = _database.GetOrg(sid);
        return cached?.MembersRefreshed is not { } refreshed || IsStale(refreshed, now);
    }

    public Task<RefreshSummary> RefreshMissingOrStaleAsync(SocialConfig config, CancellationToken ct = default)
    {
        var stale = FindStale(config);
        return RefreshListEntriesAsync(stale.Players, stale.Orgs, onlyStale: true, ct);
    }

    public Task<RefreshSummary> RefreshAllAsync(SocialConfig config, CancellationToken ct = default) =>
        RefreshListEntriesAsync(ListedHandles(config).ToList(), ListedSids(config).ToList(), onlyStale: false, ct);

    private async Task<RefreshSummary> RefreshListEntriesAsync(
        IReadOnlyList<string> handles, IReadOnlyList<string> sids, bool onlyStale, CancellationToken ct)
    {
        var summary = new RefreshSummary();

        foreach (var handle in handles)
        {
            ct.ThrowIfCancellationRequested();
            if (onlyStale)
                await YieldToLiveAsync(ct).ConfigureAwait(false);
            if (onlyStale && !NeedsPlayerFetch(handle, _clock.UtcNow))
                continue;
            var (result, _) = await FetchAndStorePlayerAsync(handle, CacheSource.PlayerList, ct).ConfigureAwait(false);
            switch (result.Status)
            {
                case RsiStatus.Found:
                    summary.PlayersRefreshed++;
                    break;
                case RsiStatus.NotFound:
                    summary.PlayersNotFound++;
                    break;
                default:
                    summary.Failures.Add($"player {handle}: {result.Reason}");
                    break;
            }
        }

        foreach (var sid in sids)
        {
            ct.ThrowIfCancellationRequested();
            if (onlyStale)
                await YieldToLiveAsync(ct).ConfigureAwait(false);
            if (onlyStale && !NeedsOrgFetch(sid, _clock.UtcNow))
                continue;
            await RefreshOrgListEntryAsync(sid, summary, ct).ConfigureAwait(false);
        }

        return summary;
    }

    private async Task RefreshOrgListEntryAsync(string sid, RefreshSummary summary, CancellationToken ct)
    {
        string longName;
        var known = _database.GetOrg(sid);
        if (known?.LongName is { Length: > 0 } storedName && !IsStale(known.LastRefreshed, _clock.UtcNow))
        {
            longName = storedName;
        }
        else
        {
            var info = await _rsi.GetOrgInfoAsync(sid, ct).ConfigureAwait(false);
            if (info.Status == RsiStatus.NotFound)
            {
                _database.SaveNotFound(NotFoundKind.Org, sid, CacheSource.OrgList, _clock.UtcNow);
                summary.OrgsNotFound++;
                return;
            }
            if (info.Status != RsiStatus.Found)
            {
                Fail(summary, $"org {sid}: {info.Reason}");
                return;
            }

            longName = info.Data!.LongName;
        }

        var members = await _rsi.GetOrgMembersAsync(sid, ct).ConfigureAwait(false);
        if (members.Status != RsiStatus.Found)
        {
            Fail(summary, $"org {sid} members: {members.Reason ?? "not found"}");
            return;
        }

        _database.SaveOrgListEntry(sid, longName, members.Data!.Handles, _clock.UtcNow);
        summary.OrgsRefreshed++;
    }

    public Task<PlayerLookup> LookupPlayerAsync(string handle, CancellationToken ct = default) =>
        GetPlayerCacheFirstAsync(handle, CacheSource.OrgListMember, ct);

    public async Task<PlayerLookup> LookupWithAffiliatesAsync(
        string handle, CancellationToken ct = default, Func<IReadOnlyList<OrgMembership>, bool>? answerEarly = null)
    {
        Interlocked.Increment(ref _liveRunning);
        try
        {
            var player = await GetPlayerCacheFirstAsync(handle, CacheSource.OrgListMember, ct).ConfigureAwait(false);
            if (player.Status != PlayerLookupStatus.Found)
                return player;

            if (answerEarly is not null && answerEarly(player.Orgs))
                return PlayerLookup.Found(player.Handle, WithCachedAttributes(player.Orgs), player.FromCache, player.IsStale);

            var orgs = new List<OrgMembership>(player.Orgs.Count);
            foreach (var membership in player.Orgs)
            {
                if (membership.IsMain || membership.Sid is null || membership.Archetype is not null || membership.ActivityList.Count > 0)
                {
                    orgs.Add(membership);
                    continue;
                }

                var org = await GetOrgAttributesAsync(membership.Sid, ct).ConfigureAwait(false);
                orgs.Add(org is null ? membership : membership with { Archetype = org.Archetype, Activities = org.Activities });
            }

            return PlayerLookup.Found(player.Handle, orgs, player.FromCache, player.IsStale);
        }
        finally
        {
            Interlocked.Decrement(ref _liveRunning);
        }
    }

    public IReadOnlyList<OrgMembership> WithCachedAttributes(IReadOnlyList<OrgMembership> orgs)
    {
        var result = new List<OrgMembership>(orgs.Count);
        foreach (var membership in orgs)
        {
            if (membership.IsMain || membership.Sid is null || membership.Archetype is not null || membership.ActivityList.Count > 0
                || _database.GetOrg(membership.Sid) is not { HasAttributes: true } org)
                result.Add(membership);
            else
                result.Add(membership with { Archetype = org.Archetype, Activities = org.Activities });
        }

        return result;
    }

    public bool NeedsAffiliateTopUp(string sid)
    {
        var now = _clock.UtcNow;
        if (!_orgGate.MayAsk(sid, now) || _database.IsKnownOrgNotFound(sid, now))
            return false;
        return _database.GetOrg(sid) is not { HasAttributes: true };
    }

    public Task TopUpAffiliateAsync(string sid, CancellationToken ct = default) => GetOrgAttributesAsync(sid, ct);

    private async Task<PlayerLookup> GetPlayerCacheFirstAsync(string handle, CacheSource source, CancellationToken ct)
    {
        if (!RsiNames.IsValidHandle(handle))
            return PlayerLookup.Failed(handle, "invalid name");

        var now = _clock.UtcNow;
        if (_database.IsKnownNotFound(handle, now))
            return PlayerLookup.NotFound(handle, fromCache: true);

        var cached = _database.GetPlayer(handle);
        if (cached is not null && !IsStale(cached.LastRefreshed, now))
            return PlayerLookup.Found(cached.Handle, cached.Orgs, fromCache: true, isStale: false);

        var (result, stored) = await FetchAndStorePlayerAsync(handle, source, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                return PlayerLookup.Found(stored!.Handle, stored.Orgs, fromCache: false, isStale: false);

            case RsiStatus.NotFound:
                return PlayerLookup.NotFound(handle, fromCache: false);

            default:
                if (cached is not null)
                    return PlayerLookup.Found(cached.Handle, cached.Orgs, fromCache: true, isStale: true);
                return PlayerLookup.Failed(handle, result.Reason ?? "unknown error");
        }
    }

    private async Task<(RsiResult<PlayerOrgs> Result, PlayerOrgs? Stored)> FetchAndStorePlayerAsync(string handle, CacheSource source, CancellationToken ct)
    {
        var result = await _rsi.GetPlayerOrgsAsync(handle, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        switch (result.Status)
        {
            case RsiStatus.Found:
                var data = result.Data!;
                if (!string.Equals(data.Handle, handle, StringComparison.OrdinalIgnoreCase))
                    data = data with { Handle = handle };
                _database.SavePlayer(data, source, now);
                return (result, data);

            case RsiStatus.NotFound:
                _database.SaveNotFound(NotFoundKind.Player, handle, source, now);
                break;
        }

        return (result, null);
    }

    private async Task<CachedOrg?> GetOrgAttributesAsync(string sid, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var cached = _database.GetOrg(sid);
        if (cached?.AttributesRefreshed is { } at && !IsStale(at, now))
            return cached;

        if (_database.IsKnownOrgNotFound(sid, now))
            return null;

        if (!_orgGate.MayAsk(sid, now))
            return cached is { HasAttributes: true } ? cached : null;

        var result = await _rsi.GetOrgInfoAsync(sid, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                try
                {
                    _database.SaveOrgAttributes(result.Data!, CacheSource.OrgListMember, _clock.UtcNow);
                }
                catch
                {
                    _orgGate.Failed(sid, _clock.UtcNow);
                    throw;
                }
                _orgGate.Succeeded(sid);
                return _database.GetOrg(sid);

            case RsiStatus.NotFound:
                _orgGate.NotFound(sid);
                if (cached?.Source != CacheSource.OrgList)
                    _database.SaveNotFound(NotFoundKind.Org, sid, CacheSource.OrgListMember, _clock.UtcNow);
                return null;

            default:
                _orgGate.Failed(sid, _clock.UtcNow);
                return cached is { HasAttributes: true } ? cached : null;
        }
    }

    private static void Fail(RefreshSummary summary, string line) => summary.Failures.Add(line);

    private bool IsStale(DateTimeOffset lastRefreshed, DateTimeOffset now) => now - lastRefreshed > _settings.Ttl;

    private static IEnumerable<string> ListedHandles(SocialConfig config) =>
        config.Players.Select(p => p.Handle).Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ListedSids(SocialConfig config) =>
        config.Orgs.Select(o => o.Sid).Distinct(StringComparer.OrdinalIgnoreCase);
}
