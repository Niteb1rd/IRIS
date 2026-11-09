
using System.Diagnostics;
using System.Globalization;

namespace Iris.Core.Pipeline;

public sealed partial class MarkerIndex
{
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

        if (update.Missed is { Count: > 0 } missed)
        {
            foreach (var id in missed)
            {
                var index = Find(id);
                if (index >= 0 && _records[index].LastFoundMs < update.FrameMs)
                    _records[index] = _records[index] with { MissedFrames = _records[index].MissedFrames + 1 };
            }
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
