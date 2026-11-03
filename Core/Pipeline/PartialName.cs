namespace Iris.Core.Pipeline;

public sealed record KnownName(string Name, int[] Fingerprint);

public readonly record struct PartialNameMatch(double Score, double Elsewhere, int Known, double CentreX);

public static class PartialName
{
    public static bool TryCompose(IEnumerable<FoundLine> pieces, double left, int width, out double[] values, out bool[] known, out int count)
    {
        values = new double[Math.Max(0, width)];
        known = new bool[values.Length];
        count = 0;
        var start = (int)Math.Round(left);
        foreach (var piece in pieces)
        {
            if (piece.Ink is not { Length: > 0 } ink)
                continue;
            var from = (int)Math.Round(piece.Box.X) - start;
            var columns = Math.Min(ink.Length, (int)Math.Round(piece.Box.Width));
            for (var j = 0; j < columns; j++)
            {
                var c = from + j;
                if (c < 0 || c >= values.Length || known[c])
                    continue;
                values[c] = ink[j];
                known[c] = true;
                count++;
            }
        }

        return count > 0;
    }

    public static double Aligned(double[] values, bool[] known, int[] stored, int maxShift, out int bestShift)
    {
        bestShift = 0;
        var knownCount = 0;
        foreach (var k in known)
            if (k)
                knownCount++;
        var best = double.NegativeInfinity;
        for (var shift = -maxShift; shift <= maxShift; shift++)
        {
            var score = Correlation(values, known, stored, shift, Math.Max(4, (int)Math.Ceiling(0.8 * knownCount)));
            if (score > best)
            {
                best = score;
                bestShift = shift;
            }
        }

        return double.IsNegativeInfinity(best) ? 0.0 : best;
    }

    public static double Elsewhere(double[] values, bool[] known, int[] fingerprint, double stopAt = double.PositiveInfinity)
    {
        var first = Array.IndexOf(known, true);
        var last = Array.LastIndexOf(known, true);
        if (first < 0 || fingerprint.Length < 4)
            return 0.0;
        var span = last - first + 1;
        var knownCount = 0;
        for (var i = first; i <= last; i++)
            if (known[i])
                knownCount++;
        var minimum = Math.Max(4, (int)Math.Ceiling(0.75 * knownCount));
        var best = double.NegativeInfinity;
        for (var offset = -span / 4; offset <= fingerprint.Length - span + span / 4; offset++)
        {
            var score = Correlation(values, known, fingerprint, offset - first, minimum);
            if (score > best)
            {
                best = score;
                if (best >= stopAt)
                    break;
            }
        }

        return double.IsNegativeInfinity(best) ? 0.0 : best;
    }

    public static double Locate(FoundLine piece, int[] stored, out int offset, out double runnerUp)
    {
        offset = 0;
        runnerUp = 0.0;
        if (piece.Ink is not { Length: >= 4 } ink)
            return 0.0;
        var values = new double[ink.Length];
        var known = new bool[ink.Length];
        for (var i = 0; i < ink.Length; i++)
        {
            values[i] = ink[i];
            known[i] = true;
        }

        var minimum = Math.Max(4, (int)Math.Ceiling(0.75 * ink.Length));
        var first = -ink.Length / 4;
        var scores = new double[Math.Max(0, stored.Length - ink.Length + ink.Length / 4 - first + 1)];
        var best = double.NegativeInfinity;
        for (var k = 0; k < scores.Length; k++)
        {
            scores[k] = Correlation(values, known, stored, first + k, minimum);
            if (scores[k] > best)
            {
                best = scores[k];
                offset = first + k;
            }
        }

        if (double.IsNegativeInfinity(best))
            return 0.0;
        var second = double.NegativeInfinity;
        for (var k = 0; k < scores.Length; k++)
            if (Math.Abs(first + k - offset) >= 3)
                second = Math.Max(second, scores[k]);
        runnerUp = double.IsNegativeInfinity(second) ? 0.0 : Math.Max(0.0, second);
        return best;
    }

    public static PartialNameMatch? Match(
        double[] values, bool[] known, int count, double rowLeft, NameLineInfo name, string? ownName, IReadOnlyList<KnownName>? others, int maxShift, PipelineSettings settings)
    {
        var stored = name.Fingerprint;
        if (stored.Length < 4 || count < Math.Max(4, settings.PartialNameMinFraction * stored.Length))
            return null;
        var score = Aligned(values, known, stored, maxShift, out var shift);
        var elsewhere = 0.0;
        if (score >= settings.PartialNameMin && others is not null)
        {
            var stopAt = score - settings.PartialNameMargin;
            foreach (var other in others)
            {
                if (ownName is not null && string.Equals(other.Name, ownName, StringComparison.OrdinalIgnoreCase))
                    continue;
                elsewhere = Math.Max(elsewhere, Elsewhere(values, known, other.Fingerprint, stopAt));
                if (elsewhere >= stopAt)
                    break;
            }
        }

        return new PartialNameMatch(score, elsewhere, count, rowLeft - shift + stored.Length / 2.0);
    }

    public static bool Passes(PartialNameMatch match, PipelineSettings settings) =>
        match.Score >= settings.PartialNameMin && match.Score - match.Elsewhere >= settings.PartialNameMargin;

    private static double Correlation(double[] values, bool[] known, int[] stored, int shift, int minimum)
    {
        var n = 0;
        double sx = 0, sy = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var j = i + shift;
            if (!known[i] || j < 0 || j >= stored.Length)
                continue;
            n++;
            sx += values[i];
            sy += stored[j];
        }

        if (n < minimum)
            return double.NegativeInfinity;
        var mx = sx / n;
        var my = sy / n;
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var j = i + shift;
            if (!known[i] || j < 0 || j >= stored.Length)
                continue;
            var dx = values[i] - mx;
            var dy = stored[j] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }

        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : double.NegativeInfinity;
    }
}
