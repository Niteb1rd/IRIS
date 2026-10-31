using System.Diagnostics;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class ReadsWorker
{
    private sealed record PendingTick(long Tag, long Tick, int Candidates, int Budget);

    private readonly PipelineSettings _settings;
    private readonly PlateNameJudge _judge;
    private readonly ReadPolicy _policy;
    private readonly WhiteDuplicateRule _duplicates;
    private readonly SessionExclusions _session;
    private readonly SessionNames _names;
    private readonly PipelineStats? _stats;
    private readonly ILog _log;
    private readonly IClock _wall;
    private readonly IUpdateSink _sink;
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<int, PlateState> _states = new();
    private readonly HashSet<string> _confirmedLogged = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SessionMemoryEntry> _memory = Array.Empty<SessionMemoryEntry>();
    private PendingTick? _pending;
    private long _tick;
    private int _epoch = int.MinValue;

    public ReadsWorker(
        PipelineSettings settings, IPlateImageOps ops, PlateNameJudge judge, SessionExclusions session, IUpdateSink sink, IMonotonicClock clock,
        SessionNames? names = null, PipelineStats? stats = null, ILog? log = null, IClock? wallClock = null, INameResolver? resolver = null)
    {
        _settings = settings;
        Ops = ops;
        _judge = judge;
        _session = session;
        _sink = sink;
        _clock = clock;
        _names = names ?? new SessionNames();
        _stats = stats;
        _log = log ?? NullLog.Instance;
        _wall = wallClock ?? SystemClock.Instance;
        _duplicates = new WhiteDuplicateRule(settings.DuplicateWindowTicks);
        _policy = new ReadPolicy(settings, resolver ?? new EvidenceNameResolver(), judge, _duplicates, _names);
    }

    public IPlateImageOps Ops { get; set; }

    public IReadOnlyDictionary<int, PlateState> States => _states;

    public bool HasPending => _pending is not null;

    public bool Accepts(CapturedFrame frame)
    {
        if (_pending is not { } pending)
            return false;
        foreach (var region in frame.Regions)
            if (region.Requester == PipelineGroup.Reads && region.Tag == pending.Tag && region.OneTime)
                return true;
        return false;
    }

    public void Prepare(IndexSnapshot snapshot, PipelineEnvironment env, double nowMs, SocialConfig config)
    {
        var workStartMs = _clock.NowMs;
        SyncEpoch(snapshot);
        _judge.Config = config;
        _judge.NowMs = nowMs;
        ForgetEnded(snapshot);
        if (_pending is not null)
            _stats?.AddSkipped(PipelineGroup.Reads);
        _tick++;
        _stats?.AddPlateTick();

        _memory = snapshot.SessionMemory;
        var epoch = snapshot.Epoch;
        var stamp = Stamp(snapshot, nowMs);
        var updates = new List<IndexUpdate>();
        ReturnNotFoundToReading(snapshot, stamp, epoch, updates);
        var width = env.WindowWidth;
        var height = env.WindowHeight;
        var scale = height / 1440.0;
        var (centreX, centreY) = RegionPlanner.ScanCentre(env.Geometry, env.Boundary, _settings);
        var list = new List<ReadCandidate>();
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status is not (PlateStatus.Unconfirmed or PlateStatus.Unclear))
                continue;
            var dx = marker.X + marker.Width / 2.0 - centreX;
            var dy = marker.Y - centreY;
            var last = _states.TryGetValue(marker.Id, out var state) ? state.LastReadMs : double.NegativeInfinity;
            list.Add(new ReadCandidate(marker.Id, marker.Status == PlateStatus.Unclear, Math.Sqrt(dx * dx + dy * dy), last));
        }

        var selected = ReadScheduler.Select(list, _settings.MaxReadsPerTick);
        var madeFrom = snapshot.NewFrameMs;
        var target = double.IsFinite(madeFrom) ? madeFrom + snapshot.NominalFrameIntervalMs : nowMs;
        var readExpiryMs = RegionPlanner.OneTimeExpiryMs(_settings.ReadRequestMs, snapshot.NominalFrameIntervalMs, _settings);
        foreach (var id in selected)
        {
            if (snapshot.Find(id) is not { } marker)
                continue;
            if (_states.TryGetValue(id, out var known) && known.Status is PlateStatus.Confirmed or PlateStatus.Stopped)
                continue;
            if (RegionPlanner.Crop(marker, target, _settings.ReadMarginPx * scale, width, height, _settings) is not { } crop)
                continue;
            var spec = new RequestSpec(PipelineGroup.Reads, RequestKind.ReadCrop, RegionPlanner.ToPixelRect(crop), _tick, madeFrom, nowMs, nowMs + readExpiryMs, true);
            updates.Add(new RequestUpdate(PipelineGroup.Reads, stamp, epoch, id, spec));
        }

        _pending = updates.Any(u => u is RequestUpdate) ? new PendingTick(_tick, _tick, list.Count, selected.Count) : null;
        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060", Justification = "same Process signature as the other workers")]
    public void Process(IndexSnapshot snapshot, CapturedFrame frame, PipelineEnvironment env, SocialConfig config)
    {
        var workStartMs = _clock.NowMs;
        SyncEpoch(snapshot);
        if (_pending is not { } pending)
            return;
        _pending = null;
        ForgetEnded(snapshot);
        _judge.Config = config;
        _judge.NowMs = frame.FrameMs;
        _memory = snapshot.SessionMemory;

        var updates = new List<IndexUpdate>();
        var freshnessMs = IndexModel.FreshnessLimitMs(snapshot, _settings);
        var readStart = Stopwatch.GetTimestamp();
        var reads = 0;
        foreach (var region in frame.Regions)
        {
            if (region.Requester != PipelineGroup.Reads || region.Kind != RequestKind.ReadCrop || region.Tag != pending.Tag || !region.OneTime || region.Image.Empty())
                continue;
            if (snapshot.Find(region.MarkerId) is not { } marker)
                continue;
            var (px, py, _, _) = marker.Predict(frame.FrameMs, _settings);
            var item = new ReadItem(marker.Id, new CropImage(region.Image, region.Region, false), new PixelRect(px, py, marker.Width, marker.Height), marker.Height);
            ReadOne(frame, pending, item, marker, freshnessMs, updates);
            reads++;
        }

        if (reads > 0)
            _stats?.AddReadTick(reads, _settings.MaxReadsPerTick, Elapsed(readStart));

        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
    }

    private void ReadOne(
        CapturedFrame frame, PendingTick pending, ReadItem item, MarkerRecord marker, double freshnessMs, List<IndexUpdate> updates)
    {
        var frameMs = frame.FrameMs;
        if (!_states.TryGetValue(item.Id, out var state))
            _states[item.Id] = state = new PlateState(item.Id, frameMs);
        if (state.Status is PlateStatus.Confirmed or PlateStatus.Stopped)
            return;

        var result = Ops.Read(item);
        var nameBox = result.Rows is { Count: > 0 } rows ? rows[^1] : (PixelRect?)null;
        var observation = new ReadObservation(
            frameMs, frame.Sequence, result.Class, result.Name, result.O0 ?? Array.Empty<Detection.O0Glyph>(),
            nameBox?.Width ?? 0.0, result.Color, nameBox, result.Reason, item.LineHeight);
        var outcome = _policy.Apply(state, observation, pending.Tick, _memory);

        if (outcome.DuplicateOf is not null && outcome.Name is { } duplicate)
            ExcludeDuplicate(duplicate, outcome.DuplicateOf.Value, frameMs, updates);

        if (outcome.Event == PolicyEvent.Stopped)
        {
            updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, PlateStatus.Stopped));
            _stats?.AddStopped();
        }
        else if (outcome.Event == PolicyEvent.Confirmed && result.Anchor is { } anchor && result.Rows is { Count: > 0 } nameRows)
        {
            if (outcome.Restored)
            {
                _log.Write(_wall.UtcNow, $"Restored: {outcome.Name}");
                _stats?.AddRestored();
            }
            else if (_confirmedLogged.Add(outcome.Name!))
                _log.Write(_wall.UtcNow, $"Confirmed: {outcome.Name}");

            Confirm(state, outcome.Name!, item, anchor, nameRows, result.NameLine, frameMs, updates);
            _stats?.AddConfirmed();
        }
        else if (state.Status is PlateStatus.Unconfirmed or PlateStatus.Unclear)
        {
            if (marker.Status != state.Status)
                updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, state.Status));
            if (result.Anchor is { } found && marker.Status != PlateStatus.Confirmed && frameMs - marker.LastFoundMs > freshnessMs)
                updates.Add(new PositionUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, found));
        }

        if (result.Color is { } colour && colour != marker.PlateColour)
            updates.Add(new PlateColourUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, colour));
    }

    private void Confirm(
        PlateState state, string name, ReadItem item, PixelRect anchor, IReadOnlyList<PixelRect> rows, NameLineInfo? nameLine, double frameMs, List<IndexUpdate> updates)
    {
        var plate = Union(rows.Append(anchor));
        var nameBox = ClipToCrop(Union(rows), item.Crop);
        var origin = item.Crop.Origin;
        var local = new OpenCvSharp.Rect((int)Math.Round(anchor.X - origin.X), (int)Math.Round(anchor.Y - origin.Y), (int)Math.Round(anchor.Width), (int)Math.Round(anchor.Height));
        var fix = LineSubPixel.Measure(item.Crop.Image, local);
        var centreX = fix.Refined ? fix.CentreX + origin.X : anchor.X + anchor.Width / 2.0;
        var top = fix.Refined ? fix.Top + origin.Y : anchor.Y;
        var line = new PixelRect(centreX - anchor.Width / 2.0, top, anchor.Width, anchor.Height);
        var offset = new PixelRect(nameBox.X - centreX, nameBox.Y - top, nameBox.Width, nameBox.Height);
        var rel = new PixelRect(plate.X - centreX, plate.Y - top, plate.Width, plate.Height);

        updates.Add(new PositionUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, line));
        updates.Add(new ConfirmUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, name, offset, rel, nameLine));
    }

    private void ExcludeDuplicate(string name, int otherId, double frameMs, List<IndexUpdate> updates)
    {
        if (_session.TryAdd(name))
            _log.Write(_wall.UtcNow, $"Session exclusion: {name} (seen on 2+ white nameplates)");
        foreach (var other in _states.Values)
        {
            var named = other.Status == PlateStatus.Confirmed && other.Name is { } known && string.Equals(known, name, StringComparison.OrdinalIgnoreCase);
            if (!named && !(other.Id == otherId && other.Status == PlateStatus.Confirmed))
                continue;
            other.Status = PlateStatus.Stopped;
            other.StopReason = ReadKind.Excluded.ToString();
            updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, other.Id, PlateStatus.Stopped));
            _stats?.AddStopped();
        }
    }

    private void ReturnNotFoundToReading(IndexSnapshot snapshot, double stamp, int epoch, List<IndexUpdate> updates)
    {
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status != PlateStatus.Confirmed || marker.Name is not { } name || marker.Identity is not { Rsi: RsiState.NotFound } identity)
                continue;
            if (!string.Equals(identity.Handle, name, StringComparison.OrdinalIgnoreCase))
                continue;
            var known = _states.TryGetValue(marker.Id, out var state);
            if (known && state!.Status == PlateStatus.Stopped)
                continue;
            _states[marker.Id] = new PlateState(marker.Id, known ? state!.BornMs : stamp)
            {
                EverNamed = true,
                ReadsTotal = known ? state!.ReadsTotal : 0,
                IgnoredReads = known ? state!.IgnoredReads : 0,
            };
            updates.Add(new UnconfirmUpdate(PipelineGroup.Reads, stamp, epoch, marker.Id));
        }
    }

    private static double Stamp(IndexSnapshot snapshot, double nowMs) => double.IsFinite(snapshot.NewFrameMs) ? snapshot.NewFrameMs : nowMs;

    private void SyncEpoch(IndexSnapshot snapshot)
    {
        if (snapshot.Epoch == _epoch)
            return;
        _epoch = snapshot.Epoch;
        _states.Clear();
        _duplicates.Clear();
        _judge.ForgetMemo();
        _pending = null;
    }

    private void ForgetEnded(IndexSnapshot snapshot)
    {
        if (_states.Count == 0)
            return;
        foreach (var id in _states.Keys.ToList())
        {
            if (snapshot.Find(id) is not null)
                continue;
            _states.Remove(id);
            _duplicates.Forget(id);
        }
    }

    private static PixelRect ClipToCrop(PixelRect box, CropImage crop)
    {
        var left = Math.Max(0, (int)Math.Round(box.X - crop.Origin.X));
        var top = Math.Max(0, (int)Math.Round(box.Y - crop.Origin.Y));
        var right = Math.Min(crop.Image.Width, (int)Math.Round(box.X - crop.Origin.X) + (int)Math.Round(box.Width));
        var bottom = Math.Min(crop.Image.Height, (int)Math.Round(box.Y - crop.Origin.Y) + (int)Math.Round(box.Height));
        if (right <= left || bottom <= top)
            return box;
        return new PixelRect(left + crop.Origin.X, top + crop.Origin.Y, right - left, bottom - top);
    }

    private static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return PixelRect.FromEdges(left, top, right, bottom);
    }

    private static double Elapsed(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
}
