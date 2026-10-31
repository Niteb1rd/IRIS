using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed record MarkerCandidates(
    MarkerRecord Marker, double PredictedX, double PredictedY, double Margin, PixelRect Patch, IReadOnlyList<FoundLine> Lines, bool Lost = false);

public enum NameGate
{
    NotFound,
    Width,
    Gap,
    Centre,
}

public readonly record struct NameGateCounts(int Width, int Gap, int Centre, int NotFound)
{
    public int Total => Width + Gap + Centre + NotFound;
}

public sealed record Assignment(
    IReadOnlyDictionary<int, FoundLine> Chosen, bool Contested, int Overrides, NameGateCounts NameFailures, int ColourMismatches = 0, int LowFingerprints = 0,
    IReadOnlyDictionary<int, int>? Places = null, int LostRefused = 0, IReadOnlyList<LostRefusalSample>? LostRefusals = null)
{
    public int NameCheckFailed => NameFailures.Total;
}

public static class Hungarian
{
    public static int[] Solve(double[,] cost)
    {
        var n = cost.GetLength(0);
        var m = cost.GetLength(1);
        if (n > m)
            throw new ArgumentException("More rows than columns.", nameof(cost));
        var u = new double[n + 1];
        var v = new double[m + 1];
        var p = new int[m + 1];
        var way = new int[m + 1];
        var minv = new double[m + 1];
        var used = new bool[m + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            Array.Fill(minv, double.PositiveInfinity);
            Array.Clear(used);
            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = 0;
                for (var j = 1; j <= m; j++)
                {
                    if (used[j])
                        continue;
                    var current = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (current < minv[j])
                    {
                        minv[j] = current;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (var j = 0; j <= m; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var result = new int[n];
        for (var j = 1; j <= m; j++)
            if (p[j] != 0)
                result[p[j] - 1] = j - 1;
        return result;
    }
}

public static class TrackAssigner
{
    private const double Forbidden = 1e6;

    private enum Verdict
    {
        Invalid,
        NameFailed,
        Valid,
    }

    private readonly record struct Evaluation(
        Verdict Verdict, double Cost, double Distance, NameGate Gate, bool ColourMismatch, bool LowFingerprint, FoundLine? Line = null,
        LostRefusalReason Reason = LostRefusalReason.NoNameRow, double Score = double.NaN);

    private readonly record struct NameCheck(
        bool Skipped, NameGate? Failed, double Cost, bool ColourMismatch, bool LowFingerprint, double? NameCentreX = null,
        LostRefusalReason Reason = LostRefusalReason.NoNameRow, double Score = double.NaN);

    private sealed class Edge
    {
        public Edge(int node, FoundLine line, Evaluation evaluation)
        {
            Node = node;
            Line = line;
            Cost = evaluation.Cost;
            Distance = evaluation.Distance;
            ColourMismatch = evaluation.ColourMismatch;
            LowFingerprint = evaluation.LowFingerprint;
        }

        public bool ColourMismatch { get; }

        public bool LowFingerprint { get; }

        public int Node { get; }

        public FoundLine Line { get; }

        public double Cost { get; }

        public double Distance { get; }
    }

    public static Assignment Assign(IReadOnlyList<MarkerCandidates> inputs, IReadOnlyList<PixelRect> claimed, PipelineSettings settings, double frameMs = double.NaN)
    {
        var chosen = new Dictionary<int, FoundLine>();
        var contested = false;
        for (var i = 0; i < inputs.Count && !contested; i++)
            for (var j = i + 1; j < inputs.Count; j++)
                if (BoxMath.Intersects(inputs[i].Patch, inputs[j].Patch))
                {
                    contested = true;
                    break;
                }

        var nodes = new List<PixelRect>();
        var edges = new List<List<Edge>>(inputs.Count);
        int widthFailed = 0, gapFailed = 0, centreFailed = 0, notFound = 0, lostRefused = 0;
        var places = new Dictionary<int, int>();
        List<LostRefusalSample>? refusals = null;
        foreach (var input in inputs)
        {
            var list = new List<Edge>();
            Evaluation? refused = null;
            FoundLine? refusedLine = null;
            var geometryValid = 0;
            var worst = NameGate.NotFound;
            var partials = PartialLines(input, settings);
            if (input.Marker.Status == PlateStatus.Confirmed)
                places[input.Marker.Id] = Places(input, partials, settings);
            foreach (var line in Expand(input, partials, settings))
            {
                if (line.Narrow || IsClaimed(line.Found ?? line.Box, claimed, settings.ClaimedOverlap))
                    continue;
                var evaluation = Evaluate(input, line, settings);
                if (evaluation.Verdict == Verdict.Invalid)
                    continue;
                geometryValid++;
                if (evaluation.Verdict == Verdict.NameFailed)
                {
                    if (evaluation.Gate > worst)
                        worst = evaluation.Gate;
                    if (input.Lost && (refused is null || evaluation.Cost < refused.Value.Cost))
                    {
                        refused = evaluation;
                        refusedLine = line;
                    }

                    continue;
                }

                var node = NodeOf(nodes, line.Found ?? line.Box, settings.ClaimedOverlap);
                var known = list.FindIndex(e => e.Node == node);
                if (known >= 0 && list[known].Cost <= evaluation.Cost)
                    continue;
                var edge = new Edge(node, evaluation.Line ?? line, evaluation);
                if (known >= 0)
                    list[known] = edge;
                else
                    list.Add(edge);
            }

            if (geometryValid > 0 && list.Count == 0)
            {
                if (input.Lost && input.Marker.NameLine is not null)
                {
                    lostRefused++;
                    if (refused is { } first && refusedLine is not null)
                        (refusals ??= new List<LostRefusalSample>()).Add(RefusalSample(input, refusedLine, first, frameMs));
                }

                switch (worst)
                {
                    case NameGate.Width:
                        widthFailed++;
                        break;
                    case NameGate.Gap:
                        gapFailed++;
                        break;
                    case NameGate.Centre:
                        centreFailed++;
                        break;
                    default:
                        notFound++;
                        break;
                }
            }

            edges.Add(list);
        }

        var active = new List<int>();
        for (var i = 0; i < edges.Count; i++)
            if (edges[i].Count > 0)
                active.Add(i);

        var assignedNode = new Dictionary<int, int>();
        var colourMismatches = 0;
        var lowFingerprints = 0;
        if (active.Count > 0)
        {
            var columns = nodes.Count + active.Count;
            var matrix = new double[active.Count, columns];
            for (var r = 0; r < active.Count; r++)
            {
                for (var c = 0; c < nodes.Count; c++)
                    matrix[r, c] = Forbidden;
                for (var c = nodes.Count; c < columns; c++)
                    matrix[r, c] = settings.MissCost;
                foreach (var edge in edges[active[r]])
                    matrix[r, edge.Node] = edge.Cost;
            }

            var solution = Hungarian.Solve(matrix);
            for (var r = 0; r < active.Count; r++)
            {
                var column = solution[r];
                if (column >= nodes.Count || matrix[r, column] >= Forbidden)
                    continue;
                var index = active[r];
                var edge = edges[index].First(e => e.Node == column);
                chosen[inputs[index].Marker.Id] = edge.Line;
                assignedNode[index] = column;
                if (edge.ColourMismatch)
                    colourMismatches++;
                if (edge.LowFingerprint)
                    lowFingerprints++;
            }
        }

        var overrides = 0;
        foreach (var index in active)
        {
            var nearest = edges[index].MinBy(e => e.Distance)!;
            if (assignedNode.TryGetValue(index, out var mine) && mine == nearest.Node)
                continue;
            foreach (var (other, node) in assignedNode)
                if (other != index && node == nearest.Node)
                {
                    overrides++;
                    break;
                }
        }

        return new Assignment(chosen, contested, overrides, new NameGateCounts(widthFailed, gapFailed, centreFailed, notFound), colourMismatches, lowFingerprints, places, lostRefused, refusals);
    }

    private static LostRefusalSample RefusalSample(MarkerCandidates input, FoundLine line, Evaluation evaluation, double frameMs)
    {
        var marker = input.Marker;
        return new LostRefusalSample(
            marker.Id, marker.Name ?? "?", frameMs - marker.LastFoundMs, evaluation.Reason, evaluation.Score, line.Box, evaluation.Distance, marker.Height, input.Patch);
    }

    private static int NodeOf(List<PixelRect> nodes, PixelRect box, double limit)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (BoxMath.Overlap(nodes[i], box) > limit)
                return i;
        nodes.Add(box);
        return nodes.Count - 1;
    }

    private static bool IsClaimed(PixelRect box, IReadOnlyList<PixelRect> claimed, double limit)
    {
        foreach (var other in claimed)
            if (BoxMath.Overlap(box, other) > limit)
                return true;
        return false;
    }

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
        foreach (var line in input.Lines)
        {
            if (line.IsUnion || line.Narrow || line.IsPartial || !PartialLine.IsShorter(line.Box.Width, stored, marker.Height, settings))
                continue;
            if (PartialLine.LeftEdgeFits(line.Box.X, input.PredictedX, marker.Height, settings))
                shorter.Add(PartialLine.Anchored(line, marker.Width));
        }

        return shorter;
    }

    private static double DistanceWidthOf(MarkerRecord marker) => PartialLine.StoredWidth(marker);

    public static int GateCandidates(MarkerCandidates input, PipelineSettings settings) => Places(input, PartialLines(input, settings), settings);

    private static int Places(MarkerCandidates input, List<FoundLine> partials, PipelineSettings settings)
    {
        var places = new List<PixelRect>();
        foreach (var line in input.Lines.Concat(partials))
        {
            if (line.Narrow || !GeometryFits(input.Marker, line, settings, out _))
                continue;
            var box = line.Part ?? line.Box;
            if (!places.Exists(p => BoxMath.Overlap(p, box) > settings.ClaimedOverlap))
                places.Add(box);
        }

        return places.Count;
    }

    private static bool GeometryFits(MarkerRecord marker, FoundLine line, PipelineSettings settings, out double widthChange)
    {
        widthChange = 0.0;
        var lh = marker.Height;
        if (Math.Abs(line.Box.Height - lh) > Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * lh))
            return false;

        var gate = settings.TrackWidthGateLines * lh;
        var distanceWidth = DistanceWidthOf(marker);
        widthChange = Math.Abs((line.Part?.Width ?? line.Box.Width) - distanceWidth);
        if (line.Part is not null && widthChange > gate && Math.Abs(distanceWidth - marker.Width) < 0.5)
            widthChange = Math.Abs(line.Box.Width - marker.Width);
        return widthChange <= gate;
    }

    private static Evaluation Evaluate(MarkerCandidates input, FoundLine line, PipelineSettings settings)
    {
        var marker = input.Marker;
        var lh = marker.Height;
        var invalid = new Evaluation(Verdict.Invalid, 0.0, 0.0, NameGate.NotFound, false, false);
        if (!GeometryFits(marker, line, settings, out var widthChange))
            return invalid;

        var dx = line.CentreX - (input.PredictedX + marker.Width / 2.0);
        var dy = line.Box.Y - input.PredictedY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > Math.Ceiling(input.Margin) + settings.MatchSlackPx)
            return invalid;

        var cost = Math.Min(1.0, distance / Math.Max(1.0, input.Margin)) + settings.WidthWeight * widthChange / lh;
        var colourMismatch = false;
        var lowFingerprint = false;
        FoundLine? chosen = null;
        if (marker.Status == PlateStatus.Confirmed && marker.NameLine is { } name)
        {
            var check = CheckName(input, line, name, settings, input.Lost);
            if (check.Failed is { } gate)
                return new Evaluation(Verdict.NameFailed, cost, distance, gate, false, false, null, check.Reason, check.Score);
            cost += check.Cost;
            colourMismatch = check.ColourMismatch;
            lowFingerprint = check.LowFingerprint;
            if (check.NameCentreX is { } nameCentre && (line.IsUnion || PartialLine.HasSpeedGroup(marker, settings)))
                chosen = line with { NameCentreX = nameCentre };
        }

        return new Evaluation(Verdict.Valid, cost, distance, NameGate.NotFound, colourMismatch, lowFingerprint, chosen);
    }

    private static NameCheck CheckName(MarkerCandidates input, FoundLine line, NameLineInfo name, PipelineSettings settings, bool lost)
    {
        var lh = input.Marker.Height;
        var gate = settings.TrackWidthGateLines * lh;
        var centre = line.CentreX;
        var centreTolerance = settings.NameCentreTolerancePx + line.CentreSlack;
        var reach = name.Width / 2.0 + gate + settings.NameReachSlackPx;
        var patch = input.Patch;
        if (line.Box.Y - name.Gap - settings.NameGapTolerancePx < patch.Y || centre - reach < patch.X || centre + reach > patch.Right)
            return lost ? new NameCheck(false, NameGate.NotFound, 0.0, false, false, null, LostRefusalReason.OutsidePatch) : new NameCheck(true, null, 0.0, false, false);

        var worst = NameGate.NotFound;
        var lowestMiss = double.NaN;
        var bestCost = double.PositiveInfinity;
        var bestColour = false;
        var bestLow = false;
        double? bestCentre = null;
        foreach (var other in input.Lines)
        {
            if (other.IsUnion || ReferenceEquals(other, line) || other.Ink is not { } ink || other.Box.Y >= line.Box.Y)
                continue;
            NameGate? failed = null;
            if (!NameSizeFits(other.Box.Width, other.Box.Height, name, gate, settings))
                failed = NameGate.Width;
            else if (Math.Abs(line.Box.Y - other.Box.Y - name.Gap) > settings.NameGapTolerancePx)
                failed = NameGate.Gap;
            else if (Math.Abs(other.CentreX - centre) > centreTolerance)
                failed = NameGate.Centre;
            if (failed is { } gateFailed)
            {
                if (gateFailed > worst)
                    worst = gateFailed;
                continue;
            }

            var mismatch = name.Colour is { } colour && other.Colour is { } seen && seen != colour;
            var correlation = Math.Clamp(Fingerprint.Correlation(ink, name.Fingerprint), 0.0, 1.0);
            if (Math.Abs(other.CentreX - centre) > settings.NameCentreTolerancePx && correlation < settings.NameFingerprintMin)
            {
                if (NameGate.Centre > worst)
                    worst = NameGate.Centre;
                continue;
            }

            if (lost && HasShape(name.Fingerprint) && correlation < settings.NameFingerprintMin)
            {
                if (double.IsNaN(lowestMiss) || correlation > lowestMiss)
                    lowestMiss = correlation;
                continue;
            }

            var cost = settings.ColourWeight * (mismatch ? 1.0 : 0.0) + settings.FingerprintWeight * (1.0 - correlation);
            if (cost >= bestCost)
                continue;
            bestCost = cost;
            bestColour = mismatch;
            bestLow = correlation < settings.NameFingerprintMin;
            bestCentre = other.CentreX;
        }

        if (double.IsPositiveInfinity(bestCost))
            return new NameCheck(false, worst, 0.0, false, false, null, FailureReason(worst, lowestMiss), lowestMiss);
        return new NameCheck(false, null, bestCost, bestColour, bestLow, bestCentre);
    }

    private static LostRefusalReason FailureReason(NameGate worst, double lowestMiss)
    {
        if (!double.IsNaN(lowestMiss))
            return LostRefusalReason.LowFingerprint;
        return worst switch
        {
            NameGate.Width => LostRefusalReason.Width,
            NameGate.Gap => LostRefusalReason.Gap,
            NameGate.Centre => LostRefusalReason.Centre,
            _ => LostRefusalReason.NoNameRow,
        };
    }

    private static bool HasShape(int[] fingerprint)
    {
        if (fingerprint.Length < 4)
            return false;
        for (var i = 1; i < fingerprint.Length; i++)
            if (fingerprint[i] != fingerprint[0])
                return true;
        return false;
    }

    internal static bool NameSizeFits(double width, double height, NameLineInfo name, double gate, PipelineSettings settings) =>
        Math.Abs(width - name.Width) <= gate && Math.Abs(height - name.Height) <= Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * name.Height);
}
