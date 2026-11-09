using Iris.Core.Detection;
using Xunit;

namespace Iris.Tests.Detection;

public class MaskBandsTests
{
    [Theory]
    [InlineData(1008, 2, 72, 4)]
    [InlineData(1008, 3, 72, 4)]
    [InlineData(756, 2, 54, 3)]
    [InlineData(1512, 4, 108, 6)]
    public void KeptRowsCoverEveryRowExactlyOnce(int rows, int bands, int overlap, int alignment)
    {
        var plan = MaskBands.Plan(rows, bands, overlap, alignment);

        var covered = new int[rows];
        foreach (var band in plan)
            for (var row = band.KeepFrom; row < band.KeepFrom + band.KeepRows; row++)
                covered[row]++;

        Assert.Equal(bands, plan.Count);
        Assert.All(covered, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(1008, 2, 72, 4)]
    [InlineData(1008, 3, 72, 4)]
    [InlineData(756, 2, 54, 3)]
    public void EveryBandStartsAndEndsOnTheAlignmentGrid(int rows, int bands, int overlap, int alignment)
    {
        foreach (var band in MaskBands.Plan(rows, bands, overlap, alignment))
        {
            Assert.Equal(0, band.From % alignment);
            Assert.Equal(0, band.Rows % alignment);
        }
    }

    [Fact]
    public void KeptRowsSitAtLeastTheOverlapInsideAnyInnerBandEdge()
    {
        const int rows = 1008, overlap = 72;

        foreach (var band in MaskBands.Plan(rows, 3, overlap, 4))
        {
            if (band.From > 0)
                Assert.True(band.KeepFrom - band.From >= overlap);
            if (band.From + band.Rows < rows)
                Assert.True(band.From + band.Rows - (band.KeepFrom + band.KeepRows) >= overlap);
        }
    }

    [Fact]
    public void BandsStayInsideTheImage()
    {
        foreach (var band in MaskBands.Plan(1008, 3, 72, 4))
        {
            Assert.True(band.From >= 0);
            Assert.True(band.From + band.Rows <= 1008);
        }
    }

    [Theory]
    [InlineData(1007, 2, 72, 4)]
    [InlineData(100, 2, 72, 4)]
    [InlineData(1008, 1, 72, 4)]
    public void AnImageThatCannotBeSplitExactlyStaysWhole(int rows, int bands, int overlap, int alignment)
    {
        var plan = MaskBands.Plan(rows, bands, overlap, alignment);

        Assert.Equal(new[] { new MaskBand(0, rows, 0, rows) }, plan);
    }
}
