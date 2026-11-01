using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public static class IndexModel
{
    public static double FreshnessLimitMs(IndexSnapshot snapshot, PipelineSettings settings)
    {
        var median = snapshot.MedianFrameIntervalMs > 0.0 ? snapshot.MedianFrameIntervalMs : snapshot.NominalFrameIntervalMs;
        var raw = Math.Max(settings.FreshnessMinMs, settings.FreshnessIntervalFactor * median) + Math.Max(0.0, snapshot.FramePublishedP95Ms);
        return Math.Min(raw, settings.FreshnessMaxMs);
    }

    public static bool IsFresh(MarkerRecord marker, double nowMs, double limitMs) => nowMs - marker.LastFoundMs <= limitMs;

    public static bool IsDrawable(MarkerRecord marker, double nowMs, PipelineSettings settings) =>
        marker.HasTrackingFix && marker.MissedFrames <= settings.RenderMissFrames && nowMs - marker.LastFoundMs <= settings.FreshnessMaxMs;

    public static double VelocityWindowMs(IndexSnapshot snapshot, PipelineSettings settings) =>
        Math.Max(settings.VelocityWindowMinMs, settings.VelocityWindowFrames * snapshot.NominalFrameIntervalMs);

    public static bool IsListed(MarkerRecord marker) =>
        marker.Status == PlateStatus.Confirmed && marker.Name is { } name && marker.PlateRel is not null
        && marker.Identity is { } identity && identity.Rsi != RsiState.NotFound && string.Equals(identity.Handle, name, StringComparison.OrdinalIgnoreCase);

    public static (double CentreX, double Top) PositionAt(MarkerRecord marker, IndexSnapshot snapshot, double nowMs, PipelineSettings pipe, LiveSettings live)
    {
        var interval = marker.TrackingIntervalMs;
        if (interval is not { } pair || pair > pipe.FreshnessMaxMs)
            return (marker.CentreX, marker.Top);
        var dt = Math.Min(Math.Max(0.0, nowMs - marker.LineMs), pipe.RenderLookAheadMaxMs);
        var (vx, vy) = marker.Speed(VelocityWindowMs(snapshot, pipe));
        var speed = Math.Sqrt(vx * vx + vy * vy);
        if (dt <= 0.0 || speed < live.MinSpeedPxPerMs)
            return (marker.CentreX, marker.Top);
        var distance = speed * dt;
        var limit = live.MaxShiftBoxHeights * marker.Height;
        var scale = distance > limit ? limit / distance : 1.0;
        return (marker.CentreX + vx * dt * scale, marker.Top + vy * dt * scale);
    }

    public static OverlayModel ModelAt(IndexSnapshot snapshot, double nowMs, PipelineSettings pipe, LiveSettings live, OverlayModel? previous = null)
    {
        var note = snapshot.BlankPictureNote ? BlankPictureWatch.NoteText : snapshot.LateFrameNote ? LateFrameWindow.NoteText : null;
        if (snapshot.Markers.Count == 0 && snapshot.RecentlyEnded.Count == 0)
            return NoteOnly(note, previous);

        var items = new List<BracketItem>();
        var listed = new List<MarkerRecord>();
        foreach (var marker in snapshot.Markers)
        {
            if (!IsListed(marker) || nowMs - marker.LastFoundMs > pipe.PanelStaleMs)
                continue;
            listed.Add(marker);
            if (!IsDrawable(marker, nowMs, pipe))
                continue;
            var rel = marker.PlateRel!.Value;
            var (cx, top) = PositionAt(marker, snapshot, nowMs, pipe, live);
            var box = new PixelRect(Math.Round(cx + rel.X), Math.Round(top + rel.Y), rel.Width, rel.Height);
            if (snapshot.Boundary is { } area && snapshot.WindowWidth > 0 && snapshot.WindowHeight > 0 && area.DistanceOutside(box, snapshot.WindowWidth, snapshot.WindowHeight) > 0.0)
                continue;
            var identity = marker.Identity!;
            items.Add(new BracketItem(box, identity.Kind, identity.Color, marker.Id));
        }

        foreach (var ended in snapshot.RecentlyEnded)
        {
            if (nowMs - ended.LastFoundMs <= pipe.PanelStaleMs)
                listed.Add(ended);
        }

        if (listed.Count == 0)
            return NoteOnly(note, previous);

        var panel = Panel(listed);
        if (previous is not null && previous.Panel.SequenceEqual(panel))
            panel = previous.Panel;
        if (previous is not null && ReferenceEquals(previous.Panel, panel) && previous.Note == note && previous.Brackets.SequenceEqual(items))
            return previous;
        return new OverlayModel(items, panel, null, note);
    }

    private static OverlayModel NoteOnly(string? note, OverlayModel? previous)
    {
        if (note is null)
            return OverlayModel.Empty;
        if (previous is not null && previous.Brackets.Count == 0 && previous.Panel.Count == 0 && previous.Note == note)
            return previous;
        return new OverlayModel(Array.Empty<BracketItem>(), Array.Empty<PanelEntry>(), null, note);
    }

    public static IReadOnlyList<PanelEntry> Panel(IReadOnlyList<MarkerRecord> listed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PanelEntry>();
        foreach (var marker in listed.OrderBy(m => m.Id))
        {
            var identity = marker.Identity!;
            if (seen.Add(identity.Handle))
                entries.Add(new PanelEntry(identity.Handle, identity.Kind, identity.Color, identity.OrgsLine));
        }

        return entries.OrderBy(e => (int)e.Kind).ToList();
    }
}
