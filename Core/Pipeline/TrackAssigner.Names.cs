
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public static partial class TrackAssigner
{
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
