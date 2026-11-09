
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public static partial class TrackAssigner
{
    private static List<FoundLine> Expand(MarkerCandidates input, List<FoundLine> partials, PipelineSettings settings)
    {
        var lines = new List<FoundLine>(input.Lines);
        var marker = input.Marker;
        var gate = settings.TrackWidthGateLines * marker.Height;
        var centre = input.PredictedX + marker.Width / 2.0;
        var stored = DistanceWidthOf(marker);
        foreach (var line in input.Lines)
        {
            if (line.IsUnion || line.Narrow || line.Box.Width <= stored + gate || centre < line.Box.X || centre > line.Box.Right)
                continue;
            var pieces = LineSplitter.Split(line, marker.Height, settings.SplitGapLines);
            lines.AddRange(pieces);
            if (pieces.Count > 0)
                lines.Add(line with { Part = pieces[0].Box });
        }

        lines.AddRange(partials);
        return AnchorWhole(input, lines, settings);
    }

    private static List<FoundLine> AnchorWhole(MarkerCandidates input, List<FoundLine> lines, PipelineSettings settings)
    {
        var marker = input.Marker;
        if (!PartialLine.HasSpeedGroup(marker, settings))
            return lines;
        List<FoundLine>? unions = null;
        foreach (var candidate in lines)
            if (UnionNear(input, candidate, settings))
                (unions ??= new List<FoundLine>()).Add(candidate);
        var result = new List<FoundLine>(lines.Count);
        foreach (var line in lines)
        {
            if (line.IsUnion || line.IsPartial || line.Narrow)
            {
                result.Add(line);
                continue;
            }

            if (unions is not null && unions.Exists(u => u.Part == line.Box))
                continue;
            result.Add(PartialLine.AnchoredWhole(line, marker.Width, 0.5 * (marker.Width - DistanceWidthOf(marker))));
        }

        return result;
    }

    private static bool UnionNear(MarkerCandidates input, FoundLine line, PipelineSettings settings)
    {
        if (!line.IsUnion || line.IsPartial || line.Narrow || !GeometryFits(input.Marker, line, settings, out _))
            return false;
        var dx = line.CentreX - (input.PredictedX + input.Marker.Width / 2.0);
        var dy = line.Box.Y - input.PredictedY;
        return Math.Sqrt(dx * dx + dy * dy) <= Math.Ceiling(input.Margin) + settings.MatchSlackPx;
    }

    private static List<FoundLine> PartialLines(MarkerCandidates input, PipelineSettings settings)
    {
        var shorter = new List<FoundLine>();
        var marker = input.Marker;
        if (marker.Status != PlateStatus.Confirmed || marker.NameLine is null || PartialLine.HasSpeedGroup(marker, settings))
            return shorter;
        foreach (var line in input.Lines)
            if (UnionNear(input, line, settings))
                return shorter;
        var stored = DistanceWidthOf(marker);
        var tolerance = PartialLine.EdgeTolerance(marker, settings);
        foreach (var line in input.Lines)
        {
            if (line.IsUnion || line.Narrow || line.IsPartial || !PartialLine.IsNarrower(line.Box.Width, stored, settings))
                continue;
            if (Math.Abs(line.Box.X - input.PredictedX) <= tolerance)
                shorter.Add(PartialLine.Anchored(line, marker.Width));
            else if (Math.Abs(line.Box.X + line.Box.Width - (input.PredictedX + marker.Width)) <= tolerance)
                shorter.Add(PartialLine.AnchoredRight(line, marker.Width));
        }

        AddCoveredLines(input, settings, shorter, stored, tolerance);
        return shorter;
    }

    private static void AddCoveredLines(MarkerCandidates input, PipelineSettings settings, List<FoundLine> result, double stored, double tolerance)
    {
        var marker = input.Marker;
        var lh = marker.Height;
        var gate = settings.TrackWidthGateLines * lh;
        var heightTolerance = Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * lh);
        var plain = input.Lines.Where(l => !l.IsUnion && !l.Narrow && !l.IsPartial && Math.Abs(l.Box.Height - lh) <= heightTolerance).ToList();
        foreach (var line in plain)
        {
            if (line.Box.Width <= stored + gate)
                continue;
            if (Math.Abs(line.Box.X - input.PredictedX) <= tolerance)
                result.Add(PartialLine.Anchored(line, marker.Width));
            else if (Math.Abs(line.Box.X + line.Box.Width - (input.PredictedX + marker.Width)) <= tolerance)
                result.Add(PartialLine.AnchoredRight(line, marker.Width));
        }

        for (var i = 0; i < plain.Count; i++)
        {
            for (var j = 0; j < plain.Count; j++)
            {
                var left = plain[i].Box;
                var right = plain[j].Box;
                var gap = right.X - left.Right;
                if (i == j || gap < 1.0 || gap > 0.5 * stored || Math.Abs(left.Y - right.Y) > settings.LineHeightMinTolerancePx)
                    continue;
                var span = right.Right - left.X;
                if (Math.Abs(span - stored) > gate || Math.Abs(left.X - input.PredictedX) > Math.Max(tolerance, gate))
                    continue;
                var top = Math.Min(left.Y, right.Y);
                var joined = new PixelRect(left.X, top, span, Math.Max(left.Bottom, right.Bottom) - top);
                if (!result.Exists(r => r.Box == joined))
                    result.Add(new FoundLine(joined));
            }
        }
    }
}
