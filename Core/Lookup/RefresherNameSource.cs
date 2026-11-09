using System.Data.Common;
using Iris.Core.Config;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;

namespace Iris.Core.Lookup;

public sealed class RefresherNameSource : INameSource
{
    private readonly CacheDatabase _database;
    private readonly CacheRefresher _refresher;
    private readonly IClock _clock;

    public RefresherNameSource(CacheDatabase database, CacheRefresher refresher, IClock clock)
    {
        _database = database;
        _refresher = refresher;
        _clock = clock;
    }

    public NameAnswer? Stored(string name)
    {
        try
        {
            if (_database.IsKnownNotFound(name, _clock.UtcNow))
                return new NameAnswer(RsiState.NotFound, null);
            return _database.GetPlayer(name) is { } cached ? new NameAnswer(RsiState.Verified, WithCachedAttributes(cached.Orgs)) : null;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    public async Task<NameAnswer?> AskRsiAsync(string name, SocialConfig config, CancellationToken ct)
    {
        var lookup = await _refresher.LookupWithAffiliatesAsync(
            name, ct, orgs => ColorResolver.IsDecidedByPlayerPage(name, orgs, config)).ConfigureAwait(false);
        return lookup.Status switch
        {
            PlayerLookupStatus.Found => new NameAnswer(RsiState.Verified, lookup.Orgs, !lookup.FromCache),
            PlayerLookupStatus.NotFound => new NameAnswer(RsiState.NotFound, null, !lookup.FromCache),
            _ => null,
        };
    }

    public bool NeedsAffiliateTopUp(string sid)
    {
        try
        {
            return _refresher.NeedsAffiliateTopUp(sid);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    public Task TopUpAffiliateAsync(string sid, CancellationToken ct) => _refresher.TopUpAffiliateAsync(sid, ct);

    public NameAnswer? Provisional(string name, SocialConfig config)
    {
        if (config.Orgs.Count == 0 || (config.OrgsMainOrgOnly && config.OrgRulesMainOrgOnly))
            return null;
        try
        {
            List<OrgMembership>? orgs = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in config.Orgs)
            {
                if (seen.Add(row.Sid) && _database.IsOrgMember(row.Sid, name))
                    (orgs ??= new List<OrgMembership>()).Add(new OrgMembership(row.Sid, false, Name: _database.GetOrg(row.Sid)?.LongName));
            }

            return orgs is null ? null : new NameAnswer(RsiState.Pending, orgs);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    public bool WantsBackgroundRefresh(string name)
    {
        try
        {
            return _refresher.TryClaimStaleRefresh(name);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    public Task RefreshInBackgroundAsync(string name, CancellationToken ct) => _refresher.RefreshStalePlayerAsync(name, ct);

    public int LiveTurnsAvailable => _refresher.LiveTurnsAvailable;

    public void SetLiveWaiting(int count) => _refresher.SetLiveWaiting(count);

    public void NoteSeenOnPlate(string name)
    {
        try
        {
            _database.NoteSeenOnPlate(name, _clock.UtcNow);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
        }
    }

    private IReadOnlyList<OrgMembership> WithCachedAttributes(IReadOnlyList<OrgMembership> orgs) => _refresher.WithCachedAttributes(orgs);
}
