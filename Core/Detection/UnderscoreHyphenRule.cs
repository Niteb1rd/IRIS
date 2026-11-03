namespace Iris.Core.Detection;

public readonly record struct BarMeasurement(double Frac, double Thick, double Length, int FirstRow = -1, int LastRow = -1);

public static class UnderscoreHyphenRule
{
    public static BarMeasurement? Measure(LineMask m, int start, int end, CapBand band)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, m.Width);
        if (end <= start)
            return null;

        var rowSums = new int[m.Height];
        var max = 0;
        for (var y = 0; y < m.Height; y++)
        {
            var sum = 0;
            for (var x = start; x < end; x++)
                sum += m.Data[y * m.Width + x];
            rowSums[y] = sum;
            if (sum > max)
                max = sum;
        }

        if (max <= 0)
            return null;

        var limit = NameplateDetectorSettings.BarRowFraction * max;
        int r0 = -1, r1 = -1;
        for (var y = 0; y < m.Height; y++)
        {
            if (rowSums[y] < limit)
                continue;
            if (r0 < 0)
                r0 = y;
            r1 = y;
        }

        int c0 = -1, c1 = -1;
        for (var x = start; x < end; x++)
        {
            var ink = false;
            for (var y = r0; y <= r1 && !ink; y++)
                ink = m.Data[y * m.Width + x] != 0;
            if (!ink)
                continue;
            if (c0 < 0)
                c0 = x;
            c1 = x;
        }

        var cap = (double)Math.Max(1, band.Bottom - band.Top + 1);
        var frac = ((r0 + r1 + 1) / 2.0 - band.Top) / cap;
        var thick = (r1 - r0 + 1) / cap;
        var length = c0 < 0 ? 0.0 : (c1 - c0 + 1) / cap;
        return new BarMeasurement(frac, thick, length, r0, r1);
    }

    public static char? Verdict(BarMeasurement bar)
    {
        if (bar.Thick > NameplateDetectorSettings.BarMaxThick)
            return null;
        if (bar.Frac >= NameplateDetectorSettings.BarUnderscoreMin)
            return '_';
        if (bar.Frac <= NameplateDetectorSettings.BarHyphenMax)
            return '-';
        return null;
    }

    public static string Correct(PieceRead read, string text)
    {
        if (read.Mask is null || read.CharacterSpans is null || read.CharacterSpans.Count != text.Length)
            return text;
        if (text.IndexOf('_') < 0 && text.IndexOf('-') < 0)
            return text;
        var band = NameplateFeatures.FindCapBand(read.Mask);
        var chars = text.ToCharArray();
        for (var k = 0; k < chars.Length; k++)
        {
            if (chars[k] != '_' && chars[k] != '-')
                continue;
            var (start, end) = read.CharacterSpans[k];
            if (Measure(read.Mask, start, end, band) is not { } bar)
                continue;
            if (Verdict(bar) is { } verdict)
                chars[k] = verdict;
        }

        return new string(chars);
    }
}
