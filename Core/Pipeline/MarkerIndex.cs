using System.Diagnostics;
using System.Globalization;

namespace Iris.Core.Pipeline;

public sealed class IndexSubscription : IDisposable
{
    private readonly MarkerIndex _owner;
    private readonly AutoResetEvent _event = new(false);

    internal IndexSubscription(MarkerIndex owner, IndexTrigger mask)
    {
        _owner = owner;
        Mask = mask;
    }

    public IndexTrigger Mask { get; }

    public WaitHandle Handle => _event;

    public bool Wait(int timeoutMs) => _event.WaitOne(timeoutMs);

    internal void Signal()
    {
        try
        {
            _event.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _owner.Unsubscribe(this);
        _event.Dispose();
    }
}

public sealed class MarkerIndex
{
    public const int LatencyWindow = 64;

    private readonly PipelineSettings _settings;
    private readonly List<MarkerRecord> _records = new();
    private readonly List<CaptureRequest> _requests = new();
    private readonly List<SessionMemoryEntry> _session = new();
    private readonly object _subscriberGate = new();
    private IndexSubscription[] _subscribers = Array.Empty<IndexSubscription>();
    private IndexSnapshot _current = IndexSnapshot.Empty;
    private int _epoch;
    private int _nextId = 1;
    private long _nextRequestId = 1;
    private long _version;
    private double _newFrameMs = double.NegativeInfinity;
    private double _nameConfirmedMs = double.NegativeInfinity;
    private double _settingsChangedMs = double.NegativeInfinity;
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
        var fired = IndexTrigger.None;
        foreach (var update in updates)
            fired |= ApplyOne(update, nowMs);
        ExpireRequests(nowMs);
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
        NewFrameMs = _newFrameMs,
        NameConfirmedMs = _nameConfirmedMs,
        SettingsChangedMs = _settingsChangedMs,
        TrackedFrameMs = _trackedFrameMs,
        PrevTrackedFrameMs = _prevTrackedFrameMs,
        MedianFrameIntervalMs = _medianFrameIntervalMs,
        TrackingP95Ms = _trackingP95Ms,
        FramePublishedP95Ms = _framePublishedP95Ms,
        Boundary = _boundary,
        WindowWidth = _windowWidth,
        WindowHeight = _windowHeight,
        LateFrameNote = _lateNote,
        CarriesTrackedFrame = _measuredInBatch > 0,
    };

    private static bool IsEpochFree(IndexUpdate update) => update is ResetUpdate or NewFrameUpdate or SettingsUpdate or LateNoteUpdate;

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
            case SettingsUpdate settings:
                _boundary = settings.Boundary;
                _windowWidth = settings.WindowWidth;
                _windowHeight = settings.WindowHeight;
                _settingsChangedMs = Math.Max(_settingsChangedMs, settings.FrameMs);
                return IndexTrigger.SettingsChanged;
            default:
                return IndexTrigger.None;
        }
    }

    private void Reset()
    {
        _epoch++;
        _lateNote = false;
        _records.Clear();
        _requests.Clear();
        _trackedFrameMs = double.NegativeInfinity;
        _prevTrackedFrameMs = double.NaN;
        _newFrameMs = double.NegativeInfinity;
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
        if (!Counters.Enabled)
            return;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Index rejected a stale {update.GetType().Name} from {update.Group} (epoch, marker 0): epoch {update.Epoch}, current {_epoch}; {TimingText(update, nowMs)}");
        Counters.Reject(update.Group, text);
    }

    private void Reject(IndexUpdate update, string field, int id, double got, double current, PipelineGroup? writer, double nowMs, double pxFromCurrent = double.NaN)
    {
        if (!Counters.Enabled)
            return;
        var by = writer is { } group ? group.ToString() : "n/a";
        var px = double.IsFinite(pxFromCurrent) ? string.Create(CultureInfo.InvariantCulture, $"; {pxFromCurrent:0} px from current") : string.Empty;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Index rejected a stale {update.GetType().Name} from {update.Group} ({field}, marker {id}): frame {Ms(got)}, current {Ms(current)} written by {by}; {TimingText(update, nowMs)}{px}");
        Counters.Reject(update.Group, text);
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
            return;
        foreach (var other in _records)
            if (BoxMath.Overlap(update.Line, other.Line) > _settings.NewMarkerOverlap)
                return;
        var record = MarkerRecord.Born(_nextId++, update.Line, update.FrameMs, update.Group);
        _records.Add(record);
        if (update.FirstRequest is { } spec)
            AddRequest(record.Id, spec);
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
            NameRel = update.NameRel,
            PlateRel = update.PlateRel,
            OffsetMs = update.FrameMs,
            NameLine = update.NameLine,
            Post0 = null,
            Post1 = null,
        };
        _nameConfirmedMs = Math.Max(_nameConfirmedMs, update.FrameMs);
        return IndexTrigger.NameConfirmed;
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
        if (!record.MarkedConfirmed || record.Name is not { } name)
            return;
        var size = record.PlateRel ?? record.Line;
        _session.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        _session.Add(new SessionMemoryEntry(name, size.Width, size.Height, update.FrameMs, update.Reason, record.Height));
        while (_session.Count > _settings.MaxSessionMemory)
            _session.RemoveAt(0);
    }

    private void AddRequest(int markerId, RequestSpec spec)
    {
        if (!spec.OneTime)
        {
            var existing = _requests.FindIndex(r => !r.OneTime && r.MarkerId == markerId && r.Requester == spec.Requester);
            if (existing >= 0 && spec.MadeFromFrameMs < _requests[existing].MadeFromFrameMs)
            {
                if (Counters.Enabled)
                    Counters.Reject(spec.Requester, string.Create(
                        CultureInfo.InvariantCulture,
                        $"Index rejected a stale request from {spec.Requester} (marker {markerId}): made from {spec.MadeFromFrameMs:0.0}, the current one from {_requests[existing].MadeFromFrameMs:0.0}"));
                return;
            }

            if (spec.Requester is PipelineGroup.Tracking or PipelineGroup.Stopped)
                _requests.RemoveAll(r => !r.OneTime && r.MarkerId == markerId && r.Requester == PipelineGroup.Scan);
            existing = _requests.FindIndex(r => !r.OneTime && r.MarkerId == markerId && r.Requester == spec.Requester);
            Store(existing, markerId, spec);
            return;
        }

        var same = _requests.FindIndex(r => r.OneTime && r.MarkerId == markerId && r.Requester == spec.Requester && r.Kind == spec.Kind);
        if (same >= 0 && spec.MadeAtMs < _requests[same].MadeAtMs)
        {
            if (Counters.Enabled)
                Counters.Reject(spec.Requester, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Index rejected a stale request from {spec.Requester} (marker {markerId}): made at {spec.MadeAtMs:0.0}, the current one at {_requests[same].MadeAtMs:0.0}"));
            return;
        }

        Store(same, markerId, spec);
    }

    private void Store(int existing, int markerId, RequestSpec spec)
    {
        var request = new CaptureRequest(
            _nextRequestId++, spec.Requester, spec.Kind, markerId, spec.Region, spec.Tag, spec.MadeFromFrameMs, spec.MadeAtMs, spec.ExpiresAtMs, spec.OneTime, false);
        if (existing >= 0)
            _requests[existing] = request;
        else
            _requests.Add(request);
    }

    private IndexTrigger ApplyNewFrame(NewFrameUpdate update, double nowMs)
    {
        if (update.FrameMs < _newFrameMs)
        {
            Reject(update, "NewFrame", 0, update.FrameMs, _newFrameMs, _newFrameBy, nowMs);
            return IndexTrigger.None;
        }

        foreach (var id in update.RequestIds)
        {
            var index = _requests.FindIndex(r => r.Id == id);
            if (index < 0)
                continue;
            if (_requests[index].OneTime)
                _requests.RemoveAt(index);
            else
                _requests[index] = _requests[index] with { Fulfilled = true };
        }

        _newFrameMs = update.FrameMs;
        _newFrameBy = update.Group;
        _medianFrameIntervalMs = update.MedianIntervalMs;
        return IndexTrigger.NewFrame;
    }

    private void ApplyFrameTracked(FrameTrackedUpdate update, double nowMs)
    {
        if (update.FrameMs < _trackedFrameMs)
        {
            Reject(update, "TrackedFrame", 0, update.FrameMs, _trackedFrameMs, _trackedFrameBy, nowMs);
            return;
        }

        _prevTrackedFrameMs = update.FrameMs - _trackedFrameMs <= _settings.MaxLoopGapMs ? _trackedFrameMs : double.NaN;
        _trackedFrameMs = update.FrameMs;
        _trackedFrameBy = update.Group;
        _trackingP95Ms = update.TrackingP95Ms;
        var latency = nowMs - update.ArrivalMs;
        Counters.AddLatency(latency);
        if (latency >= 0.0)
        {
            _latencies.Add(latency);
            while (_latencies.Count > LatencyWindow)
                _latencies.RemoveAt(0);
            _framePublishedP95Ms = LiveStats.Percentile95(_latencies);
            if (double.IsFinite(update.WorkStartMs) && double.IsFinite(update.SentMs))
            {
                var published = double.IsFinite(update.PublishedMs) ? Math.Clamp(update.PublishedMs, Math.Min(update.ArrivalMs, update.WorkStartMs), Math.Max(update.ArrivalMs, update.WorkStartMs)) : update.ArrivalMs;
                Counters.AddBreakdown(
                    Math.Max(0.0, update.ArrivalMs - update.FrameMs), published - update.ArrivalMs, update.WorkStartMs - published, update.SentMs - update.WorkStartMs, nowMs - update.SentMs);
                _measuredInBatch++;
            }
        }
    }

    private void ExpireRequests(double nowMs)
    {
        for (var i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            if (!(request.ExpiresAtMs < nowMs))
                continue;
            _requests.RemoveAt(i);
            if (!request.Fulfilled)
                Counters.Expired(request.Requester);
        }
    }
}
