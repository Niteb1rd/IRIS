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

public static partial class TrackAssigner
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
        if (UnionOnWholeWidth(marker, line, settings))
            widthChange = Math.Abs(line.Box.Width - distanceWidth);
        return widthChange <= gate;
    }

    private static bool UnionOnWholeWidth(MarkerRecord marker, FoundLine line, PipelineSettings settings) =>
        line.Part is { } part && !PartialLine.HasSpeedGroup(marker, settings)
        && Math.Abs(part.Width - DistanceWidthOf(marker)) > settings.TrackWidthGateLines * marker.Height;

    public static bool StoresUnionPart(MarkerRecord marker, FoundLine line, PipelineSettings settings) =>
        line.Part is not null
        && (!UnionOnWholeWidth(marker, line, settings) || (marker.BornWithSpeedGroup && Math.Abs(DistanceWidthOf(marker) - marker.Width) < 0.5));

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
}
