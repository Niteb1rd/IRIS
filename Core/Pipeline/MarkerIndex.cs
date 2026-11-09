using System.Diagnostics;
using System.Globalization;

namespace Iris.Core.Pipeline;

public sealed partial class MarkerIndex
{
    public const int LatencyWindow = 64;

    private readonly PipelineSettings _settings;
    private readonly List<MarkerRecord> _records = new();
    private readonly List<CaptureRequest> _requests = new();
    private readonly List<SessionMemoryEntry> _session = new();
    private readonly List<MarkerRecord> _recentlyEnded = new();
    private readonly object _subscriberGate = new();
    private IndexSubscription[] _subscribers = Array.Empty<IndexSubscription>();
    private IndexSnapshot _current = IndexSnapshot.Empty;
    private int _epoch;
    private int _nextId = 1;
    private long _nextRequestId = 1;
    private long _version;
    private double _newFrameMs = double.NegativeInfinity;
    private double _trackedFrameMs = double.NegativeInfinity;
    private double _prevTrackedFrameMs = double.NaN;
    private double _medianFrameIntervalMs = double.NaN;
    private double _trackingP95Ms;
    private readonly List<double> _latencies = new();
    private double _framePublishedP95Ms;
    private PipelineGroup? _newFrameBy;
    private PipelineGroup? _trackedFrameBy;
    private int _measuredInBatch;
    private TrackingBoundary? _boundary;
    private int _windowWidth;
    private int _windowHeight;
    private bool _lateNote;
    private bool _blankNote;
    private AberrationState _aberration;
    private bool _aberrationNote;
    private Iris.Core.Mining.MiningState? _mining;
    private double _miningMs = double.NegativeInfinity;
    private PipelineGroup? _miningBy;

    public ITraceSink? Trace { get; set; }

    private double _traceNowMs;

    public MarkerIndex(PipelineSettings settings, IndexCounters? counters = null)
    {
        _settings = settings;
        Counters = counters ?? new IndexCounters();
    }

    public IndexCounters Counters { get; }

    public IndexSnapshot Current => Volatile.Read(ref _current);

    public IndexSubscription Subscribe(IndexTrigger triggers)
    {
        var subscription = new IndexSubscription(this, triggers);
        lock (_subscriberGate)
            _subscribers = _subscribers.Append(subscription).ToArray();
        return subscription;
    }

    internal void Unsubscribe(IndexSubscription subscription)
    {
        lock (_subscriberGate)
            _subscribers = _subscribers.Where(s => !ReferenceEquals(s, subscription)).ToArray();
    }

    public IndexSnapshot Apply(IReadOnlyList<IndexUpdate> updates, double nowMs)
    {
        var started = Stopwatch.GetTimestamp();
        _measuredInBatch = 0;
        _traceNowMs = nowMs;
        var fired = IndexTrigger.None;
        if (updates.Count > 0 && Trace.Line(nowMs, "idx", "batch") is { } batch)
        {
            var first = updates[0];
            batch.Str("grp", first.Group.ToString()).Int("n", updates.Count).Num("fms", first.FrameMs).Num("sent", first.SentMs).Num("workStart", first.WorkStartMs).Array("types");
            foreach (var group in updates.GroupBy(u => u.GetType().Name))
                batch.Item().Str("type", group.Key).Int("n", group.Count());
            Trace!.Write(batch.EndArray().Text());
        }

        foreach (var update in updates)
            fired |= ApplyOne(update, nowMs);
        ExpireRequests(nowMs);
        _recentlyEnded.RemoveAll(m => nowMs - m.LastFoundMs > Math.Max(_settings.PanelStaleMs, _settings.PanelKeepMaxMs));
        _version++;
        var snapshot = BuildSnapshot();
        var applyMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        snapshot.PublishedMs = nowMs + applyMs;
        Volatile.Write(ref _current, snapshot);
        for (var i = 0; i < _measuredInBatch; i++)
            Counters.AddApply(applyMs);
        Signal(fired);
        return snapshot;
    }

    private void Signal(IndexTrigger fired)
    {
        if (fired == IndexTrigger.None)
            return;
        foreach (var subscriber in Volatile.Read(ref _subscribers))
            if ((subscriber.Mask & fired) != IndexTrigger.None)
                subscriber.Signal();
    }

    private IndexSnapshot BuildSnapshot() => new()
    {
        Version = _version,
        Epoch = _epoch,
        Markers = _records.ToArray(),
        Requests = _requests.ToArray(),
        SessionMemory = _session.ToArray(),
        RecentlyEnded = _recentlyEnded.ToArray(),
        NewFrameMs = _newFrameMs,
        TrackedFrameMs = _trackedFrameMs,
        PrevTrackedFrameMs = _prevTrackedFrameMs,
        MedianFrameIntervalMs = _medianFrameIntervalMs,
        TrackingP95Ms = _trackingP95Ms,
        FramePublishedP95Ms = _framePublishedP95Ms,
        Boundary = _boundary,
        WindowWidth = _windowWidth,
        WindowHeight = _windowHeight,
        LateFrameNote = _lateNote,
        BlankPictureNote = _blankNote,
        Aberration = _aberration,
        AberrationNote = _aberrationNote,
        Mining = _mining,
        CarriesTrackedFrame = _measuredInBatch > 0,
    };

    private static bool IsEpochFree(IndexUpdate update) => update is ResetUpdate or NewFrameUpdate or SettingsUpdate or LateNoteUpdate or BlankNoteUpdate or AberrationUpdate;

    private IndexTrigger ApplyOne(IndexUpdate update, double nowMs)
    {
        if (!IsEpochFree(update) && update.Epoch != _epoch)
        {
            RejectEpoch(update, nowMs);
            return IndexTrigger.None;
        }

        switch (update)
        {
            case ResetUpdate:
                Reset();
                return IndexTrigger.None;
            case NewMarkerUpdate marker:
                AddMarker(marker);
                return IndexTrigger.None;
            case TrackingHitUpdate hit:
                ApplyHit(hit, nowMs);
                return IndexTrigger.None;
            case PositionUpdate position:
                ApplyPosition(position, nowMs);
                return IndexTrigger.None;
            case TemplateHitUpdate template:
                ApplyTemplateHit(template, nowMs);
                return IndexTrigger.None;
            case StatusUpdate status:
                ApplyStatus(status, nowMs);
                return IndexTrigger.None;
            case ConfirmUpdate confirm:
                return ApplyConfirm(confirm, nowMs);
            case IdentityUpdate identity:
                ApplyIdentity(identity, nowMs);
                return IndexTrigger.None;
            case UnconfirmUpdate unconfirm:
                ApplyUnconfirm(unconfirm, nowMs);
                return IndexTrigger.None;
            case PlateColourUpdate colour:
                ApplyColour(colour, nowMs);
                return IndexTrigger.None;
            case EndUpdate end:
                ApplyEnd(end);
                return IndexTrigger.None;
            case RequestUpdate request:
                if (Find(request.MarkerId) >= 0 || request.MarkerId == 0)
                    AddRequest(request.MarkerId, request.Spec);
                return IndexTrigger.None;
            case NewFrameUpdate frame:
                return ApplyNewFrame(frame, nowMs);
            case FrameTrackedUpdate tracked:
                ApplyFrameTracked(tracked, nowMs);
                return IndexTrigger.None;
            case LateNoteUpdate note:
                _lateNote = note.Active;
                return IndexTrigger.None;
            case BlankNoteUpdate blank:
                _blankNote = blank.Active;
                return IndexTrigger.None;
            case AberrationUpdate aberration:
                if (_aberration != AberrationState.Off)
                    _aberration = aberration.State;
                _aberrationNote = aberration.Note && _aberration == AberrationState.On;
                return IndexTrigger.None;
            case MiningUpdate mining:
                ApplyMining(mining, nowMs);
                return IndexTrigger.None;
            case SettingsUpdate settings:
                _boundary = settings.Boundary;
                _windowWidth = settings.WindowWidth;
                _windowHeight = settings.WindowHeight;
                return IndexTrigger.SettingsChanged;
            default:
                return IndexTrigger.None;
        }
    }

    private void Reset()
    {
        _epoch++;
        _lateNote = false;
        _blankNote = false;
        _mining = null;
        _miningMs = double.NegativeInfinity;
        _miningBy = null;
        _records.Clear();
        _recentlyEnded.Clear();
        _requests.Clear();
        _trackedFrameMs = double.NegativeInfinity;
        _prevTrackedFrameMs = double.NaN;
        _newFrameMs = double.NegativeInfinity;
    }

    private void ApplyMining(MiningUpdate update, double nowMs)
    {
        if (update.FrameMs < _miningMs)
        {
            Reject(update, "mining", 0, update.FrameMs, _miningMs, _miningBy, nowMs);
            return;
        }

        _miningMs = update.FrameMs;
        _miningBy = update.Group;
        _mining = update.State;
        if (Trace.Line(_traceNowMs, "idx", "mining") is { } t)
            Trace!.Write(t.Str("grp", update.Group.ToString()).Num("fms", update.FrameMs).Bool("locked", update.State is not null)
                .Str("label", update.State?.Label).Num("value", update.State?.Value ?? double.NaN, 0).Text());
    }

    private int Find(int id)
    {
        for (var i = 0; i < _records.Count; i++)
            if (_records[i].Id == id)
                return i;
        return -1;
    }

    private void RejectEpoch(IndexUpdate update, double nowMs)
    {
        TraceReject(update, "epoch", 0, update.Epoch, _epoch, double.NaN);
        if (!Counters.Enabled)
            return;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Index rejected a stale {update.GetType().Name} from {update.Group} (epoch, marker 0): epoch {update.Epoch}, current {_epoch}; {TimingText(update, nowMs)}");
        Counters.Reject(update.Group, text);
    }

    private void Reject(IndexUpdate update, string field, int id, double got, double current, PipelineGroup? writer, double nowMs, double pxFromCurrent = double.NaN)
    {
        TraceReject(update, field, id, got, current, pxFromCurrent);
        if (!Counters.Enabled)
            return;
        var by = writer is { } group ? group.ToString() : "n/a";
        var px = double.IsFinite(pxFromCurrent) ? string.Create(CultureInfo.InvariantCulture, $"; {pxFromCurrent:0} px from current") : string.Empty;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Index rejected a stale {update.GetType().Name} from {update.Group} ({field}, marker {id}): frame {Ms(got)}, current {Ms(current)} written by {by}; {TimingText(update, nowMs)}{px}");
        Counters.Reject(update.Group, text);
    }

    private void TraceReject(IndexUpdate update, string field, int id, double got, double current, double pxFromCurrent)
    {
        if (Trace.Line(_traceNowMs, "idx", "reject") is { } t)
            Trace!.Write(t.Str("type", update.GetType().Name).Str("grp", update.Group.ToString()).Str("field", field).Int("mid", id).Num("fms", got).Num("current", current)
                .Num("px", pxFromCurrent, 1).Text());
    }

    private static string TimingText(IndexUpdate update, double nowMs) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"wait {Ms(update.WorkStartMs - update.FrameMs)} ms, work {Ms(update.SentMs - update.WorkStartMs)} ms, delivery {Ms(nowMs - update.SentMs)} ms; newest frame at start {Ms(update.NewestFrameAtStartMs)}");

    private static string Ms(double value) => double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    private static double Distance(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    private void AddMarker(NewMarkerUpdate update)
    {
        if (_records.Count >= _settings.MaxTrackedMarkers)
        {
            TraceNew(update, 0, "cap");
            return;
        }

        foreach (var other in _records)
            if (BoxMath.Overlap(update.Line, other.Line) > _settings.NewMarkerOverlap)
            {
                TraceNew(update, other.Id, "overlaps");
                return;
            }

        var record = MarkerRecord.Born(_nextId++, update.Line, update.FrameMs, update.Group) with { BornWithSpeedGroup = update.SpeedGroup, DistanceMetres = update.DistanceMetres };
        TraceNew(update, record.Id, null);
        _records.Add(record);
        if (update.FirstRequest is { } spec)
            AddRequest(record.Id, spec);
    }

    private void TraceNew(NewMarkerUpdate update, int id, string? dropped)
    {
        if (Trace.Line(_traceNowMs, "idx", "new") is { } t)
            Trace!.Write(t.Int("mid", id).Num("fms", update.FrameMs).Box("b", update.Line).Bool("speed", update.SpeedGroup).Str("dropped", dropped).Text());
    }

    private void ApplyHit(TrackingHitUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (update.FrameMs < record.LineMs)
            Reject(update, "Line", update.Id, update.FrameMs, record.LineMs, record.LineBy, nowMs, Distance(update.CentreX, update.Top, record.CentreX, record.Top));
        else
            record = record.WithLine(update.FrameMs, update.CentreX, update.Top, update.Width, update.DistanceWidth, update.Group);
        if (update.FrameMs < record.SamplesMs)
            Reject(update, "Samples", update.Id, update.FrameMs, record.SamplesMs, record.SamplesBy, nowMs);
        else
            record = record.WithSamples(update.FrameMs, update.CentreX, update.Top, update.Group);
        if (update.FrameMs >= record.LastFoundMs)
        {
            record = record with { MissedFrames = 0 };
            if (double.IsFinite(update.PredictionErrorPx))
                record = record with
                {
                    PredictionErrorPx = record.PredictionErrorPx + _settings.PredictionErrorSmoothing * (update.PredictionErrorPx - record.PredictionErrorPx),
                };
        }

        if (update.NameSeen && update.FrameMs > record.NameSeenMs)
            record = record with { NameSeenMs = update.FrameMs };
        _records[index] = record.WithFound(update.FrameMs);
    }

    private void ApplyPosition(PositionUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (update.FrameMs < record.LineMs)
        {
            Reject(update, "Line", update.Id, update.FrameMs, record.LineMs, record.LineBy, nowMs,
                Distance(update.Line.X + update.Line.Width / 2.0, update.Line.Y, record.CentreX, record.Top));
            return;
        }

        var moved = record.WithLineBox(update.FrameMs, update.Line, update.Group);
        _records[index] = double.IsFinite(update.DistanceWidth) ? moved with { DistanceWidth = update.DistanceWidth } : moved;
        if (Trace.Line(nowMs, "idx", "move") is { } t)
            Trace!.Write(t.Int("mid", update.Id).Str("grp", update.Group.ToString()).Num("fms", update.FrameMs).Box("from", record.Line).Box("to", update.Line)
                .Num("dist", Distance(record.CentreX, record.Top, update.Line.X + update.Line.Width / 2.0, update.Line.Y), 1).Num("lastFound", record.LastFoundMs).Text());
    }

    private void ApplyTemplateHit(TemplateHitUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (update.FrameMs < record.LineMs)
        {
            Reject(update, "Line", update.Id, update.FrameMs, record.LineMs, record.LineBy, nowMs, Distance(update.CentreX, update.Top, record.CentreX, record.Top));
            return;
        }

        var moved = record.WithLine(update.FrameMs, update.CentreX, update.Top, record.Width, record.DistanceWidth, update.Group);
        if (update.FrameMs >= record.SamplesMs)
            moved = moved.WithSamples(update.FrameMs, update.CentreX, update.Top, update.Group, template: true);
        _records[index] = moved;
        if (Trace.Line(nowMs, "idx", "tplmove") is { } t)
            Trace!.Write(t.Int("mid", update.Id).Num("fms", update.FrameMs).Box("from", record.Line).Box("to", moved.Line)
                .Num("dist", Distance(record.CentreX, record.Top, update.CentreX, update.Top), 1).Num("lastFound", record.LastFoundMs).Text());
    }

    private void ApplyStatus(StatusUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (update.FrameMs < record.StatusMs)
        {
            Reject(update, "Status", update.Id, update.FrameMs, record.StatusMs, record.StatusBy, nowMs);
            return;
        }

        var open = record.Status is PlateStatus.Unconfirmed or PlateStatus.Unclear;
        if (update.Status == PlateStatus.Stopped)
        {
            if (record.Status == PlateStatus.Stopped)
                return;
        }
        else if (!open || update.Status is not (PlateStatus.Unconfirmed or PlateStatus.Unclear))
        {
            return;
        }

        _records[index] = record with { Status = update.Status, StatusMs = update.FrameMs, StatusBy = update.Group };
        if (update.Status == PlateStatus.Stopped)
            _requests.RemoveAll(r => r.MarkerId == update.Id && !r.OneTime && r.Requester == PipelineGroup.Tracking);
    }

    private IndexTrigger ApplyConfirm(ConfirmUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return IndexTrigger.None;
        var record = _records[index];
        if (record.MarkedConfirmed)
        {
            Reject(update, "Offset", update.Id, update.FrameMs, record.OffsetMs, record.OffsetBy, nowMs);
            return IndexTrigger.None;
        }

        if (record.Status == PlateStatus.Stopped)
            return IndexTrigger.None;
        if (update.FrameMs < record.StatusMs)
        {
            Reject(update, "Status", update.Id, update.FrameMs, record.StatusMs, record.StatusBy, nowMs);
            return IndexTrigger.None;
        }

        _records[index] = record with
        {
            Status = PlateStatus.Confirmed,
            StatusMs = update.FrameMs,
            StatusBy = update.Group,
            OffsetBy = update.Group,
            MarkedConfirmed = true,
            Name = update.Name,
            NameMs = update.FrameMs,
            NameSeenMs = update.FrameMs,
            NameRel = update.NameRel,
            PlateRel = update.PlateRel,
            OffsetMs = update.FrameMs,
            NameLine = update.NameLine,
            Post0 = null,
            Post1 = null,
        };
        if (Trace.Line(nowMs, "idx", "confirm") is { } t)
            Trace!.Write(t.Int("mid", update.Id).Str("name", update.Name).Num("fms", update.FrameMs).Box("line", record.Line).Text());
        EndDuplicates(_records[index], update);
        return IndexTrigger.NameConfirmed;
    }

    private void EndDuplicates(MarkerRecord confirmed, ConfirmUpdate update)
    {
        List<int>? older = null;
        foreach (var other in _records)
        {
            if (other.Id == confirmed.Id || other.Status != PlateStatus.Confirmed || !other.MarkedConfirmed
                || !string.Equals(other.Name, confirmed.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var reach = _settings.DuplicateNameReachLines * Math.Max(other.Height, confirmed.Height);
            if (Distance(other.CentreX, other.Top, confirmed.CentreX, confirmed.Top) <= reach)
                (older ??= new List<int>()).Add(other.Id);
        }

        if (older is null)
            return;
        foreach (var id in older)
            ApplyEnd(new EndUpdate(update.Group, update.FrameMs, _epoch, id, EndReason.Duplicate));
    }

    private void ApplyIdentity(IdentityUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (update.FrameMs < record.IdentityMs)
        {
            Reject(update, "Identity", update.Id, update.FrameMs, record.IdentityMs, record.IdentityBy, nowMs);
            return;
        }

        _records[index] = record with { Identity = update.Identity, IdentityMs = update.FrameMs, IdentityBy = update.Group };
    }

    private void ApplyUnconfirm(UnconfirmUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (record.Status != PlateStatus.Confirmed || !record.MarkedConfirmed)
            return;
        if (update.FrameMs < record.StatusMs)
        {
            Reject(update, "Status", update.Id, update.FrameMs, record.StatusMs, record.StatusBy, nowMs);
            return;
        }

        _records[index] = record with
        {
            Status = PlateStatus.Unconfirmed,
            StatusMs = update.FrameMs,
            StatusBy = update.Group,
            OffsetBy = null,
            MarkedConfirmed = false,
            Name = null,
            NameMs = double.NegativeInfinity,
            NameRel = null,
            PlateRel = null,
            OffsetMs = double.NegativeInfinity,
            NameLine = null,
            Identity = null,
            IdentityMs = update.FrameMs,
            IdentityBy = update.Group,
            Post0 = null,
            Post1 = null,
        };
    }

    private void ApplyColour(PlateColourUpdate update, double nowMs)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        if (update.FrameMs < record.PlateColourMs)
        {
            Reject(update, "PlateColour", update.Id, update.FrameMs, record.PlateColourMs, record.PlateColourBy, nowMs);
            return;
        }

        _records[index] = record with { PlateColour = update.Colour, PlateColourMs = update.FrameMs, PlateColourBy = update.Group };
    }

    private void ApplyEnd(EndUpdate update)
    {
        var index = Find(update.Id);
        if (index < 0)
            return;
        var record = _records[index];
        _records.RemoveAt(index);
        _requests.RemoveAll(r => r.MarkerId == update.Id);
        if (Trace.Line(_traceNowMs, "idx", "end") is { } t)
            Trace!.Write(t.Int("mid", update.Id).Str("grp", update.Group.ToString()).Str("why", update.Reason.ToString()).Num("fms", update.FrameMs).Str("name", record.Name)
                .Box("line", record.Line).Num("lastFound", record.LastFoundMs).Text());
        if (IndexModel.IsListed(record))
        {
            _recentlyEnded.RemoveAll(m => string.Equals(m.Identity!.Handle, record.Identity!.Handle, StringComparison.OrdinalIgnoreCase));
            _recentlyEnded.Add(record);
        }
        if (!record.MarkedConfirmed || record.Name is not { } name)
            return;
        var size = record.PlateRel ?? record.Line;
        _session.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        _session.Add(new SessionMemoryEntry(name, size.Width, size.Height, update.FrameMs, update.Reason, record.Height, record.NameLine?.Fingerprint, record.Identity));
        while (_session.Count > _settings.MaxSessionMemory)
            _session.RemoveAt(0);
    }
}
