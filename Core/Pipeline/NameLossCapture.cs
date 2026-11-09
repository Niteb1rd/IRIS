using System.Globalization;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class NameLossCapture : IDisposable
{
    private sealed record Shot(Mat Image, Rect Window, long Sequence, double FrameMs);

    private sealed class Track
    {
        public required string Name { get; init; }

        public Shot? Good;
        public MarkerRecord? GoodRecord;
        public readonly List<Shot> After = new();
        public bool Lost;

        public void DropAfter()
        {
            foreach (var shot in After)
                shot.Image.Dispose();
            After.Clear();
            Lost = false;
        }

        public void DropAll()
        {
            DropAfter();
            Good?.Image.Dispose();
            Good = null;
            GoodRecord = null;
        }
    }

    private readonly PipelineSettings _settings;
    private readonly Dictionary<int, Track> _tracks = new();
    private readonly Dictionary<(string Name, bool Unsure), double> _lastSent = new();
    private double _lostAfterMs;

    public NameLossCapture(PipelineSettings settings)
    {
        _settings = settings;
        _lostAfterMs = settings.LostAfterMs;
    }

    public Action<LossGroup>? Emit { get; set; }

    public void Clear()
    {
        foreach (var track in _tracks.Values)
            track.DropAll();
        _tracks.Clear();
    }

    public void Dispose() => Clear();

    public void Hit(
        IndexSnapshot snapshot, MarkerRecord after, FrameRegion region, CapturedFrame frame, double predictedCentreX, double predictedTop, NameEvidence evidence,
        int windowWidth, int windowHeight)
    {
        if (Emit is null || after.Name is not { } name)
            return;
        var track = TrackOf(after.Id, name);
        var full = track.Good is not null && track.After.Count >= _settings.LossAfterFrames;
        if (full && (track.Lost || frame.FrameMs - track.Good!.FrameMs > _lostAfterMs))
            Finish(track, after.Id, frame.FrameMs, Reacquired(track, after, frame.FrameMs, predictedCentreX, predictedTop, evidence), after, predictedCentreX, predictedTop);
        else if (track.After.Count > 0)
            track.DropAfter();

        track.Good?.Image.Dispose();
        track.Good = null;
        track.GoodRecord = null;
        if (!Qualifies(snapshot, after, windowWidth, windowHeight))
            return;
        if (LossGroup.Cut(region.Image, region.Region, CropBox(after.PlateBox, after.Height)) is { } cut)
        {
            track.Good = new Shot(cut.Image, cut.Window, frame.Sequence, frame.FrameMs);
            track.GoodRecord = after;
        }
    }

    public void Miss(IndexSnapshot snapshot, MarkerRecord marker, FrameRegion region, CapturedFrame frame, double predictedCentreX, double predictedTop)
    {
        if (Emit is null || marker.Name is not { } name || !_tracks.TryGetValue(marker.Id, out var track) || track.Name != name)
            return;
        if (track.Good is null || track.GoodRecord is not { } good || track.After.Count >= _settings.LossAfterFrames)
            return;
        if (snapshot.LateFrameNote || snapshot.BlankPictureNote)
        {
            track.DropAll();
            return;
        }

        var plate = good.PlateBox;
        var moved = new PixelRect(plate.X + predictedCentreX - good.CentreX, plate.Y + predictedTop - good.Top, plate.Width, plate.Height);
        if (LossGroup.Cut(region.Image, region.Region, CropBox(moved, good.Height)) is not { } cut)
        {
            track.DropAll();
            return;
        }

        track.After.Add(new Shot(cut.Image, cut.Window, frame.Sequence, frame.FrameMs));
    }

    public void EndTick(IndexSnapshot snapshot, double frameMs, IReadOnlySet<int> ended, PipelineSettings tick)
    {
        _lostAfterMs = tick.LostAfterMs;
        if (Emit is null || _tracks.Count == 0)
            return;
        foreach (var (id, track) in _tracks.ToList())
        {
            var gone = ended.Contains(id) || snapshot.Find(id) is not { Status: PlateStatus.Confirmed };
            var full = track.Good is not null && track.After.Count >= _settings.LossAfterFrames;
            if (full && !track.Lost && frameMs - track.Good!.FrameMs > _lostAfterMs)
                track.Lost = true;
            if (full && track.Lost && (gone || frameMs - track.Good!.FrameMs > _settings.LossReacquireMs))
                Finish(track, id, frameMs, null, null, double.NaN, double.NaN);
            if (gone)
            {
                track.DropAll();
                _tracks.Remove(id);
            }
        }
    }

    public bool Qualifies(IndexSnapshot snapshot, MarkerRecord marker, int windowWidth, int windowHeight)
    {
        if (marker.Status != PlateStatus.Confirmed || marker.PlateRel is null || marker.NameLine is null || windowWidth <= 0 || windowHeight <= 0)
            return false;
        if (snapshot.LateFrameNote || snapshot.BlankPictureNote)
            return false;
        var lh = marker.Height;
        if (lh < _settings.LossMinLineHeightPx * windowHeight / 1440.0)
            return false;
        var plate = marker.PlateBox;
        var cx = plate.X + plate.Width / 2.0;
        var cy = plate.Y + plate.Height / 2.0;
        if (Math.Abs(cx - windowWidth / 2.0) > _settings.LossCentreShare * windowWidth || Math.Abs(cy - windowHeight / 2.0) > _settings.LossCentreShare * windowHeight)
            return false;
        if (SpeedLinesPerMs(marker) > _settings.LossMaxSpeedLinesPerMs)
            return false;
        var reach = _settings.LossNeighbourLines * lh;
        foreach (var other in snapshot.Markers)
        {
            if (other.Id == marker.Id)
                continue;
            var box = other.PlateBox;
            var gapX = Math.Max(0.0, Math.Max(box.X - plate.Right, plate.X - box.Right));
            var gapY = Math.Max(0.0, Math.Max(box.Y - plate.Bottom, plate.Y - box.Bottom));
            if (gapX <= reach && gapY <= reach)
                return false;
        }

        return true;
    }

    private double SpeedLinesPerMs(MarkerRecord marker)
    {
        if (marker.Height <= 0)
            return double.PositiveInfinity;
        var (vx, vy) = marker.TrackingVelocity(0.0, marker.MaxSpeedPxPerMs(_settings));
        return Math.Sqrt(vx * vx + vy * vy) / marker.Height;
    }

    private PixelRect CropBox(PixelRect plate, double lineHeight)
    {
        var m = _settings.LossCropMarginLines * lineHeight;
        return new PixelRect(plate.X - m, plate.Y - m, plate.Width + 2 * m, plate.Height + 2 * m);
    }

    private bool Reacquired(Track track, MarkerRecord hit, double frameMs, double predictedCentreX, double predictedTop, NameEvidence evidence)
    {
        if (evidence is not (NameEvidence.Whole or NameEvidence.Partial or NameEvidence.Anchored))
            return false;
        if (frameMs - track.Good!.FrameMs > _settings.LossReacquireMs)
            return false;
        var dx = hit.CentreX - predictedCentreX;
        var dy = hit.Top - predictedTop;
        return Math.Sqrt(dx * dx + dy * dy) <= _settings.LossReacquireReachLines * hit.Height;
    }

    private Track TrackOf(int id, string name)
    {
        if (_tracks.TryGetValue(id, out var track) && track.Name == name)
            return track;
        track?.DropAll();
        track = new Track { Name = name };
        _tracks[id] = track;
        return track;
    }

    private void Finish(Track track, int id, double frameMs, bool? reacquired, MarkerRecord? hit, double predictedCentreX, double predictedTop)
    {
        var good = track.Good!;
        var record = track.GoodRecord!;
        var unsure = reacquired != true;
        var key = (track.Name.ToUpperInvariant(), unsure);
        var shots = new List<LossShot>();
        if (_lastSent.TryGetValue(key, out var lastMs) && good.FrameMs - lastMs < _settings.LossCooldownMs)
        {
            track.DropAll();
            return;
        }

        _lastSent[key] = good.FrameMs;
        shots.Add(new LossShot("before", good.Image, good.Window, good.Sequence, good.FrameMs));
        for (var i = 0; i < track.After.Count; i++)
        {
            var a = track.After[i];
            shots.Add(new LossShot("after" + (i + 1).ToString(CultureInfo.InvariantCulture), a.Image, a.Window, a.Sequence, a.FrameMs));
        }

        var reason = reacquired switch
        {
            true => "reacquired",
            false when frameMs - good.FrameMs > _settings.LossReacquireMs => "refound too late",
            false => "refound off the prediction",
            null => "not refound",
        };
        var line = TraceLine.Begin(good.FrameMs, "trk", "loss").Str("kind", "name").Str("label", track.Name).Bool("unsure", unsure).Str("reason", reason)
            .Int("mid", id).Num("lh", record.Height, 1).Box("plate", record.PlateBox).Num("speedLhPerMs", SpeedLinesPerMs(record), 4)
            .Num("decidedMs", frameMs);
        if (hit is not null)
        {
            var dx = hit.CentreX - predictedCentreX;
            var dy = hit.Top - predictedTop;
            line.Num("refoundAfterMs", frameMs - good.FrameMs, 1).Num("refoundOffLh", hit.Height > 0 ? Math.Sqrt(dx * dx + dy * dy) / hit.Height : double.NaN, 2);
        }

        line.Array("shots");
        foreach (var shot in shots)
            line.Item().Str("role", shot.Role).Int("seq", shot.Sequence).Num("fms", shot.FrameMs)
                .Box("win", new PixelRect(shot.Window.X, shot.Window.Y, shot.Window.Width, shot.Window.Height));
        var sidecar = line.EndArray().Text();

        track.Good = null;
        track.GoodRecord = null;
        track.After.Clear();
        track.Lost = false;
        Emit!(new LossGroup(LossKind.Name, unsure, track.Name, id, shots, sidecar));
    }
}
