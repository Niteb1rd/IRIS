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
    private readonly HashSet<int> _crowded = new();
    private readonly List<double> _durations = new();
    private int _epoch = int.MinValue;
    private double _lastFrameMs = double.NegativeInfinity;

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
            _crowded.Clear();
            _lastFrameMs = double.NegativeInfinity;
            _durations.Clear();
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
            var (px, py, margin, _) = marker.Predict(frameMs, _settings);
            var plan = new PatchPlan(marker.Id, region.Region, px, py, margin);
            handled[marker.Id] = (plan, region);
            inputs.Add(new MarkerCandidates(marker, px, py, margin, RegionPlanner.ToPixelRect(region.Region), _tracker.Find(marker, plan, region.Image), marker.IsLost(frameMs, _settings)));
        }

        var assignment = TrackAssigner.Assign(inputs, claimed, _settings, frameMs);
        foreach (var input in inputs)
        {
            if (input.Marker.Status != PlateStatus.Confirmed)
                continue;
            var places = assignment.Places is { } counted && counted.TryGetValue(input.Marker.Id, out var count) ? count : TrackAssigner.GateCandidates(input, _settings);
            if (places >= 2)
                _crowded.Add(input.Marker.Id);
            else
                _crowded.Remove(input.Marker.Id);
        }

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
                continue;
            }

            var (plan, region) = handled[marker.Id];
            var position = line.Found is { } shorter ? RefinePartial(plan, region.Image, line, shorter) : _tracker.Refine(plan, region.Image, line.Box);
            if (line.NameCentreX is { } nameCentre)
                position = position with { CentreX = nameCentre };
            found++;
            if (confirmed)
                confirmedFound++;
            var distanceWidth = line.Part?.Width ?? line.Box.Width;
            current[marker.Id] = marker.AfterHit(frameMs, position.CentreX, position.Top, position.Width, distanceWidth);
            updates.Add(new TrackingHitUpdate(PipelineGroup.Tracking, frameMs, epoch, marker.Id, position.CentreX, position.Top, position.Width, distanceWidth));
            _matched[marker.Id] = true;
        }

        _stats?.AddAssignment(assignment.Contested, assignment.Overrides, assignment.NameFailures, assignment.ColourMismatches, assignment.LowFingerprints, assignment.LostRefused);
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
        var plans = RegionPlanner.PlanTick(current.Where(kv => !ended.Contains(kv.Key)).Select(kv => kv.Value), WantsNameRow, frameMs, interval, width, height, _settings,
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
        _crowded.RemoveWhere(id => snapshot.Find(id) is null || ended.Contains(id));

        var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _durations.Add(elapsedMs);
        while (_durations.Count > _settings.TrackingTimesKept)
            _durations.RemoveAt(0);
        updates.Add(new FrameTrackedUpdate(PipelineGroup.Tracking, frameMs, epoch, frame.ArrivalMs, LiveStats.Percentile95(_durations), frame.PublishedMs));
        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);

        _stats?.AddMarkers(current.Count - ended.Count);
        _stats?.AddTrackedFrame();
        if (attempts > 0)
            _stats?.AddTrackTick(elapsedMs, attempts, found, confirmedAttempts, confirmedFound);
        return true;
    }

    private LinePosition RefinePartial(PatchPlan plan, OpenCvSharp.Mat patch, FoundLine line, PixelRect found)
    {
        var fix = _tracker.Refine(plan, patch, found);
        var left = fix.CentreX - found.Width / 2.0;
        return new LinePosition(left + line.Box.Width / 2.0, fix.Top, line.Box.Width);
    }

    private static bool IsTrackingRegion(FrameRegion region) => region.Kind == RequestKind.TrackingRegion || region.Kind == RequestKind.FirstRegion;

    private static bool KeepsNameRowWhenLost(MarkerRecord record) => record.Status == PlateStatus.Confirmed;

    private bool WantsNameRow(MarkerRecord record) =>
        record.Status == PlateStatus.Confirmed && (_crowded.Contains(record.Id) || PartialLine.HasSpeedGroup(record, _settings));
}
