using Iris.Core.Pipeline;
using Xunit;

namespace Iris.Tests.Pipeline;

public class RateClockTests
{
    [Fact]
    public void AnEarlyTickPushesTheNextTickAFullPeriodAway()
    {
        var clock = new RateClock(10.0);
        clock.TryTick(0.0, out _);

        clock.TickNow(60.0);

        Assert.False(clock.TryTick(100.0, out _));
        Assert.True(clock.TryTick(160.0, out _));
    }
}
