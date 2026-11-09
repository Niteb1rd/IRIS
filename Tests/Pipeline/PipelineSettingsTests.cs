using Iris.Core.Pipeline;
using Xunit;

namespace Iris.Tests.Pipeline;

public class PipelineSettingsTests
{
    private static readonly PipelineSettings Defaults = PipelineSettings.Default;

    [Fact]
    public void TheNameScanRunsEightTimesASecond()
    {
        Assert.Equal(8.0, Defaults.ScanHz);
        Assert.Equal(1000.0 / Defaults.ScanHz, Defaults.ScanRequestMs, 1.0);
    }

    [Fact]
    public void PlateChecksKeepTheirFourPerSecondPace()
    {
        Assert.Equal(4.0, Defaults.PlateCheckHz);
    }

    [Fact]
    public void KnownPlayersNeedTwoReadsAndOthersThree()
    {
        Assert.Equal(2, Defaults.KnownConfirmReads);
        Assert.Equal(3, Defaults.ConfirmReads);
    }

    [Fact]
    public void TwoKnownReadsCanSpanOneReadTickEvenWhenItRunsEarly()
    {
        var period = 1000.0 / Defaults.PlateHz;

        Assert.True(Defaults.KnownConfirmSpanMs <= period - PipelineSettings.SlackMs(period));
    }

    [Fact]
    public void TheDefaultsAreValid()
    {
        Assert.Null(Defaults.Validate());
    }

    [Fact]
    public void ChecksMustRunOnSomeScans()
    {
        Assert.NotNull((Defaults with { PlateCheckHz = 0.0 }).Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void KnownReadsMustBeBetweenOneAndTheNormalCount(int reads)
    {
        Assert.NotNull((Defaults with { KnownConfirmReads = reads }).Validate());
    }

    [Fact]
    public void TheKnownSpanMustNotBeNegative()
    {
        Assert.NotNull((Defaults with { KnownConfirmSpanMs = -1.0 }).Validate());
    }
}
