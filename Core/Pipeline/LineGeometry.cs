using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed record NameLineInfo(double Width, double Height, double Gap, PlateColor? Colour, int[] Fingerprint);

public sealed record FoundLine(
    PixelRect Box, int[]? Ink = null, PixelRect? Part = null, PlateColor? Colour = null, bool Narrow = false, PixelRect? Found = null, double CentreSlack = 0.0, double? NameCentreX = null)
{
    public bool IsUnion => Part is not null;

    public bool IsPartial => Found is not null;

    public double CentreX => Box.X + Box.Width / 2.0;
}

public static class PartialLine
{
    public static double StoredWidth(MarkerRecord marker) => marker.DistanceWidth > 0.0 ? marker.DistanceWidth : marker.Width;

    public static bool IsShorter(double lineWidth, double storedWidth, double lineHeight, PipelineSettings settings) =>
        lineWidth < storedWidth - settings.TrackWidthGateLines * lineHeight && lineWidth >= settings.PartialLineMinFraction * storedWidth;

    public static bool LeftEdgeFits(double lineLeft, double predictedLeft, double lineHeight, PipelineSettings settings) =>
        Math.Abs(lineLeft - predictedLeft) <= Math.Max(settings.PartialEdgeMinPx, settings.PartialEdgeToleranceLines * lineHeight);

    public static bool HasSpeedGroup(MarkerRecord marker, PipelineSettings settings) =>
        marker.Width > StoredWidth(marker) + settings.TrackWidthGateLines * marker.Height;

    public static FoundLine AnchoredWhole(FoundLine part, double wholeWidth, double centreSlack) =>
        new(new PixelRect(part.Box.X, part.Box.Y, wholeWidth, part.Box.Height), null, part.Box, part.Colour, false, part.Box, centreSlack);

    public static FoundLine Anchored(FoundLine line, double storedWidth) =>
        new(new PixelRect(line.Box.X, line.Box.Y, storedWidth, line.Box.Height), null, null, line.Colour, false, line.Box);
}

public static class LineSplitter
{
    public static IReadOnlyList<FoundLine> Split(FoundLine line, double lineHeight, double minGapLines)
    {
        if (line.Ink is not { Length: > 0 } ink)
            return Array.Empty<FoundLine>();
        var first = -1;
        var last = -1;
        for (var i = 0; i < ink.Length; i++)
        {
            if (ink[i] <= 0)
                continue;
            if (first < 0)
                first = i;
            last = i;
        }

        if (first < 0 || last <= first)
            return Array.Empty<FoundLine>();

        var minGap = minGapLines * lineHeight;
        var gapStart = -1;
        var gapLength = 0;
        for (var i = first + 1; i < last; i++)
        {
            if (ink[i] > 0)
                continue;
            var end = i;
            while (end + 1 < last && ink[end + 1] <= 0)
                end++;
            var length = end - i + 1;
            if (length >= minGap && length > gapLength)
            {
                gapStart = i;
                gapLength = length;
            }

            i = end;
        }

        if (gapStart < 0)
            return Array.Empty<FoundLine>();

        var gapEnd = gapStart + gapLength - 1;
        var padLeft = first;
        var padRight = ink.Length - 1 - last;
        return new[]
        {
            Piece(line, ink, 0, Math.Min(ink.Length - 1, gapStart - 1 + padRight)),
            Piece(line, ink, Math.Max(0, gapEnd + 1 - padLeft), ink.Length - 1),
        };
    }

    private static FoundLine Piece(FoundLine line, int[] ink, int from, int to) =>
        new(new PixelRect(line.Box.X + from, line.Box.Y, to - from + 1, line.Box.Height), ink[from..(to + 1)], null, line.Colour);
}

public static class Fingerprint
{
    public static double Correlation(int[] a, int[] b, int maxShift = 2)
    {
        if (a.Length < 4 || b.Length < 4)
            return 0.0;
        var x = new double[a.Length];
        for (var i = 0; i < a.Length; i++)
            x[i] = a[i];
        var y = Resample(b, a.Length);
        var best = double.NegativeInfinity;
        for (var shift = -maxShift; shift <= maxShift; shift++)
        {
            var from = Math.Max(0, shift);
            var to = Math.Min(x.Length, y.Length + shift);
            var n = to - from;
            if (n < Math.Max(4, x.Length / 2))
                continue;
            double meanX = 0.0;
            double meanY = 0.0;
            for (var i = from; i < to; i++)
            {
                meanX += x[i];
                meanY += y[i - shift];
            }

            meanX /= n;
            meanY /= n;
            double sxy = 0.0;
            double sxx = 0.0;
            double syy = 0.0;
            for (var i = from; i < to; i++)
            {
                var dx = x[i] - meanX;
                var dy = y[i - shift] - meanY;
                sxy += dx * dy;
                sxx += dx * dx;
                syy += dy * dy;
            }

            var score = sxx > 0.0 && syy > 0.0 ? sxy / Math.Sqrt(sxx * syy) : 0.0;
            best = Math.Max(best, score);
        }

        return double.IsNegativeInfinity(best) ? 0.0 : best;
    }

    private static double[] Resample(int[] values, int length)
    {
        var result = new double[length];
        if (values.Length == length)
        {
            for (var i = 0; i < length; i++)
                result[i] = values[i];
            return result;
        }

        for (var i = 0; i < length; i++)
        {
            var at = length == 1 ? 0.0 : i * (values.Length - 1.0) / (length - 1.0);
            var lo = (int)Math.Floor(at);
            var hi = Math.Min(values.Length - 1, lo + 1);
            var t = at - lo;
            result[i] = values[lo] * (1.0 - t) + values[hi] * t;
        }

        return result;
    }
}
