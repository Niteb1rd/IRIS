using Iris.Core.Config;
using Iris.Core.Lookup;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Iris.Tests.Support;
using Xunit;

namespace Iris.Tests.Pipeline;

public class CacheWorkerTests : IDisposable
{
    private static readonly NameAnswer Verified = new(RsiState.Verified, Array.Empty<OrgMembership>(), true);

    private readonly FakeNameSource _source = new();
    private readonly RecordingSink _sink = new();
    private readonly SocialConfig _config = new();
    private readonly CacheWorker _worker;

    public CacheWorkerTests() => _worker = new CacheWorker(_source, new SessionNames(), _sink, LiveSettings.Default, null, new FakeClock());

    public void Dispose() => _worker.Dispose();

    private static string[] Names(int count) => Enumerable.Range(1, count).Select(i => $"PLAYER{i}").ToArray();

    private static IndexSnapshot Snapshot(params string[] names) => new()
    {
        Epoch = 1,
        Markers = names.Select((name, i) => new MarkerRecord(i + 1, 0.0) { Status = PlateStatus.Confirmed, Name = name, NameMs = 0.0 }).ToArray(),
    };

    private void Step(params string[] names) => _worker.Step(Snapshot(names), _config, 1000.0);

    private async Task AnswerAll(params string[] names)
    {
        var running = _worker.Running!;
        foreach (var name in names)
            _source.Answer(name, Verified);
        await running;
    }

    [Fact]
    public void AGroupOfNewNamesStartsTogether()
    {
        var names = Names(CacheSettings.RsiLiveBurst);

        Step(names);

        Assert.Equal(names.OrderBy(n => n), _source.Asked.OrderBy(n => n));
    }

    [Fact]
    public void NoMoreThanTheBurstRunAtOnce()
    {
        Step(Names(CacheSettings.RsiLiveBurst + 2));

        Assert.Equal(CacheSettings.RsiLiveBurst, _source.Asked.Count);
        Assert.Equal(2, _worker.QueuedLookups);
    }

    [Fact]
    public void AFinishedLookupFreesASlotAsSoonAsTheWorkerIsWoken()
    {
        var names = Names(CacheSettings.RsiLiveBurst + 1);
        Step(names);

        _source.Answer(_source.Asked.First(), Verified);
        Assert.True(_worker.Signal.WaitOne(TimeSpan.FromSeconds(5)));
        Step(names);

        Assert.Equal(CacheSettings.RsiLiveBurst + 1, _source.Asked.Count);
    }

    [Fact]
    public async Task EveryConcurrentAnswerReachesTheOverlay()
    {
        var names = Names(CacheSettings.RsiLiveBurst);
        Step(names);

        await AnswerAll(names);
        _sink.Updates.Clear();
        Step(names);

        var verified = _sink.Updates.OfType<IdentityUpdate>().Where(u => u.Identity?.Rsi == RsiState.Verified).Select(u => u.Identity!.Handle);
        Assert.Equal(names.OrderBy(n => n), verified.OrderBy(n => n));
    }

    [Fact]
    public void TheSameNameOnTwoPlatesIsAskedOnce()
    {
        Step("PLAYER1", "PLAYER1");

        Assert.Single(_source.Asked);
        Assert.Equal(0, _worker.QueuedLookups);
    }

    [Fact]
    public async Task ANameThatLeftTheScreenIsNotLookedUp()
    {
        var names = Names(CacheSettings.RsiLiveBurst + 1);
        Step(names);
        var onScreen = names.Take(CacheSettings.RsiLiveBurst).ToArray();

        await AnswerAll(onScreen);
        Step(onScreen);

        Assert.DoesNotContain(names[^1], _source.Asked);
    }

    [Fact]
    public void ABackgroundRefreshDoesNotHoldUpANewNameOnScreen()
    {
        _source.StoredAnswers["OLDNAME"] = Verified;
        _source.WantRefresh.Add("OLDNAME");
        Step("OLDNAME");
        Assert.Contains("OLDNAME", _source.RefreshesStarted);

        Step("OLDNAME", "NEWNAME");

        Assert.Contains("NEWNAME", _source.Asked);
    }

    [Fact]
    public async Task BackgroundWorkWaitsWhileNamesAreQueued()
    {
        _source.StoredAnswers["OLDNAME"] = Verified;
        _source.WantRefresh.Add("OLDNAME");
        var names = Names(CacheSettings.RsiLiveBurst + 1);
        var all = names.Append("OLDNAME").ToArray();

        Step(all);
        Assert.Empty(_source.RefreshesStarted);

        await AnswerAll(names.Take(CacheSettings.RsiLiveBurst).ToArray());
        Step(all);
        Assert.Contains("OLDNAME", _source.RefreshesStarted);
    }

    [Fact]
    public void OnlyOneBackgroundJobRunsAtATime()
    {
        foreach (var name in new[] { "OLDNAME1", "OLDNAME2" })
        {
            _source.StoredAnswers[name] = Verified;
            _source.WantRefresh.Add(name);
        }

        Step("OLDNAME1", "OLDNAME2");

        Assert.Single(_source.RefreshesStarted);
    }

    [Fact]
    public void OnlyNamesWithAFreeTurnGoOut()
    {
        _source.LiveTurnsAvailable = 2;

        Step(Names(5));

        Assert.Equal(2, _source.Asked.Count);
        Assert.Equal(3, _worker.QueuedLookups);
    }

    [Fact]
    public void NothingGoesOutWithoutAFreeTurn()
    {
        _source.LiveTurnsAvailable = 0;

        Step(Names(3));

        Assert.Empty(_source.Asked);
    }

    [Fact]
    public void APlayerWhoLeavesBeforeTheirTurnNeverCostsARequest()
    {
        _source.LiveTurnsAvailable = 0;
        Step("LEAVER", "STAYER");

        _source.LiveTurnsAvailable = 1;
        Step("STAYER");

        Assert.Equal(new[] { "STAYER" }, _source.Asked);
    }

    [Fact]
    public void ANewArrivalIsNotStuckBehindPlayersWhoLeft()
    {
        _source.LiveTurnsAvailable = 0;
        Step("GONE1", "GONE2", "GONE3");

        _source.LiveTurnsAvailable = 1;
        Step("ARRIVAL");

        Assert.Equal(new[] { "ARRIVAL" }, _source.Asked);
        Assert.Equal(0, _worker.QueuedLookups);
    }

    [Fact]
    public void APlayerWhoComesBackIsLookedUpAgain()
    {
        _source.LiveTurnsAvailable = 0;
        Step("RETURNER");
        Step("SOMEONE");

        _source.LiveTurnsAvailable = 2;
        Step("RETURNER");

        Assert.Contains("RETURNER", _source.Asked);
    }

    [Fact]
    public void PlayersWhoLeftDoNotDelayBackgroundWork()
    {
        _source.LiveTurnsAvailable = 0;
        Step("GONE1", "GONE2");
        _source.StoredAnswers["OLDNAME"] = Verified;
        _source.WantRefresh.Add("OLDNAME");

        Step("OLDNAME");

        Assert.Contains("OLDNAME", _source.RefreshesStarted);
    }

    [Fact]
    public async Task ARequestAlreadySentIsKeptEvenIfThePlayerLeaves()
    {
        Step("PLAYER1");
        var running = _worker.Running!;
        Step("SOMEONE");

        _source.Answer("PLAYER1", Verified);
        await running;
        Step("PLAYER1");

        Assert.Equal(2, _source.Asked.Count);
        Assert.Contains(_sink.Updates.OfType<IdentityUpdate>(), u => u.Identity is { Handle: "PLAYER1", Rsi: RsiState.Verified });
    }

    [Fact]
    public void WaitingPlayersAreCountedAsLiveDemandOnlyWhileOnScreen()
    {
        _source.LiveTurnsAvailable = 0;
        Step(Names(3));
        Assert.Equal(3, _source.LastLiveWaiting);

        Step("PLAYER1");

        Assert.Equal(1, _source.LastLiveWaiting);
    }

    private void StepAt(params (string Name, double Metres)[] plates) => _worker.Step(new IndexSnapshot
    {
        Epoch = 1,
        Markers = plates.Select((plate, i) => new MarkerRecord(i + 1, 0.0) { Status = PlateStatus.Confirmed, Name = plate.Name, NameMs = 0.0, DistanceMetres = plate.Metres }).ToArray(),
    }, _config, 1000.0);

    [Fact]
    public void TheNearestWaitingPlayerGoesFirst()
    {
        _source.LiveTurnsAvailable = 1;

        StepAt(("FARPLAYER", 5000.0), ("NEARPLAYER", 900.0), ("MIDPLAYER", 2000.0));

        Assert.Equal(new[] { "NEARPLAYER" }, _source.Asked);
    }

    [Fact]
    public void ALimitedBurstGoesToTheNearestPlayers()
    {
        _source.LiveTurnsAvailable = 2;

        StepAt(("FARPLAYER", 5000.0), ("NEARPLAYER", 900.0), ("MIDPLAYER", 2000.0));

        Assert.Equal(new[] { "MIDPLAYER", "NEARPLAYER" }, _source.Asked.OrderBy(n => n));
    }

    [Fact]
    public void ACloserNewArrivalGoesAheadOfFartherWaitingPlayers()
    {
        _source.LiveTurnsAvailable = 0;
        StepAt(("FARPLAYER", 5000.0));

        _source.LiveTurnsAvailable = 1;
        StepAt(("FARPLAYER", 5000.0), ("NEARPLAYER", 300.0));

        Assert.Equal(new[] { "NEARPLAYER" }, _source.Asked);
        Assert.Equal(1, _worker.QueuedLookups);
    }

    [Fact]
    public void PlayersWithAKnownDistanceGoBeforeUnknownOnes()
    {
        _source.LiveTurnsAvailable = 1;

        StepAt(("NODISTANCE", double.NaN), ("VERYFARAWAY", 9000.0));

        Assert.Equal(new[] { "VERYFARAWAY" }, _source.Asked);
    }

    [Fact]
    public void EqualDistancesKeepTheirArrivalOrder()
    {
        _source.LiveTurnsAvailable = 0;
        StepAt(("FIRSTSEEN", 1000.0));
        StepAt(("FIRSTSEEN", 1000.0), ("SECONDSEEN", 1000.0));

        _source.LiveTurnsAvailable = 1;
        StepAt(("SECONDSEEN", 1000.0), ("FIRSTSEEN", 1000.0));

        Assert.Equal(new[] { "FIRSTSEEN" }, _source.Asked);
    }

    [Fact]
    public void UnknownDistancesKeepTheirArrivalOrder()
    {
        _source.LiveTurnsAvailable = 1;

        Step("FIRSTSEEN", "SECONDSEEN", "THIRDSEEN");

        Assert.Equal(new[] { "FIRSTSEEN" }, _source.Asked);
    }

    [Fact]
    public void ANameOnTwoPlatesUsesItsNearestDistance()
    {
        _source.LiveTurnsAvailable = 1;

        StepAt(("TWOPLATES", 8000.0), ("MIDPLAYER", 3000.0), ("TWOPLATES", 100.0));

        Assert.Equal(new[] { "TWOPLATES" }, _source.Asked);
    }

    [Fact]
    public void RunningIsNullWhenIdle()
    {
        Assert.Null(_worker.Running);
        Assert.False(_worker.LookupRunning);
    }

    [Fact]
    public async Task RunningCoversEveryLookupInFlight()
    {
        var names = Names(2);
        Step(names);
        var running = _worker.Running!;
        Assert.True(_worker.LookupRunning);

        _source.Answer(names[0], Verified);
        await Task.Delay(20);
        Assert.False(running.IsCompleted);

        _source.Answer(names[1], Verified);
        await running;
        Assert.False(_worker.LookupRunning);
    }

    [Fact]
    public void LiveDemandCountsRunningAndQueuedNames()
    {
        Step(Names(CacheSettings.RsiLiveBurst + 2));

        Assert.Equal(CacheSettings.RsiLiveBurst + 2, _source.LastLiveWaiting);
    }

    [Fact]
    public void ARefreshedAnswerWithTheSameContentIsNotPublishedAgain()
    {
        static NameAnswer Fresh() => new(RsiState.Verified, new[] { new OrgMembership("ORG1", true, "Corporation", new[] { "Bounty" }, "Org One") });
        _source.StoredAnswers["PLAYER1"] = Fresh();
        _worker.Step(Snapshot("PLAYER1"), _config, 1000.0);
        var published = _sink.Updates.OfType<IdentityUpdate>().Single().Identity;
        var seen = new IndexSnapshot { Epoch = 1, Markers = new[] { new MarkerRecord(1, 0.0) { Status = PlateStatus.Confirmed, Name = "PLAYER1", NameMs = 0.0, Identity = published } } };
        _sink.Updates.Clear();
        _source.StoredAnswers["PLAYER1"] = Fresh();

        _worker.Step(seen, _config, 1000.0 + LiveSettings.Default.OrgCacheMs + 1.0);

        Assert.Empty(_sink.Updates.OfType<IdentityUpdate>());
    }

    [Fact]
    public void ARefreshedAnswerWithNewOrgsIsPublished()
    {
        _source.StoredAnswers["PLAYER1"] = new NameAnswer(RsiState.Verified, new[] { new OrgMembership("ORG1", true) });
        _worker.Step(Snapshot("PLAYER1"), _config, 1000.0);
        var published = _sink.Updates.OfType<IdentityUpdate>().Single().Identity;
        var seen = new IndexSnapshot { Epoch = 1, Markers = new[] { new MarkerRecord(1, 0.0) { Status = PlateStatus.Confirmed, Name = "PLAYER1", NameMs = 0.0, Identity = published } } };
        _sink.Updates.Clear();
        _source.StoredAnswers["PLAYER1"] = new NameAnswer(RsiState.Verified, new[] { new OrgMembership("ORG2", true) });

        _worker.Step(seen, _config, 1000.0 + LiveSettings.Default.OrgCacheMs + 1.0);

        Assert.Single(_sink.Updates.OfType<IdentityUpdate>());
    }

    private sealed class RecordingSink : IUpdateSink
    {
        public List<IndexUpdate> Updates { get; } = new();

        public void Submit(IReadOnlyList<IndexUpdate> updates) => Updates.AddRange(updates);
    }
}
