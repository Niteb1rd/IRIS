
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using OpenCvSharp;

namespace Iris.App.Services;

public sealed partial class LiveDetectionService
{
    private sealed record FrameInFlight(
        CapturedFrame Frame,
        double FrameMs,
        double ArrivedAt,
        double IssuedAt,
        double IssueMs,
        int Epoch,
        List<(CaptureRequest Request, Rect Window)> Requests,
        List<Mat> PatchBuffers,
        long[] RequestIds,
        ReadbackTicket Patches,
        ReadbackTicket Scan);

    private sealed record ScanInFlight(CapturedFrame Frame, ReadbackTicket Ticket, double PublishedAt, long Sequence);

    private bool CopiesOnTheirWay => _framesInFlight.Count > 0 || _scansInFlight.Count > 0;

    private void IssueFrame(double frameMs, double arrivedAt, int width, int height)
    {
        var source = _source!;
        if (!source.CanTakeFrame)
        {
            source.ReleaseHeldFrame();
            _stats?.AddReadbackFull();
            if (Trace("cap", "readback_full") is { } fullLine)
                Emit(fullLine.Num("fms", frameMs).Int("frames", _framesInFlight.Count).Int("scans", _scansInFlight.Count));
            return;
        }

        var snapshot = _index.Current;
        var frame = _frames.Rent();
        if (frame is null)
        {
            source.ReleaseHeldFrame();
            if (Trace("cap", "pool_empty") is { } poolLine)
                Emit(poolLine.Num("fms", frameMs));
            return;
        }

        var queued = false;
        CopyIssue? issued = null;
        try
        {
            frame.Begin(frameMs, arrivedAt, width, height);
            var chosen = CaptureRule.SelectUnseen(snapshot.Requests, frameMs, _newestSeenRequestId);
            var scanSlot = source.CanTakeScan;
            var skippedScan = false;
            var requests = new List<(CaptureRequest Request, Rect Window)>(chosen.Count);
            foreach (var request in chosen.OrderBy(r => r.Kind == RequestKind.ScanArea ? 0 : 1))
            {
                var rect = RegionPlanner.ToWindowRect(request.Region, width, height);
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;
                if (request.Kind == RequestKind.ScanArea)
                {
                    if (!scanSlot)
                    {
                        skippedScan = true;
                        continue;
                    }

                    if (requests.Count > 0)
                        continue;
                }

                requests.Add((request, rect));
            }

            if (skippedScan)
            {
                _stats?.AddReadbackScanSkipped();
                if (Trace("cap", "scan_skipped") is { } skippedLine)
                    Emit(skippedLine.Num("fms", frameMs).Int("scans", _scansInFlight.Count));
            }

            var firstIsScan = requests.Count > 0 && requests[0].Request.Kind == RequestKind.ScanArea;
            var issuedAt = _clock.NowMs;
            var issue = requests.Count == 0
                ? new CopyIssue(GrabStatus.Ok, null, default, default, 0.0)
                : source.IssueHeldFrameCopies(requests.Select(r => r.Window).ToList(), firstIsScan);
            if (requests.Count == 0)
                source.ReleaseHeldFrame();
            if (requests.Count > 0)
                issued = issue;
            if (issue.Status == GrabStatus.Unavailable)
            {
                issued = null;
                _stats?.AddUnavailable();
                NoteCaptureProblem(source, issue.Message);
                return;
            }

            _lastCaptureProblem = null;
            var patchBuffers = new List<Mat>(requests.Count);
            var patch = 0;
            foreach (var (request, window) in requests)
            {
                var buffer = request.Kind == RequestKind.ScanArea ? frame.ScanBuffer : frame.PatchBuffer(patch++);
                frame.Add(request, window, buffer);
                if (request.Kind != RequestKind.ScanArea)
                    patchBuffers.Add(buffer);
            }

            if (firstIsScan)
                frame.MarkScanPending();
            _newestSeenRequestId = Math.Max(_newestSeenRequestId, CaptureRule.NewestId(snapshot.Requests));
            var inFlight = new FrameInFlight(frame, frameMs, arrivedAt, issuedAt, issue.IssueMs, snapshot.Epoch, requests, patchBuffers, frame.RequestIds.ToArray(),
                issue.Patches, firstIsScan ? issue.Scan : default);
            queued = true;
            if (requests.Count == 0 && _framesInFlight.Count == 0)
                Publish(inFlight, 0.0);
            else
                _framesInFlight.Enqueue(inFlight);
        }
        finally
        {
            if (!queued)
            {
                if (issued is { } copies)
                {
                    source.Abandon(copies.Patches);
                    source.Abandon(copies.Scan);
                }

                frame.Release();
            }
        }
    }

    private void CollectReadbacks(IFrameSource source)
    {
        var now = _clock.NowMs;
        while (_framesInFlight.Count > 0)
        {
            var next = _framesInFlight.Peek();
            string? problem = null;
            var started = Stopwatch.GetTimestamp();
            var state = ReadbackState.Done;
            if (!next.Patches.IsNone)
            {
                state = source.Poll(next.Patches);
                if (state == ReadbackState.Done)
                    state = source.ReadInto(next.Patches, next.PatchBuffers, false, out problem);
            }

            if (state == ReadbackState.Pending && now - next.ArrivedAt <= LiveSettings.ReadbackFrameGiveUpMs)
                break;
            _framesInFlight.Dequeue();
            if (state == ReadbackState.Pending)
            {
                source.Abandon(next.Patches);
                _stats?.AddReadbackFrameGivenUp();
                if (Trace("cap", "readback_giveup") is { } giveUpLine)
                    Emit(giveUpLine.Num("fms", next.FrameMs).Num("waitMs", now - next.IssuedAt));
                DropFrame(source, next);
                continue;
            }

            if (state == ReadbackState.Lost)
            {
                if (problem is not null)
                {
                    _stats?.AddUnavailable();
                    NoteCaptureProblem(source, problem);
                }

                DropFrame(source, next);
                continue;
            }

            Publish(next, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        for (var i = 0; i < _scansInFlight.Count;)
        {
            var scan = _scansInFlight[i];
            var started = Stopwatch.GetTimestamp();
            string? problem = null;
            var state = ReadbackState.Lost;
            try
            {
                state = source.Poll(scan.Ticket);
                if (state == ReadbackState.Done)
                {
                    state = source.ReadInto(scan.Ticket, new[] { scan.Frame.ScanBuffer }, true, out problem);
                    if (state == ReadbackState.Done)
                        CheckBlankPicture(scan.Frame);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log("Live detection: the scan region could not be read (" + ex.GetType().Name + ": " + ex.Message + ")");
                state = ReadbackState.Lost;
            }

            if (state == ReadbackState.Pending && now - scan.PublishedAt <= _pipe.ScanReadWaitMs)
            {
                i++;
                continue;
            }

            _scansInFlight.RemoveAt(i);
            if (state == ReadbackState.Pending)
            {
                source.Abandon(scan.Ticket);
                _stats?.AddReadbackScanGivenUp();
            }
            else if (problem is not null)
                NoteCaptureProblem(source, problem);

            FinishScan(scan, state == ReadbackState.Done, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void Publish(FrameInFlight inFlight, double readMs)
    {
        var frame = inFlight.Frame;
        var publishedAt = _clock.NowMs;
        if (!inFlight.Scan.IsNone)
            frame.TryRetain();
        frame.MarkPublished(publishedAt);
        var sequence = _frames.Publish(frame);
        TracePublish(sequence, inFlight, publishedAt, readMs);
        _intervals.Add(inFlight.FrameMs);
        _queue.Submit(new IndexUpdate[] { new NewFrameUpdate(PipelineGroup.Capture, inFlight.FrameMs, inFlight.Epoch, inFlight.ArrivedAt, _intervals.MedianMs, inFlight.RequestIds) });
        if (!inFlight.Scan.IsNone)
            _scansInFlight.Add(new ScanInFlight(frame, inFlight.Scan, publishedAt, sequence));
    }

    private void FinishScan(ScanInFlight scan, bool ok, double readMs)
    {
        var afterMs = _clock.NowMs - scan.PublishedAt;
        if (Trace("cap", "scan_read") is { } readLine)
            Emit(readLine.Int("seq", scan.Sequence).Num("fms", scan.Frame.FrameMs).Bool("ok", ok).Num("ms", readMs).Num("afterMs", afterMs));
        scan.Frame.CompleteScan(ok);
        scan.Frame.Release();
        if (ok)
            _stats?.AddScanRead(afterMs);
    }

    private static void DropFrame(IFrameSource? source, FrameInFlight inFlight)
    {
        if (!inFlight.Scan.IsNone)
            source?.Abandon(inFlight.Scan);
        inFlight.Frame.CompleteScan(false);
        inFlight.Frame.Release();
    }

    private void DropCopies()
    {
        var source = _source;
        while (_framesInFlight.Count > 0)
        {
            var inFlight = _framesInFlight.Dequeue();
            source?.Abandon(inFlight.Patches);
            DropFrame(source, inFlight);
        }

        foreach (var scan in _scansInFlight)
        {
            source?.Abandon(scan.Ticket);
            scan.Frame.CompleteScan(false);
            scan.Frame.Release();
        }

        _scansInFlight.Clear();
    }

    private void WaitForPictureOrCopies(IFrameSource source)
    {
        var arrival = source.ArrivalHandle;
        var done = source.CompletionHandle;
        if (arrival is not null && done is not null)
        {
            if (_bothHandles[0] != arrival || _bothHandles[1] != done)
                _bothHandles = new[] { arrival, done };
            WaitHandle.WaitAny(_bothHandles, LiveSettings.CaptureAcquireTimeoutMs);
        }
        else
            _pollWait.Wait(LiveSettings.ReadbackPollMs, arrival, done);
    }

    private void NoteCaptureProblem(IFrameSource source, string? problem)
    {
        if (problem is null || problem == _lastCaptureProblem)
            return;
        _lastCaptureProblem = problem;
        Log("Live detection: " + source.Name + " unavailable (" + problem + ")");
        _services.Milestones.CaptureFailed(problem);
    }

    private void TracePublish(long sequence, FrameInFlight inFlight, double publishedAt, double readMs)
    {
        if (Trace("cap", "publish") is not { } line)
            return;
        line.Int("seq", sequence).Num("fms", inFlight.FrameMs).Num("arrive", inFlight.ArrivedAt).Num("copyMs", publishedAt - inFlight.ArrivedAt)
            .Num("issueMs", inFlight.IssueMs).Num("waitMs", Math.Max(0.0, publishedAt - inFlight.IssuedAt - inFlight.IssueMs - readMs)).Num("readMs", readMs)
            .Int("inFlight", _framesInFlight.Count).Bool("scanDeferred", !inFlight.Scan.IsNone).Array("regions");
        foreach (var (request, window) in inFlight.Requests)
            line.Item().Str("kind", request.Kind.ToString()).Str("by", request.Requester.ToString()).Int("mid", request.MarkerId).Int("tag", request.Tag)
                .Box("win", new PixelRect(window.X, window.Y, window.Width, window.Height)).Num("madeFrom", request.MadeFromFrameMs).Num("exp", request.ExpiresAtMs);
        Emit(line.EndArray());
    }
}
