using System.Diagnostics;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class TrackingWorker
{
    private readonly PipelineSettings _settings;
    private readonly ILineTracker _tracker;
    private readonly PipelineStats? _stats;
    private readonly IUpdateSink _sink;
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<int, bool> _matched = new();
    private readonly Dictionary<int, int> _lostTicks = new();
    private readonly HashSet<int> _crowded = new();
    private readonly List<double> _durations = new();
    private readonly Dictionary<int, (double LastMs, double SampleMs)> _crops = new();
    private int _epoch = int.MinValue;
    private double _lastFrameMs = double.NegativeInfinity;

    public ITraceSink? Trace { get; set; }

    public Action<OpenCvSharp.Mat, string>? KeepCrop { get; set; }

    public TrackingWorker(PipelineSettings settings, ILineTracker tracker, IUpdateSink sink, IMonotonicClock clock, PipelineStats? stats = null)
    {
        _settings = settings;
        _tracker = tracker;
        _sink = sink;
        _clock = clock;
        _stats = stats;
    }

    public bool Process(IndexSnapshot snapshot, CapturedFrame frame, PipelineEnvironment env)
    {
        if (snapshot.Epoch != _epoch)
        {
            _epoch = snapshot.Epoch;
            _matched.Clear();
            _lostTicks.Clear();
            _crowded.Clear();
            _lastFrameMs = double.NegativeInfinity;
            _durations.Clear();
            _crops.Clear();
        }

        if (!(frame.FrameMs > _lastFrameMs))
            return false;
        _lastFrameMs = frame.FrameMs;

        var started = Stopwatch.GetTimestamp();
        var workStartMs = _clock.NowMs;
        var frameMs = frame.FrameMs;
        var width = env.WindowWidth;
        var height = env.WindowHeight;
        var interval = snapshot.NominalFrameIntervalMs;
        var settings = _settings.Adapt(interval);
        var epoch = snapshot.Epoch;

        var tracked = snapshot.Markers.Where(m => m.Status != PlateStatus.Stopped).ToList();
        var regions = new Dictionary<int, FrameRegion>();
        foreach (var region in frame.Regions)
        {
            if (region.MarkerId <= 0 || !IsTrackingRegion(region))
                continue;
            if (!regions.ContainsKey(region.MarkerId) || region.Kind == RequestKind.TrackingRegion)
                regions[region.MarkerId] = region;
        }

        var claimed = new List<PixelRect>();
        foreach (var marker in tracked)
            if (!regions.ContainsKey(marker.Id) && _matched.GetValueOrDefault(marker.Id, true))
                claimed.Add(marker.Line);

        var updates = new List<IndexUpdate>();
        var current = new Dictionary<int, MarkerRecord>();
        var inputs = new List<MarkerCandidates>();
        var handled = new Dictionary<int, (PatchPlan Plan, FrameRegion Region)>();
        foreach (var marker in tracked.OrderBy(m => m.Id))
        {
            current[marker.Id] = marker;
            if (!regions.TryGetValue(marker.Id, out var region) || region.Image.Empty())
                continue;
            if (SkipsLostSearch(marker, frameMs, settings))
                continue;
            var (px, py, margin, _) = marker.Predict(frameMs, settings);
            var plan = new PatchPlan(marker.Id, region.Region, px, py, margin);
            handled[marker.Id] = (plan, region);
            inputs.Add(new MarkerCandidates(marker, px, py, margin, RegionPlanner.ToPixelRect(region.Region), _tracker.Find(marker, plan, region.Image), marker.IsLost(frameMs, settings), width));
        }

        var tracing = Trace is { Enabled: true };
        Dictionary<int, List<TrackCandidateNote>>? notes = tracing ? new() : null;
        var crowdedBefore = tracing ? new HashSet<int>(_crowded) : null;
        var assignment = TrackAssigner.Assign(inputs, claimed, settings, frameMs, notes is null ? null : (id, note) =>
        {
            if (!notes.TryGetValue(id, out var list))
                notes[id] = list = new List<TrackCandidateNote>();
            list.Add(note);
        }, inputs.Count > 0 ? KnownNames(snapshot, settings.KnownNamesMax) : null);
        foreach (var input in inputs)
        {
            if (input.Marker.Status != PlateStatus.Confirmed)
                continue;
            var places = assignment.Places is { } counted && counted.TryGetValue(input.Marker.Id, out var count) ? count : TrackAssigner.GateCandidates(input, settings);
            if (places >= 2)
                _crowded.Add(input.Marker.Id);
            else
                _crowded.Remove(input.Marker.Id);
        }

        var missed = new List<int>();
        var attempts = 0;
        var found = 0;
        var confirmedAttempts = 0;
        var confirmedFound = 0;
        foreach (var input in inputs)
        {
            var marker = input.Marker;
            var confirmed = marker.Status == PlateStatus.Confirmed;
            attempts++;
            if (confirmed)
                confirmedAttempts++;
            if (!assignment.Chosen.TryGetValue(marker.Id, out var line))
            {
                _matched[marker.Id] = false;
                missed.Add(marker.Id);
                if (tracing)
                    TraceMarker(frame, input, handled[marker.Id].Region.Kind, crowdedBefore!, notes!, null, null, NameEvidence.None);
                if (confirmed)
                    Keep(handled[marker.Id].Region, marker, input.Lost ? "lost" : "miss", frameMs, width, height);
                continue;
            }

            var (plan, region) = handled[marker.Id];
            var evidence = assignment.EvidenceOf(marker.Id);
            var position = evidence == NameEvidence.Anchored
                ? new LinePosition(line.CentreX, line.Box.Y, line.Box.Width)
                : line.Found is { } shorter ? RefinePartial(plan, region.Image, line, shorter) : _tracker.Refine(plan, region.Image, line.Box);
            if (line.NameCentreX is { } nameCentre)
                position = position with { CentreX = nameCentre };
            found++;
            if (confirmed)
                confirmedFound++;
            var distanceWidth = evidence == NameEvidence.Anchored ? PartialLine.StoredWidth(marker)
                : line.Part is { } part && TrackAssigner.StoresUnionPart(marker, line, _settings) ? part.Width : line.Box.Width;
            current[marker.Id] = marker.AfterHit(frameMs, position.CentreX, position.Top, position.Width, distanceWidth);
            if (tracing)
                TraceMarker(frame, input, region.Kind, crowdedBefore!, notes!, line, position, evidence);
            var predictedCentre = input.PredictedX + marker.Width / 2.0;
            var error = Math.Sqrt((position.CentreX - predictedCentre) * (position.CentreX - predictedCentre) + (position.Top - input.PredictedY) * (position.Top - input.PredictedY));
            var nameSeen = evidence is NameEvidence.Whole or NameEvidence.Partial or NameEvidence.Anchored;
            updates.Add(new TrackingHitUpdate(PipelineGroup.Tracking, frameMs, epoch, marker.Id, position.CentreX, position.Top, position.Width, distanceWidth, error, nameSeen));
            _matched[marker.Id] = true;
            if (confirmed)
                Keep(region, marker, evidence switch { NameEvidence.Partial => "partial", NameEvidence.Anchored => "fromname", NameEvidence.Unverified => "alone", _ => "sample" },
                    frameMs, width, height);
        }

        _stats?.AddAssignment(assignment.Contested, assignment.Overrides, assignment.NameFailures, assignment.ColourMismatches, assignment.LowFingerprints, assignment.LostRefused);
        _stats?.AddNameEvidence(assignment.Count(NameEvidence.Partial), assignment.Count(NameEvidence.Anchored), assignment.Count(NameEvidence.Unverified));
        _stats?.AddLostRefusals(assignment.LostRefusals);

        var ended = new HashSet<int>();
        if (env.Boundary is { } area)
        {
            var limit = TrackingBoundary.DropDistancePx(height);
            foreach (var (id, record) in current)
            {
                if (area.DistanceOutside(record.PlateBox, width, height) <= limit)
                    continue;
                ended.Add(id);
                updates.Add(new EndUpdate(PipelineGroup.Tracking, frameMs, epoch, id, EndReason.Boundary));
                _stats?.AddBoundaryEnd();
            }
        }

        var nowMs = _clock.NowMs;
        var plans = RegionPlanner.PlanTick(current.Where(kv => !ended.Contains(kv.Key)).Select(kv => kv.Value), WantsNameRow, frameMs, interval, width, height, settings,
            KeepsNameRowWhenLost);
        foreach (var (id, _) in current.OrderBy(kv => kv.Key))
        {
            if (ended.Contains(id))
            {
                _matched.Remove(id);
                continue;
            }

            if (plans.TryGetValue(id, out var planned))
            {
                var spec = new RequestSpec(
                    PipelineGroup.Tracking, RequestKind.TrackingRegion, RegionPlanner.ToPixelRect(planned.Region), 0, frameMs, nowMs, frameMs + planned.ExpiryMs, false);
                updates.Add(new RequestUpdate(PipelineGroup.Tracking, frameMs, epoch, id, spec));
            }
        }

        foreach (var id in _matched.Keys.Where(id => snapshot.Find(id) is null).ToList())
            _matched.Remove(id);
        foreach (var id in _lostTicks.Keys.Where(id => snapshot.Find(id) is null).ToList())
            _lostTicks.Remove(id);
        foreach (var id in _crops.Keys.Where(id => snapshot.Find(id) is null || ended.Contains(id)).ToList())
            _crops.Remove(id);
        _crowded.RemoveWhere(id => snapshot.Find(id) is null || ended.Contains(id));

        var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _durations.Add(elapsedMs);
        while (_durations.Count > settings.TrackingTimesKept)
            _durations.RemoveAt(0);
        updates.Add(new FrameTrackedUpdate(PipelineGroup.Tracking, frameMs, epoch, frame.ArrivalMs, LiveStats.Percentile95(_durations), frame.PublishedMs, missed));
        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);

        if (tracing && Trace.Line(nowMs, "trk", "tick") is { } tick)
        {
            tick.Int("seq", frame.Sequence).Num("fms", frameMs).Int("tracked", tracked.Count).Int("patches", inputs.Count).Int("claimed", claimed.Count)
                .Bool("contested", assignment.Contested).Int("found", found).Num("ms", elapsedMs, 3).Num("published", frame.PublishedMs).Array("noPatch");
            foreach (var marker in tracked)
                if (!handled.ContainsKey(marker.Id))
                    tick.Item().Int("mid", marker.Id).Bool("matchedLast", _matched.GetValueOrDefault(marker.Id, true));
            tick.EndArray().Array("ended");
            foreach (var id in ended)
                tick.Item().Int("mid", id);
            Trace!.Write(tick.EndArray().Text());
        }

        _stats?.AddMarkers(current.Count - ended.Count);
        _stats?.AddTrackedFrame();
        if (attempts > 0)
            _stats?.AddTrackTick(elapsedMs, attempts, found, confirmedAttempts, confirmedFound);
        return true;
    }

    private static List<KnownName> KnownNames(IndexSnapshot snapshot, int max)
    {
        var result = new List<KnownName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in snapshot.Markers)
        {
            if (result.Count >= max)
                return result;
            if (marker.Name is { } name && marker.NameLine is { Fingerprint.Length: >= 4 } line && seen.Add(name))
                result.Add(new KnownName(name, line.Fingerprint));
        }

        for (var i = snapshot.SessionMemory.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var entry = snapshot.SessionMemory[i];
            if (entry.Fingerprint is { Length: >= 4 } fingerprint && seen.Add(entry.Name))
                result.Add(new KnownName(entry.Name, fingerprint));
        }

        return result;
    }

    private void Keep(FrameRegion region, MarkerRecord marker, string reason, double frameMs, int windowWidth, int windowHeight)
    {
        if (KeepCrop is not { } keep || region.Image.Empty())
            return;
        var (lastMs, sampleMs) = _crops.TryGetValue(marker.Id, out var times) ? times : (double.NegativeInfinity, double.NegativeInfinity);
        if (frameMs - lastMs < _settings.TrackCropEveryMs || (reason == "sample" && frameMs - sampleMs < _settings.TrackCropSampleEveryMs))
            return;
        _crops[marker.Id] = (frameMs, reason == "sample" ? frameMs : sampleMs);
        var r = region.Region;
        var name = new string((marker.Name ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        keep(region.Image, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{marker.Id}_{name}_{reason}_x{r.X}y{r.Y}w{r.Width}h{r.Height}_win{windowWidth}x{windowHeight}"));
    }

    private bool SkipsLostSearch(MarkerRecord marker, double frameMs, PipelineSettings settings)
    {
        if (settings.LostThrottleEveryFrames <= 1 || !marker.IsLost(frameMs, settings) || frameMs - marker.LastFoundMs <= settings.LostThrottleAfterMs)
        {
            _lostTicks.Remove(marker.Id);
            return false;
        }

        var ticks = _lostTicks.GetValueOrDefault(marker.Id);
        _lostTicks[marker.Id] = ticks + 1;
        if (ticks % settings.LostThrottleEveryFrames == 0)
            return false;
        _stats?.AddLostSearchSkipped();
        return true;
    }

    private void TraceMarker(
        CapturedFrame frame, MarkerCandidates input, RequestKind regionKind, HashSet<int> crowdedBefore, Dictionary<int, List<TrackCandidateNote>> notes,
        FoundLine? line, LinePosition? position, NameEvidence evidence)
    {
        if (Trace.Line(_clock.NowMs, "trk", "mk") is not { } t)
            return;
        var m = input.Marker;
        var (vx, vy) = m.TrackingVelocity(0.0, m.MaxSpeedPxPerMs(_settings));
        t.Int("seq", frame.Sequence).Num("fms", frame.FrameMs).Int("mid", m.Id).Str("st", m.Status.ToString()).Str("name", m.Name)
            .Bool("lost", input.Lost).Bool("crowded", crowdedBefore.Contains(m.Id)).Str("region", regionKind.ToString())
            .Box("line", m.Line).Num("lineMs", m.LineMs).Num("lastFound", m.LastFoundMs)
            .Num("px", input.PredictedX).Num("py", input.PredictedY).Num("margin", input.Margin).Num("vx", vx, 4).Num("vy", vy, 4)
            .Box("patch", input.Patch).Int("lines", input.Lines.Count).Num("err", m.PredictionErrorPx, 1).Int("missed", m.MissedFrames)
            .Str("nameEv", evidence.ToString()).Num("nameSeen", m.NameSeenMs);
        t.Array("cands");
        if (notes.TryGetValue(m.Id, out var list))
        {
            foreach (var note in list.OrderBy(n => n.Verdict == "valid" ? 0 : n.Verdict == "name" ? 1 : 2).ThenBy(n => double.IsNaN(n.Cost) ? n.Distance : n.Cost).Take(8))
            {
                t.Item().Box("b", note.Box).Str("src", note.Source).Str("v", note.Verdict).Num("cost", note.Cost, 3).Num("d", note.Distance, 1);
                if (note.Why is not null)
                    t.Str("why", note.Why);
            }
        }

        t.EndArray();
        if (line is not null && position is { } p)
            t.Box("hit", line.Box).Bool("partial", line.IsPartial).Bool("union", line.IsUnion).Num("hitCx", p.CentreX).Num("hitTop", p.Top).Num("hitW", p.Width)
                .Num("jump", Math.Sqrt((p.CentreX - m.CentreX) * (p.CentreX - m.CentreX) + (p.Top - m.Top) * (p.Top - m.Top)), 1);
        else
            t.Bool("miss", true);
        Trace!.Write(t.Text());
    }

    private LinePosition RefinePartial(PatchPlan plan, OpenCvSharp.Mat patch, FoundLine line, PixelRect found)
    {
        var fix = _tracker.Refine(plan, patch, found);
        var offset = line.Box.X + line.Box.Width / 2.0 - (found.X + found.Width / 2.0);
        return new LinePosition(fix.CentreX + offset, fix.Top, line.Box.Width);
    }

    private static bool IsTrackingRegion(FrameRegion region) => region.Kind == RequestKind.TrackingRegion || region.Kind == RequestKind.FirstRegion;

    private static bool KeepsNameRowWhenLost(MarkerRecord record) => record.Status == PlateStatus.Confirmed;

    private static bool WantsNameRow(MarkerRecord record) => record.Status == PlateStatus.Confirmed;
}
