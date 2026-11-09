using Iris.Core.Detection;
using Xunit;

namespace Iris.Tests.Detection;

public class NameplateReaderTests
{
    private const int Height = 40;
    private const double FailedPieceScore = -50.0;

    private static readonly NameplateReader Reader = new(LetterModel.LoadEmbedded());

    private static LineMask FusedPiece(int width)
    {
        var data = new byte[Height * width];
        for (var x = 0; x < width; x++)
        {
            var ink = x == 0 ? Height : new[] { 6, 8, 10, 8 }[x % 4];
            for (var y = Height - ink; y < Height; y++)
                data[y * width + x] = 1;
        }

        return new LineMask(Height, width, data);
    }

    private static int LetterGuesses(LineMask mask)
    {
        var cap = Height;
        var columns = new int[mask.Width];
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
                columns[x] += mask.Data[y * mask.Width + x];
        var cuts = new List<int> { 0 };
        for (var x = 2; x < mask.Width - 2; x++)
            if (columns[x] <= columns[x - 1] && columns[x] <= columns[x + 1] && columns[x] <= 0.5 * cap)
                cuts.Add(x);
        cuts.Add(mask.Width);

        var guesses = 0;
        for (var i = 0; i < cuts.Count; i++)
            for (var j = i + 1; j < cuts.Count; j++)
            {
                var span = cuts[j] - cuts[i];
                if (span > 1.2 * cap)
                    break;
                if (span >= 0.25 * cap)
                    guesses++;
            }

        return guesses;
    }

    [Fact]
    public void ASinglePieceMayTakeAtMost500LetterGuesses()
    {
        Assert.Equal(500, NameplateDetectorSettings.MaxSplitSpans);
    }

    [Fact]
    public void APieceNeedingTooManyGuessesIsRefusedWithoutTryingThem()
    {
        var mask = FusedPiece(300);
        Assert.InRange(LetterGuesses(mask), 501, 1500);

        var piece = Assert.Single(Reader.ReadPieces(mask).Pieces);

        Assert.Equal("", piece.Text);
        Assert.Equal(FailedPieceScore, piece.Score);
    }

    [Fact]
    public void APieceWithinTheLimitIsStillRead()
    {
        var mask = FusedPiece(150);
        Assert.InRange(LetterGuesses(mask), 200, 500);

        var piece = Assert.Single(Reader.ReadPieces(mask).Pieces);

        Assert.NotEqual("", piece.Text);
    }
}
