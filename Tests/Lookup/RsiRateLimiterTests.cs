using Iris.Core.Lookup;
using Iris.Tests.Support;
using Xunit;

namespace Iris.Tests.Lookup;

public class RsiRateLimiterTests
{
    private const int Burst = 5;
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly FakeClock _clock = new();
    private readonly List<TimeSpan> _delays = new();
    private bool _cancelNextDelay;

    private RsiRateLimiter Limiter(int burst = Burst) => new(_clock, (wait, _) =>
    {
        if (_cancelNextDelay)
            throw new OperationCanceledException();
        _delays.Add(wait);
        _clock.Advance(wait);
        return Task.CompletedTask;
    }, Second, burst);

    [Fact]
    public async Task ABackwardClockStepDoesNotStallLookups()
    {
        var limiter = Limiter();
        await Live(limiter, Burst);
        _clock.UtcNow -= TimeSpan.FromHours(1);
        _delays.Clear();

        await limiter.WaitForTurnAsync(allowBurst: true);

        Assert.All(_delays, wait => Assert.True(wait <= Second * Burst, $"waited {wait}"));
    }

    [Fact]
    public async Task ABackwardClockStepKeepsAnAskedPause()
    {
        var limiter = Limiter();
        limiter.ReportThrottled(TimeSpan.FromSeconds(30));
        _clock.UtcNow -= TimeSpan.FromHours(1);
        _delays.Clear();

        await limiter.WaitForTurnAsync();

        Assert.True(_delays.Sum(d => d.TotalSeconds) is >= 29.0 and <= 31.0, string.Join(",", _delays));
    }

    private static async Task Live(RsiRateLimiter limiter, int count)
    {
        for (var i = 0; i < count; i++)
            await limiter.WaitForTurnAsync(allowBurst: true);
    }

    [Fact]
    public async Task LiveRequestsUpToTheBurstGoOutWithoutWaiting()
    {
        await Live(Limiter(), Burst);

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task TheRequestAfterTheBurstWaitsOneInterval()
    {
        await Live(Limiter(), Burst + 1);

        Assert.Equal(new[] { Second }, _delays);
    }

    [Fact]
    public async Task AfterTheBurstRequestsArePacedAtTheInterval()
    {
        await Live(Limiter(), Burst + 4);

        Assert.Equal(new[] { Second, Second, Second, Second }, _delays);
    }

    [Fact]
    public async Task IdleTimeRefillsTheBurstOneCreditPerInterval()
    {
        var limiter = Limiter();
        await Live(limiter, Burst);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await Live(limiter, 2);
        Assert.Empty(_delays);

        await Live(limiter, 1);
        Assert.Equal(new[] { Second }, _delays);
    }

    [Fact]
    public async Task IdleTimeNeverBanksMoreThanOneBurst()
    {
        var limiter = Limiter();
        _clock.Advance(TimeSpan.FromHours(1));

        await Live(limiter, Burst + 1);

        Assert.Equal(new[] { Second }, _delays);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task NoWindowEverExceedsBurstPlusSustainedRate(int seed)
    {
        var limiter = Limiter();
        var random = new Random(seed);
        var sends = new List<DateTimeOffset>();
        for (var i = 0; i < 400; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(random.NextDouble() * 2.5 * random.Next(0, 2)));
            await limiter.WaitForTurnAsync(allowBurst: true);
            sends.Add(_clock.UtcNow);
        }

        foreach (var seconds in new[] { 1, 2, 5, 10, 20 })
        {
            var window = TimeSpan.FromSeconds(seconds);
            foreach (var start in sends)
                Assert.True(sends.Count(s => s >= start && s < start + window) <= Burst + seconds, $"{seconds}s window from {start:O}");
        }
    }

    [Fact]
    public async Task BackgroundRequestsNeverUseTheBurst()
    {
        var limiter = Limiter();

        await limiter.WaitForTurnAsync();
        await limiter.WaitForTurnAsync();
        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { Second, Second }, _delays);
    }

    [Fact]
    public async Task BackgroundRequestsPayBackADebtLeftByABurst()
    {
        var limiter = Limiter();
        await Live(limiter, Burst);

        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { TimeSpan.FromSeconds(Burst) }, _delays);
    }

    [Fact]
    public async Task SteadyBackgroundTrafficLeavesMostOfTheBurstForLiveNames()
    {
        var limiter = Limiter();
        for (var i = 0; i < 10; i++)
            await limiter.WaitForTurnAsync();
        _delays.Clear();

        await Live(limiter, Burst - 1);
        Assert.Empty(_delays);

        await Live(limiter, 1);
        Assert.Equal(new[] { Second }, _delays);
    }

    [Fact]
    public async Task BeingThrottledTurnsTheBurstOff()
    {
        var limiter = Limiter();

        limiter.ReportThrottled();
        await Live(limiter, 3);

        var gap = TimeSpan.FromSeconds(2);
        Assert.Equal(new[] { gap, gap, gap }, _delays);
    }

    [Fact]
    public async Task TheBurstStaysOffForTheCooldownEvenAfterRecovering()
    {
        var limiter = Limiter();
        limiter.ReportThrottled();
        limiter.ReportSuccess();
        Assert.Equal(Second, limiter.CurrentGap);

        _clock.Advance(CacheSettings.RsiBurstCooldown - TimeSpan.FromSeconds(10));
        await Live(limiter, 2);

        Assert.Equal(new[] { Second }, _delays);
    }

    [Fact]
    public async Task TheBurstReturnsAfterTheCooldown()
    {
        var limiter = Limiter();
        limiter.ReportThrottled();
        limiter.ReportSuccess();

        _clock.Advance(CacheSettings.RsiBurstCooldown + TimeSpan.FromSeconds(1));
        await Live(limiter, Burst);

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task SeveralThrottlesFromOneBurstCountAsOneEvent()
    {
        var limiter = Limiter();
        var turns = new List<long>();
        for (var i = 0; i < Burst; i++)
            turns.Add(await limiter.WaitForTurnAsync(allowBurst: true));

        foreach (var turn in turns)
            limiter.ReportThrottled(null, turn);

        Assert.Equal(TimeSpan.FromSeconds(2), limiter.CurrentGap);
    }

    [Fact]
    public async Task AThrottleFromALaterTurnDoublesAgain()
    {
        var limiter = Limiter();
        limiter.ReportThrottled(null, await limiter.WaitForTurnAsync(allowBurst: true));

        limiter.ReportThrottled(null, await limiter.WaitForTurnAsync(allowBurst: true));

        Assert.Equal(TimeSpan.FromSeconds(4), limiter.CurrentGap);
    }

    [Fact]
    public async Task ALateThrottleStillHonoursItsRetryAfter()
    {
        var limiter = Limiter();
        var stale = await limiter.WaitForTurnAsync(allowBurst: true);
        limiter.ReportThrottled();

        limiter.ReportThrottled(TimeSpan.FromSeconds(30), stale);
        await Live(limiter, 1);

        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, _delays);
        Assert.Equal(TimeSpan.FromSeconds(2), limiter.CurrentGap);
    }

    [Fact]
    public async Task AStaleSuccessDoesNotEndABackoff()
    {
        var limiter = Limiter();
        var stale = await limiter.WaitForTurnAsync(allowBurst: true);
        limiter.ReportThrottled();

        limiter.ReportSuccess(stale);

        Assert.Equal(TimeSpan.FromSeconds(2), limiter.CurrentGap);
    }

    [Fact]
    public async Task ACancelledWaitGivesItsSlotBack()
    {
        var limiter = Limiter();
        await Live(limiter, Burst);

        _cancelNextDelay = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Live(limiter, 1));

        _cancelNextDelay = false;
        await Live(limiter, 1);
        Assert.Equal(new[] { Second }, _delays);
    }

    [Fact]
    public void AnIdleLimiterOffersTheWholeBurst()
    {
        Assert.Equal(Burst, Limiter().AvailableNow(allowBurst: true));
    }

    [Fact]
    public void BackgroundCallersAreOfferedOneTurnAtMost()
    {
        Assert.Equal(1, Limiter().AvailableNow());
    }

    [Fact]
    public async Task EachTurnTakenReducesWhatIsOffered()
    {
        var limiter = Limiter();

        await Live(limiter, 3);

        Assert.Equal(Burst - 3, limiter.AvailableNow(allowBurst: true));
    }

    [Fact]
    public async Task NothingIsOfferedOnceTheBurstIsSpent()
    {
        var limiter = Limiter();

        await Live(limiter, Burst);

        Assert.Equal(0, limiter.AvailableNow(allowBurst: true));
    }

    [Fact]
    public async Task OneTurnReturnsPerIntervalAfterASpentBurst()
    {
        var limiter = Limiter();
        await Live(limiter, Burst);

        _clock.Advance(Second);
        Assert.Equal(1, limiter.AvailableNow(allowBurst: true));

        _clock.Advance(Second);
        Assert.Equal(2, limiter.AvailableNow(allowBurst: true));
    }

    [Fact]
    public async Task WhatIsOfferedCanAlwaysBeTakenWithoutWaiting()
    {
        var limiter = Limiter();
        await Live(limiter, 2);
        _clock.Advance(Second);

        await Live(limiter, limiter.AvailableNow(allowBurst: true));

        Assert.Empty(_delays);
    }

    [Fact]
    public void AskingWhatIsAvailableTakesNothing()
    {
        var limiter = Limiter();

        limiter.AvailableNow(allowBurst: true);

        Assert.Equal(Burst, limiter.AvailableNow(allowBurst: true));
    }

    [Fact]
    public void NothingIsOfferedDuringABackoffWait()
    {
        var limiter = Limiter();

        limiter.ReportThrottled();

        Assert.Equal(0, limiter.AvailableNow(allowBurst: true));
    }

    [Fact]
    public void OneTurnAtATimeIsOfferedDuringTheCooldown()
    {
        var limiter = Limiter();
        limiter.ReportThrottled();
        limiter.ReportSuccess();

        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(1, limiter.AvailableNow(allowBurst: true));
    }

    [Fact]
    public void TheDefaultBurstIsConfiguredAndSmall()
    {
        Assert.InRange(CacheSettings.RsiLiveBurst, 2, 8);
    }
}
