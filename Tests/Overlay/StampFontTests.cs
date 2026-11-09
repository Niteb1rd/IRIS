using Iris.Core.Overlay;
using Xunit;

namespace Iris.Tests.Overlay;

public class StampFontTests
{
    private static byte[] Drawn(string text, int scale, out int width, out int height)
    {
        (width, height) = StampFont.Measure(text, scale);
        var pixels = new byte[width * height * 4];
        StampFont.DrawOutlined(pixels, width * 4, width, height, text, scale);
        return pixels;
    }

    [Fact]
    public void OnlyTheCharactersAndTheirOutlineAreVisible()
    {
        var pixels = Drawn("8", 1, out var width, out var height);

        Assert.Equal(0, pixels[3]);
        Assert.Equal(0, pixels[(width - 1) * 4 + 3]);
        Assert.Equal(0, pixels[(height - 1) * width * 4 + 3]);
        for (var i = 3; i < pixels.Length; i += 4)
            Assert.True(pixels[i] is 0 or 255, $"partly transparent pixel at {i}");
    }

    [Fact]
    public void CharactersAreWhiteAndFullyOpaque()
    {
        var pixels = Drawn("8", 1, out var width, out _);
        var top = (StampFont.Padding * width + StampFont.Padding + 1) * 4;

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels[top..(top + 4)]);
    }

    [Fact]
    public void ADarkOutlineKeepsCharactersReadableOnBrightGround()
    {
        var pixels = Drawn("-", 1, out var width, out _);
        var above = ((StampFont.Padding + 2) * width + StampFont.Padding + 2) * 4;

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels[above..(above + 4)]);
    }

    [Fact]
    public void ABrokenBufferSizeNeverWritesOutsideIt()
    {
        var pixels = new byte[8 * 4 * 4];

        StampFont.DrawOutlined(pixels, 8 * 4, 8, 4, "CPU 100%", 2);

        Assert.Equal(8 * 4 * 4, pixels.Length);
    }
}
