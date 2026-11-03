using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public readonly record struct TrackCandidateNote(PixelRect Box, string Source, string Verdict, double Cost, double Distance, string? Why);

public sealed record MarkerCandidates(
    MarkerRecord Marker, double PredictedX, double PredictedY, double Margin, PixelRect Patch, IReadOnlyList<FoundLine> Lines, bool Lost = false, int WindowWidth = 0);

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
    IReadOnlyDictionary<int, int>? Places = null, int LostRefused = 0, IReadOnlyList<LostRefusalSample>? LostRefusals = null,
    IReadOnlyDictionary<int, NameEvidence>? Evidence = null)
{
    public int NameCheckFailed => NameFailures.Total;

    public NameEvidence EvidenceOf(int id) => Evidence is { } evidence && evidence.TryGetValue(id, out var value) ? value : NameEvidence.None;

    public int Count(NameEvidence evidence) => Evidence is null ? 0 : Evidence.Values.Count(e => e == evidence);
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
        LostRefusalReason Reason = LostRefusalReason.NoNameRow, double Score = double.NaN, string? Why = null, NameEvidence Evidence = NameEvidence.None);

    private readonly record struct NameCheck(
        bool Skipped, NameGate? Failed, double Cost, bool ColourMismatch, bool LowFingerprint, double? NameCentreX = null,
        LostRefusalReason Reason = LostRefusalReason.NoNameRow, double Score = double.NaN, NameEvidence Evidence = NameEvidence.None);

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
            Evidence = evaluation.Evidence;
        }

        public NameEvidence Evidence { get; }

        public bool ColourMismatch { get; }

        public bool LowFingerprint { get; }

        public int Node { get; }

        public FoundLine Line { get; }

        public double Cost { get; }

        public double Distance { get; }
    }

    public static Assignment Assign(
        IReadOnlyList<MarkerCandidates> inputs, IReadOnlyList<PixelRect> claimed, PipelineSettings settings, double frameMs = double.NaN, Action<int, TrackCandidateNote>? notes = null,
        IReadOnlyList<KnownName>? knownNames = null)
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
            var placeCount = 0;
            if (input.Marker.Status == PlateStatus.Confirmed)
                places[input.Marker.Id] = placeCount = Places(input, partials, settings);
            var context = new CheckContext(knownNames, placeCount, frameMs);
            foreach (var line in Expand(input, partials, settings))
            {
                if (line.Narrow)
                    continue;
                if (IsClaimed(line.Found ?? line.Box, claimed, settings.ClaimedOverlap))
                {
                    notes?.Invoke(input.Marker.Id, new TrackCandidateNote(line.Box, SourceOf(input, line), "claimed", double.NaN, double.NaN, null));
                    continue;
                }

                var evaluation = Evaluate(input, line, settings, context);
                notes?.Invoke(input.Marker.Id, new TrackCandidateNote(
                    line.Box, SourceOf(input, line), evaluation.Verdict switch { Verdict.Invalid => "invalid", Verdict.NameFailed => "name", _ => "valid" },
                    evaluation.Cost, evaluation.Distance, evaluation.Why));
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

            var wholeMatched = list.Exists(e => e.Evidence == NameEvidence.Whole);
            foreach (var (anchored, match) in wholeMatched ? Enumerable.Empty<(FoundLine, PartialNameMatch)>() : NameAnchoredLines(input, settings, knownNames))
            {
                if (IsClaimed(anchored.Box, claimed, settings.ClaimedOverlap))
                {
                    notes?.Invoke(input.Marker.Id, new TrackCandidateNote(anchored.Box, "name", "claimed", double.NaN, double.NaN, null));
                    continue;
                }

                var evaluation = EvaluateAnchored(input, anchored, match, settings);
                notes?.Invoke(input.Marker.Id, new TrackCandidateNote(
                    anchored.Box, "name", evaluation.Verdict == Verdict.Valid ? "valid" : "invalid", evaluation.Cost, evaluation.Distance, evaluation.Why));
                if (evaluation.Verdict != Verdict.Valid)
                    continue;
                var node = NodeOf(nodes, anchored.Box, settings.ClaimedOverlap);
                var known = list.FindIndex(e => e.Node == node);
                if (known >= 0 && list[known].Cost <= evaluation.Cost)
                    continue;
                var edge = new Edge(node, anchored, evaluation);
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
        var evidence = new Dictionary<int, NameEvidence>();
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
                evidence[inputs[index].Marker.Id] = edge.Evidence;
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

        return new Assignment(
            chosen, contested, overrides, new NameGateCounts(widthFailed, gapFailed, centreFailed, notFound), colourMismatches, lowFingerprints, places, lostRefused, refusals, evidence);
    }

    private static string SourceOf(MarkerCandidates input, FoundLine line)
    {
        if (line.IsPartial)
            return "partial";
        foreach (var original in input.Lines)
            if (ReferenceEquals(original, line))
                return line.IsUnion ? "union" : "line";
        return line.IsUnion ? "whole" : "split";
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

    private readonly record struct CheckContext(IReadOnlyList<KnownName>? KnownNames, int Places, double FrameMs);

    private static Evaluation Evaluate(MarkerCandidates input, FoundLine line, PipelineSettings settings, CheckContext context = default)
    {
        var marker = input.Marker;
        var lh = marker.Height;
        var dx = line.CentreX - (input.PredictedX + marker.Width / 2.0);
        var dy = line.Box.Y - input.PredictedY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (!GeometryFits(marker, line, settings, out var widthChange))
            return new Evaluation(Verdict.Invalid, double.NaN, distance, NameGate.NotFound, false, false, Why: "size");
        if (distance > Math.Ceiling(input.Margin) + settings.MatchSlackPx)
            return new Evaluation(Verdict.Invalid, double.NaN, distance, NameGate.NotFound, false, false, Why: "far");

        var cost = Math.Min(1.0, distance / Math.Max(1.0, input.Margin)) + settings.WidthWeight * widthChange / lh;
        var colourMismatch = false;
        var lowFingerprint = false;
        FoundLine? chosen = null;
        string? why = null;
        var evidence = NameEvidence.None;
        if (marker.Status == PlateStatus.Confirmed && marker.NameLine is { } name)
        {
            var check = CheckName(input, line, name, settings, input.Lost, context.KnownNames);
            why = check.Skipped ? "noname" : check.ColourMismatch ? "colour" : check.Evidence == NameEvidence.Partial ? "partial" : null;
            if (check.Failed is { } gate)
            {
                if (gate == NameGate.NotFound && FollowsWithoutName(input, line, widthChange, distance, context, settings))
                    return new Evaluation(Verdict.Valid, cost + settings.NameUnverifiedCost, distance, NameGate.NotFound, false, false, null,
                        Why: "unverified " + gate, Evidence: NameEvidence.Unverified);
                return new Evaluation(Verdict.NameFailed, cost, distance, gate, false, false, null, check.Reason, check.Score, gate + "/" + check.Reason);
            }

            cost += check.Cost;
            evidence = check.Evidence == NameEvidence.Whole && check.LowFingerprint ? NameEvidence.None : check.Evidence;
            colourMismatch = check.ColourMismatch;
            lowFingerprint = check.LowFingerprint;
            if (check.NameCentreX is { } nameCentre && (line.IsUnion || PartialLine.HasSpeedGroup(marker, settings)))
                chosen = line with { NameCentreX = nameCentre };
        }

        return new Evaluation(Verdict.Valid, cost, distance, NameGate.NotFound, colourMismatch, lowFingerprint, chosen,
            Why: why, Evidence: evidence);
    }

    private static bool FollowsWithoutName(MarkerCandidates input, FoundLine line, double widthChange, double distance, CheckContext context, PipelineSettings settings)
    {
        var marker = input.Marker;
        return !input.Lost && marker.Status == PlateStatus.Confirmed && context.Places == 1 && line.Found is null && line.Part is null && !line.IsUnion && !line.TallPart
            && input.Lines.Any(found => ReferenceEquals(found, line))
            && widthChange <= settings.NameUnverifiedWidthLines * marker.Height
            && marker.MissedFrames <= settings.NameUnverifiedMaxMissed && distance <= settings.NameUnverifiedReachLines * marker.Height
            && context.FrameMs - marker.NameSeenMs <= settings.NameUnverifiedMaxMs;
    }

    private static Evaluation EvaluateAnchored(MarkerCandidates input, FoundLine line, PartialNameMatch match, PipelineSettings settings)
    {
        var marker = input.Marker;
        var dx = line.CentreX - (input.PredictedX + marker.Width / 2.0);
        var dy = line.Box.Y - input.PredictedY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > Math.Ceiling(input.Margin) + settings.MatchSlackPx)
            return new Evaluation(Verdict.Invalid, double.NaN, distance, NameGate.NotFound, false, false, Why: "far");
        var cost = Math.Min(1.0, distance / Math.Max(1.0, input.Margin)) + settings.NameAnchorCost + settings.FingerprintWeight * (1.0 - Math.Clamp(match.Score, 0.0, 1.0));
        return new Evaluation(Verdict.Valid, cost, distance, NameGate.NotFound, false, false, null, Why: string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"from name {match.Score:0.00}/{match.Elsewhere:0.00} {match.Known} cols"), Evidence: NameEvidence.Anchored);
    }

    private static bool PartialNameUsable(MarkerRecord marker, NameLineInfo name, PipelineSettings settings) =>
        name.Fingerprint.Length >= 4 && name.Width >= settings.PartialNameMinWidthLines * marker.Height;

    private static bool IsNamePiece(FoundLine other, NameLineInfo name, double rowTop, PipelineSettings settings) =>
        !other.IsUnion && other.Ink is { Length: > 0 } && Math.Abs(other.Box.Y - rowTop) <= settings.NameGapTolerancePx
        && Math.Abs(other.Box.Height - name.Height) <= Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * name.Height);

    private static PartialNameMatch? PartialNameAbove(MarkerCandidates input, FoundLine line, NameLineInfo name, PipelineSettings settings, IReadOnlyList<KnownName>? knownNames)
    {
        var marker = input.Marker;
        if (!PartialNameUsable(marker, name, settings))
            return null;
        var rowTop = line.Box.Y - name.Gap;
        var rowLeft = line.CentreX - name.Fingerprint.Length / 2.0;
        var pieces = input.Lines.Where(other => !ReferenceEquals(other, line) && IsNamePiece(other, name, rowTop, settings));
        if (!PartialName.TryCompose(pieces, rowLeft, name.Fingerprint.Length, out var values, out var known, out var count))
            return null;
        var match = PartialName.Match(values, known, count, Math.Round(rowLeft), name, marker.Name, knownNames, 2, settings);
        return match is { } found && PartialName.Passes(found, settings) ? found : null;
    }

    private static List<(FoundLine Line, PartialNameMatch Match)> NameAnchoredLines(MarkerCandidates input, PipelineSettings settings, IReadOnlyList<KnownName>? knownNames)
    {
        var result = new List<(FoundLine Line, PartialNameMatch Match)>();
        var marker = input.Marker;
        if (marker.Status != PlateStatus.Confirmed || marker.NameLine is not { } name || !PartialNameUsable(marker, name, settings))
            return result;
        var stored = name.Fingerprint;
        var expectedTop = input.PredictedY - name.Gap;
        var reach = Math.Ceiling(input.Margin) + settings.MatchSlackPx;
        foreach (var piece in input.Lines)
        {
            if (piece.Narrow || !IsNamePiece(piece, name, piece.Box.Y, settings) || Math.Abs(piece.Box.Y - expectedTop) > reach
                || piece.Box.Width < settings.PartialNameMinFraction * stored.Length)
                continue;
            var located = PartialName.Locate(piece, stored, out var offset, out var runnerUp);
            if (located < settings.NameAnchorMin || located - runnerUp < settings.PartialNameMargin)
                continue;
            var rowLeft = Math.Round(piece.Box.X) - offset;
            var rowTop = piece.Box.Y;
            var pieces = input.Lines.Where(other => !other.Narrow && IsNamePiece(other, name, rowTop, settings));
            if (!PartialName.TryCompose(pieces, rowLeft, stored.Length, out var values, out var known, out var count))
                continue;
            if (PartialName.Match(values, known, count, rowLeft, name, marker.Name, knownNames, 2, settings) is not { } match || !PartialName.Passes(match, settings)
                || match.Score < settings.NameAnchorMin)
                continue;
            var box = new PixelRect(match.CentreX - marker.Width / 2.0, rowTop + name.Gap, marker.Width, marker.Height);
            if (result.Exists(r => Math.Abs(r.Line.CentreX - (box.X + box.Width / 2.0)) <= 2.0 && Math.Abs(r.Line.Box.Y - box.Y) <= 2.0))
                continue;
            if (DistanceLineVisible(input, box, settings))
                continue;
            result.Add((new FoundLine(box), match));
        }

        return result;
    }

    private static bool DistanceLineVisible(MarkerCandidates input, PixelRect box, PipelineSettings settings)
    {
        var lh = input.Marker.Height;
        var heightTolerance = Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * lh);
        foreach (var line in input.Lines)
        {
            if (line.Narrow || Math.Abs(line.Box.Height - lh) > heightTolerance)
                continue;
            var target = line.Part ?? line.Box;
            var overlapX = Math.Min(target.Right, box.Right) - Math.Max(target.X, box.X);
            if (Math.Abs(target.Y + target.Height / 2.0 - (box.Y + box.Height / 2.0)) <= 0.75 * lh
                && overlapX >= settings.ClaimedOverlap * Math.Min(target.Width, box.Width))
                return true;
        }

        return false;
    }

    private static NameCheck CheckName(MarkerCandidates input, FoundLine line, NameLineInfo name, PipelineSettings settings, bool lost, IReadOnlyList<KnownName>? knownNames = null)
    {
        var lh = input.Marker.Height;
        var gate = settings.TrackWidthGateLines * lh;
        var centre = line.CentreX;
        var centreTolerance = settings.NameCentreTolerancePx + line.CentreSlack;
        var reach = name.Width / 2.0 + gate + settings.NameReachSlackPx;
        var patch = input.Patch;
        var above = line.Box.Y - name.Gap - settings.NameGapTolerancePx < patch.Y;
        var left = centre - reach < patch.X;
        var right = centre + reach > patch.Right;
        if (above || left || right)
        {
            var cutByWindow = (!above || patch.Y <= 0) && (!left || patch.X <= 0) && (!right || (input.WindowWidth > 0 && patch.Right >= input.WindowWidth));
            return !lost && cutByWindow
                ? new NameCheck(true, null, 0.0, false, false)
                : new NameCheck(false, NameGate.NotFound, 0.0, false, false, null, LostRefusalReason.OutsidePatch);
        }

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
        {
            if (PartialNameAbove(input, line, name, settings, knownNames) is { } partial)
                return new NameCheck(false, null, settings.FingerprintWeight * (1.0 - Math.Clamp(partial.Score, 0.0, 1.0)) + settings.PartialNameCost, false, false,
                    partial.CentreX, Evidence: NameEvidence.Partial);
            return new NameCheck(false, worst, 0.0, false, false, null, FailureReason(worst, lowestMiss), lowestMiss);
        }

        return new NameCheck(false, null, bestCost, bestColour, bestLow, bestCentre, Evidence: NameEvidence.Whole);
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

    internal static bool NamePieceFits(double width, double height, NameLineInfo name, double lineHeight, PipelineSettings settings) =>
        name.Width >= settings.PartialNameMinWidthLines * lineHeight && width >= 4
        && Math.Abs(height - name.Height) <= Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * name.Height);

    internal static bool NameSizeFits(double width, double height, NameLineInfo name, double gate, PipelineSettings settings) =>
        Math.Abs(width - name.Width) <= gate && Math.Abs(height - name.Height) <= Math.Max(settings.LineHeightMinTolerancePx, settings.LineHeightTolerance * name.Height);
}
