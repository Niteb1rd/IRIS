using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class StoppedWorker
{
    private readonly PipelineSettings _settings;
    private readonly ILineTracker _tracker;
    private readonly IUpdateSink _sink;
    private readonly IMonotonicClock _clock;
    private readonly TrackingSchedule _schedule = new();
    private int _epoch = int.MinValue;

    public StoppedWorker(PipelineSettings settings, ILineTracker tracker, IUpdateSink sink, IMonotonicClock clock)
    {
        _settings = settings;
        _tracker = tracker;
        _sink = sink;
        _clock = clock;
    }

    public bool Offer(double frameMs) => _schedule.Offer(frameMs, (int)Math.Round(_settings.StoppedTrackHz));

    public void Process(IndexSnapshot snapshot, CapturedFrame frame, PipelineEnvironment env)
    {
        if (snapshot.Epoch != _epoch)
        {
            _epoch = snapshot.Epoch;
            _schedule.Reset();
        }

        var stopped = snapshot.Markers.Where(m => m.Status == PlateStatus.Stopped).ToList();
        if (stopped.Count == 0)
            return;

        var workStartMs = _clock.NowMs;
        var frameMs = frame.FrameMs;
        var width = env.WindowWidth;
        var height = env.WindowHeight;
        var epoch = snapshot.Epoch;
        var interval = snapshot.NominalFrameIntervalMs;
        var regions = new Dictionary<int, FrameRegion>();
        foreach (var region in frame.Regions)
            if (region.Kind == RequestKind.StoppedRegion && region.MarkerId > 0)
                regions[region.MarkerId] = region;

        var claimed = new List<PixelRect>();
        foreach (var marker in snapshot.Markers)
            if (marker.Status != PlateStatus.Stopped)
                claimed.Add(marker.Line);

        var updates = new List<IndexUpdate>();
        var current = new Dictionary<int, MarkerRecord>();
        foreach (var marker in stopped)
        {
            var record = marker;
            if (regions.TryGetValue(marker.Id, out var region) && !region.Image.Empty())
            {
                var (px, py, margin, _) = marker.Predict(frameMs, _settings);
                var plan = new PatchPlan(marker.Id, region.Region, px, py, margin);
                if (_tracker.LocateLine(region.Image, plan, marker.Height, claimed) is { } line)
                {
                    claimed.Add(line);
                    var stored = PartialLine.StoredWidth(marker);
                    var gate = _settings.TrackWidthGateLines * marker.Height;
                    var speed = PartialLine.HasSpeedGroup(marker, _settings);
                    var isPartial = !speed && PartialLine.IsShorter(line.Width, stored, marker.Height, _settings) && PartialLine.LeftEdgeFits(line.X, px, marker.Height, _settings);
                    var wholeFromPart = speed && Math.Abs(line.Width - stored) <= gate && Math.Abs(line.Width - marker.Width) > gate;
                    var lineWidth = isPartial || wholeFromPart ? marker.Width : line.Width;
                    var distanceWidth = wholeFromPart ? line.Width : speed && line.Width > stored + gate ? stored : double.NaN;
                    var centre = line.X + lineWidth / 2.0;
                    record = marker.AfterHit(frameMs, centre, line.Y, lineWidth, distanceWidth);
                    updates.Add(new TrackingHitUpdate(PipelineGroup.Stopped, frameMs, epoch, marker.Id, centre, line.Y, lineWidth, distanceWidth));
                }
            }

            current[marker.Id] = record;
        }

        var ended = new HashSet<int>();
        if (env.Boundary is { } area)
        {
            var limit = TrackingBoundary.DropDistancePx(height);
            foreach (var (id, record) in current)
            {
                if (area.DistanceOutside(record.PlateBox, width, height) <= limit)
                    continue;
                ended.Add(id);
                updates.Add(new EndUpdate(PipelineGroup.Stopped, frameMs, epoch, id, EndReason.Boundary));
            }
        }

        var nowMs = _clock.NowMs;
        foreach (var (id, record) in current.OrderBy(kv => kv.Key))
        {
            if (ended.Contains(id))
                continue;
            if (RegionPlanner.TrackingPatch(record, frameMs, interval, false, width, height, _settings, out var expiryMs) is not { } rect)
                continue;
            var expires = frameMs + expiryMs;
            var spec = new RequestSpec(PipelineGroup.Stopped, RequestKind.StoppedRegion, RegionPlanner.ToPixelRect(rect), 0, frameMs, nowMs, expires, false);
            updates.Add(new RequestUpdate(PipelineGroup.Stopped, frameMs, epoch, id, spec));
        }

        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
    }
}
