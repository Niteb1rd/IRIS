using Iris.Core.Config;
using Iris.Core.Lookup;
using Iris.Core.Overlay;
using Iris.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Iris.Tests.Lookup;

public class CacheRefresherTests : IDisposable
{
    private const string Shared = "SHAREDORG";

    private readonly FakeClock _clock = new();
    private readonly FakeRsi _rsi = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CacheDatabase _database;
    private readonly CacheRefresher _refresher;

    public CacheRefresherTests()
    {
        _connection.Open();
        _database = new CacheDatabase(_connection, _clock);
        _refresher = new CacheRefresher(_database, _rsi, _clock, new CacheSettings(30), (_, _) => Task.CompletedTask);
        _rsi.Player = handle => RsiResult<PlayerOrgs>.Found(new PlayerOrgs(handle, new[] { new OrgMembership("MAINORG", true), new OrgMembership(Shared, false) }));
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private static RsiResult<OrgInfo> Org(string sid, string name = "Some Org") =>
        RsiResult<OrgInfo>.Found(new OrgInfo(sid, name, 10, "Corporation", null, false, new[] { "Trading" }));

    private RsiLane LaneOf(string kind) => _rsi.Calls.Single(c => c.Kind == kind).Lane;

    [Fact]
    public async Task ALookupForAnOnScreenNameUsesTheLiveLane()
    {
        _rsi.Org = sid => Task.FromResult(Org(sid));

        await _refresher.LookupWithAffiliatesAsync("PLAYER1");

        Assert.Equal(RsiLane.Live, LaneOf("player"));
        Assert.Equal(RsiLane.Live, LaneOf("org"));
    }

    [Fact]
    public async Task OpeningADossierUsesTheLiveLane()
    {
        await _refresher.FetchProfileAsync("PLAYER1");

        Assert.Equal(RsiLane.Live, LaneOf("profile"));
    }

    [Fact]
    public async Task AStaleRefreshUsesTheBackgroundLane()
    {
        await _refresher.RefreshStalePlayerAsync("PLAYER1");

        Assert.Equal(RsiLane.Background, LaneOf("player"));
    }

    [Fact]
    public async Task AnAffiliateTopUpUsesTheBackgroundLane()
    {
        _rsi.Org = sid => Task.FromResult(Org(sid));

        await _refresher.TopUpAffiliateAsync(Shared);

        Assert.Equal(RsiLane.Background, LaneOf("org"));
    }

    [Fact]
    public async Task TwoPlayersSharingAnAffiliateFetchItOnce()
    {
        var release = new TaskCompletionSource<RsiResult<OrgInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rsi.Org = _ => release.Task;

        var first = _refresher.LookupWithAffiliatesAsync("PLAYER1");
        var second = _refresher.LookupWithAffiliatesAsync("PLAYER2");
        await WaitForOrgCall();
        release.SetResult(Org(Shared));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, _rsi.Count("org"));
        Assert.All(results, r => Assert.Equal("Corporation", r.Orgs.Single(o => o.Sid == Shared).Archetype));
    }

    [Fact]
    public async Task AFailedSharedFetchLeavesBothPlayersWithoutAttributes()
    {
        var release = new TaskCompletionSource<RsiResult<OrgInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rsi.Org = _ => release.Task;

        var first = _refresher.LookupWithAffiliatesAsync("PLAYER1");
        var second = _refresher.LookupWithAffiliatesAsync("PLAYER2");
        await WaitForOrgCall();
        release.SetResult(RsiResult<OrgInfo>.Failed("boom"));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, _rsi.Count("org"));
        Assert.All(results, r => Assert.Null(r.Orgs.Single(o => o.Sid == Shared).Archetype));
    }

    [Fact]
    public async Task ASharedFetchIsForgottenOnceFinished()
    {
        _rsi.Org = sid => Task.FromResult(RsiResult<OrgInfo>.Failed("boom"));
        await _refresher.LookupWithAffiliatesAsync("PLAYER1");

        _clock.Advance(CacheSettings.OrgRetryFirst + TimeSpan.FromSeconds(1));
        _rsi.Org = sid => Task.FromResult(Org(sid));
        var later = await _refresher.LookupWithAffiliatesAsync("PLAYER2");

        Assert.Equal(2, _rsi.Count("org"));
        Assert.Equal("Corporation", later.Orgs.Single(o => o.Sid == Shared).Archetype);
    }

    [Fact]
    public async Task CancellingTheCallerThatStartedAnOrgFetchDoesNotFailTheOneWaitingOnIt()
    {
        var first = new TaskCompletionSource<RsiResult<OrgInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        _rsi.Org = sid => Interlocked.Increment(ref asked) == 1 ? first.Task : Task.FromResult(Org(sid));
        using var dossier = new CancellationTokenSource();
        var starter = _refresher.LookupWithAffiliatesAsync("PLAYER1", ct: dossier.Token);
        SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 1, TimeSpan.FromSeconds(5));
        var joiner = _refresher.LookupWithAffiliatesAsync("PLAYER2");

        dossier.Cancel();
        first.SetCanceled(dossier.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        await joiner;
        Assert.Equal(2, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task AFailedStaleRefreshCanBeClaimedAgain()
    {
        await _refresher.LookupWithAffiliatesAsync("PLAYER1");
        _clock.Advance(TimeSpan.FromDays(31));
        _rsi.Player = _ => RsiResult<PlayerOrgs>.Failed("no answer");
        Assert.True(_refresher.TryClaimStaleRefresh("PLAYER1"));

        await _refresher.RefreshStalePlayerAsync("PLAYER1");

        Assert.True(_refresher.TryClaimStaleRefresh("PLAYER1"));
    }

    [Fact]
    public void AListedPlayerIsCheckedAgainOnceNotFoundRetentionEnds()
    {
        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry("PLAYER1", OverlayColor.Red));
        _database.SaveNotFound(NotFoundKind.Player, "PLAYER1", CacheSource.OrgListMember, _clock.UtcNow);
        Assert.Empty(_refresher.FindStale(config).Players);

        _clock.Advance(CacheSettings.NotFoundRetentionFor(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { "PLAYER1" }, _refresher.FindStale(config).Players);
    }

    [Fact]
    public void AListedOrgIsCheckedAgainOnceNotFoundRetentionEnds()
    {
        var config = new SocialConfig();
        config.Orgs.Add(new OrgEntry("ORG1", 1, OverlayColor.Red));
        _database.SaveNotFound(NotFoundKind.Org, "ORG1", CacheSource.OrgListMember, _clock.UtcNow);
        Assert.Empty(_refresher.FindStale(config).Orgs);

        _clock.Advance(CacheSettings.NotFoundRetentionFor(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { "ORG1" }, _refresher.FindStale(config).Orgs);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void ARosterMemberHasAProvisionalColourUnlessMainOrgOnlyRulesItOut(bool orgsMainOnly, bool rulesMainOnly, bool expected)
    {
        _database.SaveOrgListEntry("ORG1", "Org One", new[] { "PLAYER1" }, _clock.UtcNow);
        var config = new SocialConfig { OrgsMainOrgOnly = orgsMainOnly, OrgRulesMainOrgOnly = rulesMainOnly };
        config.Orgs.Add(new OrgEntry("ORG1", 1, OverlayColor.Red));
        var source = new RefresherNameSource(_database, _refresher, _clock);

        Assert.Equal(expected, source.Provisional("PLAYER1", config) is not null);
    }

    [Fact]
    public async Task OrgDetailsAreReusedWhileFresh()
    {
        _rsi.Org = sid => Task.FromResult(Org(sid));
        await _refresher.LookupWithAffiliatesAsync("PLAYER1");

        _clock.Advance(TimeSpan.FromDays(29));
        var later = new CacheRefresher(_database, _rsi, _clock, new CacheSettings(30), (_, _) => Task.CompletedTask);
        await later.LookupWithAffiliatesAsync("PLAYER2");

        Assert.Equal(1, _rsi.Count("org"));
    }

    [Fact]
    public async Task ARenamedOrgIsPickedUpOnceItsDetailsExpire()
    {
        _rsi.Org = sid => Task.FromResult(Org(sid, "Old Name"));
        await _refresher.LookupWithAffiliatesAsync("PLAYER1");

        var fresh = new CacheRefresher(_database, _rsi, _clock, new CacheSettings(30), (_, _) => Task.CompletedTask);
        _clock.Advance(TimeSpan.FromDays(31));
        _rsi.Org = sid => Task.FromResult(Org(sid, "New Name"));
        await fresh.LookupWithAffiliatesAsync("PLAYER2");

        Assert.Equal("New Name", _database.GetOrg(Shared)!.LongName);
    }

    [Fact]
    public async Task ADissolvedOrgIsDroppedWhenItsDetailsExpire()
    {
        _rsi.Org = sid => Task.FromResult(Org(sid));
        await _refresher.LookupWithAffiliatesAsync("PLAYER1");

        var fresh = new CacheRefresher(_database, _rsi, _clock, new CacheSettings(30), (_, _) => Task.CompletedTask);
        _clock.Advance(TimeSpan.FromDays(31));
        _rsi.Org = _ => Task.FromResult(RsiResult<OrgInfo>.NotFound());
        await fresh.LookupWithAffiliatesAsync("PLAYER2");

        Assert.Null(_database.GetOrg(Shared));
    }

    private async Task WaitForOrgCall()
    {
        for (var i = 0; i < 500 && _rsi.Count("org") == 0; i++)
            await Task.Delay(5);
        Assert.True(_rsi.Count("org") > 0);
    }
}
