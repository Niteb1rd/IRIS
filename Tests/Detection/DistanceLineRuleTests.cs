using Iris.Core.Detection;
using Xunit;

namespace Iris.Tests.Detection;

public class DistanceLineRuleTests
{
    private static ReadPiece Piece(int start, string text, double score) => new(start, start + 8, text, score, score);

    private static readonly NameplateDetectorSettings Settings = NameplateDetectorSettings.Default;

    [Fact]
    public void TouchingLettersReadAsOnePieceAreNotTrimmedAsJunk()
    {
        var read = new PieceRead(new[] { Piece(0, "4", -0.1), Piece(10, ".", -0.1), Piece(14, "3", -0.1), Piece(24, "km", -1.15) }, 14);

        var reading = DistanceLineRule.Evaluate(read, Settings);

        Assert.NotNull(reading);
        Assert.Equal(4300.0, reading!.Value.Metres, 6);
    }

    [Fact]
    public void AJunkPieceAtTheEndIsStillTrimmed()
    {
        var read = new PieceRead(new[] { Piece(0, "4", -0.1), Piece(10, ".", -0.1), Piece(14, "3", -0.1), Piece(24, "k", -0.1), Piece(34, "m", -0.1), Piece(44, "1", -3.0) }, 14);

        var reading = DistanceLineRule.Evaluate(read, Settings);

        Assert.NotNull(reading);
        Assert.Equal(4300.0, reading!.Value.Metres, 6);
    }

    [Theory]
    [InlineData("4.3km", 4300.0)]
    [InlineData("0.9km", 900.0)]
    [InlineData("850.2m", 850.2)]
    [InlineData("12.0m", 12.0)]
    public void AReadingConvertsToMetres(string text, double metres)
    {
        var reading = DistanceLineRule.Parse(text, 1000);

        Assert.NotNull(reading);
        Assert.Equal(metres, reading!.Value.Metres, 6);
    }
}
