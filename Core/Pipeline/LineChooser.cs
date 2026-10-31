using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public static class BoxMath
{
    public static double Overlap(PixelRect a, PixelRect b)
    {
        var iw = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var ih = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        if (iw <= 0 || ih <= 0)
            return 0.0;
        var inter = iw * ih;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }

    public static bool Intersects(PixelRect a, PixelRect b) =>
        Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 0 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > 0;
}

public static class LineChooser
{
    public static PixelRect? Choose(
        IReadOnlyList<PixelRect> lines, double predictedX, double predictedY, double lineHeight, double margin,
        IReadOnlyList<PixelRect> claimed, PipelineSettings settings, double predictedWidth = double.NaN)
    {
        var byCentre = !double.IsNaN(predictedWidth);
        var predictedAt = byCentre ? predictedX + predictedWidth / 2.0 : predictedX;
        var tolerance = Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * lineHeight);
        var reach = Math.Ceiling(margin) + settings.MatchSlackPx;
        PixelRect? best = null;
        var bestDistance = double.MaxValue;
        foreach (var line in lines)
        {
            if (Math.Abs(line.Height - lineHeight) > tolerance || IsClaimed(line, claimed, settings.ClaimedOverlap))
                continue;
            var lineX = byCentre ? line.X + line.Width / 2.0 : line.X;
            var distance = Math.Sqrt((lineX - predictedAt) * (lineX - predictedAt) + (line.Y - predictedY) * (line.Y - predictedY));
            if (distance > reach || distance >= bestDistance)
                continue;
            best = line;
            bestDistance = distance;
        }

        if (best is not { } near)
            return null;

        PixelRect? lower = null;
        foreach (var line in lines)
        {
            var below = line.Y - near.Y;
            if (Math.Abs(line.Height - lineHeight) > tolerance || below < settings.LowerLineMinLines * lineHeight || below > settings.LowerLineMaxLines * lineHeight)
                continue;
            var shared = Math.Min(line.Right, near.Right) - Math.Max(line.X, near.X);
            if (shared < settings.LowerLineSharedFraction * Math.Min(line.Width, near.Width) || IsClaimed(line, claimed, settings.ClaimedOverlap))
                continue;
            if (lower is null || line.Y < lower.Value.Y)
                lower = line;
        }

        return lower ?? near;
    }

    private static bool IsClaimed(PixelRect line, IReadOnlyList<PixelRect> claimed, double limit)
    {
        foreach (var other in claimed)
            if (BoxMath.Overlap(line, other) > limit)
                return true;
        return false;
    }
}
