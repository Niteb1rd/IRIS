using Iris.Core.Overlay;
using Xunit;

namespace Iris.Tests.Overlay;

public class ResourceOverlayPlacementTests
{
    private const int Width = 170;
    private const int Height = 11;
    private static readonly PixelRect Screen = new(0, 0, 2560, 1440);

    [Fact]
    public void ByDefaultItSitsJustBelowTheTopRightCorner()
    {
        var (x, y) = ResourceOverlayPlacement.Place(Screen, Width, Height, ResourceOverlayPlacement.Unset, ResourceOverlayPlacement.Unset);

        Assert.Equal(2560 - Width - ResourceOverlayPlacement.DefaultRightPx, x);
        Assert.Equal(ResourceOverlayPlacement.DefaultTopPx, y);
        Assert.InRange(ResourceOverlayPlacement.DefaultTopPx, 1, 20);
    }

    [Fact]
    public void ADefaultReadoutFitsAboveTheSidePanel()
    {
        const int defaultPanelTopGapPercent = 5;

        Assert.True(ResourceOverlayPlacement.DefaultTopPx + Height * ResourceOverlayPlacement.GlyphScale(1080) < 1080 * defaultPanelTopGapPercent / 100);
    }

    [Fact]
    public void ItFollowsAScreenThatDoesNotStartAtZero()
    {
        var (x, y) = ResourceOverlayPlacement.Place(new PixelRect(2560, 100, 1920, 1080), Width, Height, ResourceOverlayPlacement.Unset, ResourceOverlayPlacement.Unset);

        Assert.Equal(2560 + 1920 - Width - ResourceOverlayPlacement.DefaultRightPx, x);
        Assert.Equal(100 + ResourceOverlayPlacement.DefaultTopPx, y);
    }

    [Fact]
    public void AMovedReadoutKeepsItsPlaceMeasuredFromTheTopRight()
    {
        var (right, top) = ResourceOverlayPlacement.Offsets(Screen, Width, Height, 900.0, 400.0);

        var (x, y) = ResourceOverlayPlacement.Place(Screen, Width, Height, right, top);

        Assert.Equal((900, 400), (x, y));
    }

    [Theory]
    [InlineData(-500.0, -500.0, 0, 0)]
    [InlineData(5000.0, 5000.0, 2560 - Width, 1440 - Height)]
    public void DraggingCannotPutItOffScreen(double dragX, double dragY, int expectedX, int expectedY)
    {
        var (right, top) = ResourceOverlayPlacement.Offsets(Screen, Width, Height, dragX, dragY);

        Assert.Equal((expectedX, expectedY), ResourceOverlayPlacement.Place(Screen, Width, Height, right, top));
    }

    [Fact]
    public void ASavedPlaceOutsideASmallerScreenIsBroughtBack()
    {
        var (x, y) = ResourceOverlayPlacement.Place(new PixelRect(0, 0, 1280, 720), Width, Height, 2000, 2000);

        Assert.Equal((0, 720 - Height), (x, y));
    }

    [Theory]
    [InlineData(720, 1)]
    [InlineData(1080, 1)]
    [InlineData(1440, 1)]
    [InlineData(2160, 2)]
    [InlineData(4320, 3)]
    public void TheTextStaysVerySmallAtCommonSizes(int windowHeight, int expected)
    {
        Assert.Equal(expected, ResourceOverlayPlacement.GlyphScale(windowHeight));
    }
}
