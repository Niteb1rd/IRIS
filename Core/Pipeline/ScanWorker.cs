using System.Diagnostics;
using System.Globalization;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed partial class ScanWorker : IDisposable
{
    private const int KeepAliveLogLimit = 5;

    private sealed record PendingTick(long Tag);

    private sealed class KeepAlive : IDisposable
    {
        public Mat Template { get; } = new();

        public int OffsetX { get; set; }

        public int OffsetY { get; set; }

        public bool Following { get; set; }

        public double AnchorMs { get; set; } = double.NegativeInfinity;

        public double CentreX { get; set; }

        public double Top { get; set; }

        public void Dispose() => Template.Dispose();
    }

    private readonly PipelineSettings _settings;
    private readonly PipelineStats? _stats;
    private readonly IUpdateSink _sink;
    private readonly IMonotonicClock _clock;
    private readonly ILog _log;
    private readonly IClock _wall;
    private readonly Mat _grey = new();
    private Mat? _greyOf;
    private readonly Dictionary<int, KeepAlive> _keep = new();
    private int _keepAlivesLogged;
    private readonly Dictionary<int, int> _fails = new();
    private readonly Dictionary<int, int> _lost = new();
    private readonly HashSet<int> _seenConfirmed = new();
    private PendingTick? _pending;
    private double _scale = 1.0;
    private long _tick;
    private int _epoch = int.MinValue;

    public ScanWorker(PipelineSettings settings, IPlateImageOps ops, ITemplateMatcher? matcher, IUpdateSink sink, IMonotonicClock clock, PipelineStats? stats = null, ILog? log = null, IClock? wallClock = null)
    {
        _settings = settings;
        Ops = ops;
        Matcher = matcher;
        _sink = sink;
        _clock = clock;
        _stats = stats;
        _log = log ?? NullLog.Instance;
        _wall = wallClock ?? SystemClock.Instance;
    }

    public IPlateImageOps Ops { get; set; }

    public ITemplateMatcher? Matcher { get; set; }

    public bool HasPending => _pending is not null;

    public bool HasTemplate(int id) => _keep.TryGetValue(id, out var keep) && !keep.Template.Empty();

    public (double CentreX, double Top)? FollowPosition(int id)
    {
        if (_keep.TryGetValue(id, out var keep) && keep.Following)
            return (keep.CentreX, keep.Top);
        return null;
    }

    public int FailedChecks(int id) => _fails.GetValueOrDefault(id);

    public int LostChecks(int id) => _lost.GetValueOrDefault(id);

    public bool Accepts(CapturedFrame frame)
    {
        if (_pending is not { } pending)
            return false;
        foreach (var region in frame.Regions)
            if (region.Requester == PipelineGroup.Scan && region.Tag == pending.Tag && region.OneTime)
                return true;
        return false;
    }

    public void Prepare(IndexSnapshot snapshot, PipelineEnvironment env, double nowMs)
    {
        var workStartMs = _clock.NowMs;
        SyncEpoch(snapshot);
        if (_pending is not null)
            _stats?.AddSkipped(PipelineGroup.Scan);
        _tick++;
        var width = env.WindowWidth;
        var height = env.WindowHeight;
        var madeFrom = snapshot.NewFrameMs;
        var target = double.IsFinite(madeFrom) ? madeFrom + snapshot.NominalFrameIntervalMs : nowMs;
        var expires = nowMs + RegionPlanner.OneTimeExpiryMs(_settings.ScanRequestMs, snapshot.NominalFrameIntervalMs, _settings);
        var epoch = snapshot.Epoch;
        var updates = new List<IndexUpdate>();
        var stamp = double.IsFinite(madeFrom) ? madeFrom : nowMs;
        var freshMs = IndexModel.FreshnessLimitMs(snapshot, _settings);

        var area = new PixelRect(env.Geometry.Capture.X, env.Geometry.Capture.Y, env.Geometry.Capture.Width, env.Geometry.Capture.Height);
        updates.Add(new RequestUpdate(
            PipelineGroup.Scan, madeFrom, epoch, 0, new RequestSpec(PipelineGroup.Scan, RequestKind.ScanArea, area, _tick, madeFrom, nowMs, expires, true)));
        foreach (var marker in snapshot.Markers)
        {
            if (RegionPlanner.Crop(CropMarker(marker, stamp, freshMs, target), target, marker.Height * _settings.CheckMarginLines, width, height, _settings) is not { } crop)
                continue;
            updates.Add(new RequestUpdate(
                PipelineGroup.Scan, madeFrom, epoch, marker.Id,
                new RequestSpec(PipelineGroup.Scan, RequestKind.CheckCrop, RegionPlanner.ToPixelRect(crop), _tick, madeFrom, nowMs, expires, true)));
        }

        _pending = new PendingTick(_tick);
        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
    }

    private MarkerRecord CropMarker(MarkerRecord marker, double stampMs, double freshMs, double targetMs)
    {
        if (!_keep.TryGetValue(marker.Id, out var keep) || !keep.Following)
            return marker;
        if (marker.Status != PlateStatus.Confirmed || stampMs - marker.LastFoundMs <= freshMs)
        {
            keep.Following = false;
            return marker;
        }

        return marker with { CentreX = keep.CentreX, Top = keep.Top, LineMs = targetMs, Hist0 = null, Hist1 = null };
    }

    public void Process(IndexSnapshot snapshot, CapturedFrame frame, PipelineEnvironment env)
    {
        var workStartMs = _clock.NowMs;
        SyncEpoch(snapshot);
        if (_pending is not { } pending)
            return;
        _pending = null;
        _scale = env.WindowHeight / 1440.0;
        Forget(snapshot);
        foreach (var marker in snapshot.Markers)
        {
            if (marker.MarkedConfirmed && _seenConfirmed.Add(marker.Id))
            {
                DropKeep(marker.Id);
                _fails[marker.Id] = 0;
                _lost[marker.Id] = 0;
            }
            else if (!marker.MarkedConfirmed && _seenConfirmed.Remove(marker.Id))
            {
                DropKeep(marker.Id);
                _fails[marker.Id] = 0;
                _lost[marker.Id] = 0;
            }
        }

        var frameMs = frame.FrameMs;
        var epoch = snapshot.Epoch;
        var updates = new List<IndexUpdate>();

        FrameRegion? scan = null;
        var checks = new List<FrameRegion>();
        foreach (var region in frame.Regions)
        {
            if (region.Requester != PipelineGroup.Scan || region.Tag != pending.Tag || !region.OneTime)
                continue;
            if (region.Kind == RequestKind.ScanArea)
                scan = region;
            else if (region.Kind == RequestKind.CheckCrop)
                checks.Add(region);
        }

        if (scan is not null && !frame.WaitForScan(_settings.ScanReadWaitMs))
        {
            _stats?.AddScanUnavailable();
            scan = null;
        }

        if (scan is not null)
        {
            var started = Stopwatch.GetTimestamp();
            var lines = Ops.Scan(new CropImage(scan.Image, scan.Region, false), candidate => IsTrackedLine(candidate, snapshot, frameMs));
            _stats?.AddScan((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            var nowMs = _clock.NowMs;
            foreach (var line in lines)
            {
                if (IsTrackedLine(line, snapshot, frameMs))
                    continue;
                updates.Add(new NewMarkerUpdate(PipelineGroup.Scan, frameMs, epoch, line, FirstRequest(line, frameMs, snapshot, env, nowMs)));
            }
        }

        var claimed = new List<PixelRect>();
        foreach (var region in checks.OrderBy(r => r.MarkerId))
        {
            if (snapshot.Find(region.MarkerId) is not { } marker || region.Image.Empty())
                continue;
            RunCheck(marker, region, frameMs, epoch, snapshot, claimed, updates);
        }

        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
    }

    private bool IsTrackedLine(PixelRect line, IndexSnapshot snapshot, double frameMs)
    {
        foreach (var marker in snapshot.Markers)
        {
            var (px, py, _, _) = marker.Predict(frameMs, _settings);
            if (OnLine(line, marker.Line, marker.Height) || OnLine(line, new PixelRect(px, py, marker.Width, marker.Height), marker.Height))
                return true;
        }

        return false;
    }

    private bool OnLine(PixelRect line, PixelRect other, double lineHeight)
    {
        if (BoxMath.Overlap(line, other) > _settings.TrackedLineOverlap)
            return true;
        var half = _settings.TrackedLineOffsetLines * lineHeight;
        return Math.Abs(line.X + line.Width / 2.0 - (other.X + other.Width / 2.0)) <= half && Math.Abs(line.Y - other.Y) <= half;
    }

    private RequestSpec FirstRequest(PixelRect line, double frameMs, IndexSnapshot snapshot, PipelineEnvironment env, double nowMs)
    {
        var born = MarkerRecord.Born(0, line, frameMs);
        var region = RegionPlanner.Patch(born, frameMs + snapshot.NominalFrameIntervalMs, 0.0, line.Height * _settings.FirstRegionMarginLines, env.WindowWidth, env.WindowHeight, _settings, out _);
        var rect = region is { } r ? RegionPlanner.ToPixelRect(r) : line;
        return new RequestSpec(PipelineGroup.Scan, RequestKind.FirstRegion, rect, _tick, frameMs, nowMs, frameMs + RegionPlanner.OneTimeExpiryMs(_settings.FirstRequestMs, snapshot.NominalFrameIntervalMs, _settings), false);
    }

    private void DropKeep(int id)
    {
        if (_keep.Remove(id, out var keep))
            keep.Dispose();
    }

    private void End(int id, double frameMs, int epoch, List<IndexUpdate> updates)
    {
        updates.Add(new EndUpdate(PipelineGroup.Scan, frameMs, epoch, id, EndReason.MarkerCheck));
        DropKeep(id);
    }

    private void SyncEpoch(IndexSnapshot snapshot)
    {
        if (snapshot.Epoch == _epoch)
            return;
        _epoch = snapshot.Epoch;
        _pending = null;
        _fails.Clear();
        _lost.Clear();
        _seenConfirmed.Clear();
        DropAllKeeps();
    }

    private void Forget(IndexSnapshot snapshot)
    {
        var alive = snapshot.Markers.Select(m => m.Id).ToHashSet();
        foreach (var id in _fails.Keys.Where(id => !alive.Contains(id)).ToList())
            _fails.Remove(id);
        foreach (var id in _lost.Keys.Where(id => !alive.Contains(id)).ToList())
            _lost.Remove(id);
        _seenConfirmed.RemoveWhere(id => !alive.Contains(id));
        foreach (var id in _keep.Keys.Where(id => !alive.Contains(id)).ToList())
            DropKeep(id);
    }

    private void DropAllKeeps()
    {
        foreach (var keep in _keep.Values)
            keep.Dispose();
        _keep.Clear();
    }

    public void Dispose()
    {
        DropAllKeeps();
        _grey.Dispose();
    }
}
