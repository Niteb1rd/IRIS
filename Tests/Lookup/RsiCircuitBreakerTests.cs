using Iris.Core.Lookup;
using Iris.Tests.Support;
using Xunit;

namespace Iris.Tests.Lookup;

public class RsiCircuitBreakerTests
{
    private readonly FakeClock _clock = new();
    private readonly RsiCircuitBreaker _breaker = new();

    private bool IsOpen() => _breaker.IsOpen(_clock.UtcNow, out _, out _);

    private void FailRounds(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            _breaker.RecordFailure(_clock.UtcNow);
            _clock.Advance(CacheSettings.RsiDefaultInterval);
        }
    }

    [Fact]
    public void AClockStepBackwardsDoesNotStopFailuresCounting()
    {
        FailRounds(CacheSettings.RsiBreakerThreshold - 1);
        _clock.UtcNow -= TimeSpan.FromHours(1);

        _breaker.RecordFailure(_clock.UtcNow);

        Assert.True(IsOpen());
    }

    [Fact]
    public void OneBurstOfFailuresCountsAsASingleFailure()
    {
        for (var i = 0; i < CacheSettings.RsiLiveBurst * 3; i++)
            _breaker.RecordFailure(_clock.UtcNow);

        Assert.False(IsOpen());
    }

    [Fact]
    public void RepeatedFailingRoundsStillOpenTheBreaker()
    {
        FailRounds(CacheSettings.RsiBreakerThreshold);

        Assert.True(IsOpen());
    }

    [Fact]
    public void OneRoundShortOfTheThresholdStaysClosed()
    {
        FailRounds(CacheSettings.RsiBreakerThreshold - 1);

        Assert.False(IsOpen());
    }

    [Fact]
    public void ASuccessEndsAStreak()
    {
        FailRounds(CacheSettings.RsiBreakerThreshold - 1);

        _breaker.RecordSuccess();
        FailRounds(CacheSettings.RsiBreakerThreshold - 1);

        Assert.False(IsOpen());
    }

    [Fact]
    public void TheBreakerClosesAfterItsPause()
    {
        FailRounds(CacheSettings.RsiBreakerThreshold);

        _clock.Advance(CacheSettings.RsiBreakerPause);

        Assert.False(IsOpen());
    }
}
