
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;
using Iris.Core.Lookup;
using Iris.Core.Detection;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.App.Services;

public sealed partial class LiveDetectionService
{
    private void CaptureLoop()
    {
        RegisterCpu(PipelineGroup.Capture);
        var running = false;
        var released = true;
        var idleSinceMs = 0.0;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _wake.Reset();
                var gate = _gate;
                var now = _clock.NowMs;

                if (!gate.Active)
                {
                    if (running)
                    {
                        running = false;
                        StopRun(now);
                        idleSinceMs = now;
                        released = false;
                    }
                    else if (!released && now - idleSinceMs >= _live.IdleReleaseMs)
                    {
                        _source?.Release();
                        released = true;
                    }

                    _wake.Wait(500);
                    continue;
                }

                if (!running)
                {
                    if (!EnsureReader(now))
                    {
                        _wake.Wait((int)Math.Clamp(_readerRetryAtMs - now, 50.0, 500.0));
                        continue;
                    }

                    StartRun(gate, now);
                    running = true;
                    released = false;
                }

                var pause = 0;
                try
                {
                    pause = Step(gate);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _source?.ReleaseHeldFrame();
                    var text = ex.GetType().Name + ": " + ex.Message;
                    if (text != _lastCaptureProblem)
                    {
                        _lastCaptureProblem = text;
                        Log("Live detection: a capture step failed (" + text + "); it is tried again at the next one");
                    }

                    pause = LiveSettings.WorkerErrorPauseMs;
                }

                if (pause > 0)
                    _wake.Wait(pause);

                var after = _clock.NowMs;
                if (_stats is { } stats && stats.IsDue(after, _live.SummaryEveryMs))
                {
                    Log(TakeSummary(stats, after));
                    LogLostRefusals();
                    LogRenderSummary();
                }
            }
        }
        catch (Exception ex)
        {
            Log("Live detection stopped because of an unexpected error: " + ex);
        }
        finally
        {
            _running = false;
            _environment = null;
            _frames.Clear();
            _source?.Dispose();
            _source = null;
        }
    }

    private bool EnsureReader(double now)
    {
        if (_reader is not null)
            return true;
        if (now < _readerRetryAtMs)
            return false;
        try
        {
            _reader = new NameplateReader(LetterModel.LoadEmbedded());
            _readerFailures = 0;
            _lastReaderProblem = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var retryMs = LiveSettings.EngineRetryBackoffMs[Math.Min(_readerFailures, LiveSettings.EngineRetryBackoffMs.Count - 1)];
            _readerFailures++;
            var problem = ex.GetType().Name + ": " + ex.Message;
            if (problem != _lastReaderProblem)
            {
                _lastReaderProblem = problem;
                Log("Live detection could not load the letter model (" + problem + "); trying again in " + retryMs / 1000 + " s, then less often while it keeps failing");
            }

            _readerRetryAtMs = now + retryMs;
            return false;
        }
    }

    private void StartRun(CaptureGate gate, double now)
    {
        _source ??= new WgcFrameSource { Log = Log };
        _frames.Clear();
        _queue.Submit(new IndexUpdate[] { new ResetUpdate(PipelineGroup.Capture, now, _index.Current.Epoch) });
        CollectCpu();
        _stats?.Restart(now);
        _lateFrames.Restart(now);
        _lateNote.Reset();
        _counters.Take();
        _schedule.Reset();
        _intervals.Reset();
        _lastFrameMs = now;
        _lastCaptureProblem = null;
        _tooSmallLogged = false;
        _environment = null;
        _running = true;
        var bounds = gate.Bounds;
        var cap = TrackingCapFps();
        if (!AppVersion.Diagnostics)
        {
            Log("Live detection started");
            return;
        }
        Log(string.Format(
            CultureInfo.InvariantCulture,
            "Live detection started (game window {0:0}x{1:0}; full scan {2:0.#} times a second, plate reads {3:0.#} times a second with at most {4} per tick, markers tracked on every new frame of the game{5}, brackets drawn at monitor refresh)",
            bounds.Width, bounds.Height, _pipe.ScanHz, _pipe.PlateHz, _pipe.MaxReadsPerTick, cap > 0 ? $", at most {cap} times a second" : " with no cap (Max)"));
    }

    private void StopRun(double now)
    {
        _running = false;
        _source?.ReleaseHeldFrame();
        if (_stats is { } stats)
        {
            Log(TakeSummary(stats, now));
            LogLostRefusals();
            LogRenderSummary();
        }
        _environment = null;
        _frames.Clear();
        _lateNote.Reset();
        _queue.Submit(new IndexUpdate[] { new ResetUpdate(PipelineGroup.Capture, now, _index.Current.Epoch) });
        Log(AppVersion.Diagnostics ? "Live detection stopped (the game is not in front and borderless, or the overlay is disabled)" : "Live detection stopped");
    }

    private int Step(CaptureGate gate)
    {
        var bounds = gate.Bounds;
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        if (!EnsureEnvironment(width, height))
        {
            UpdateLateNote(_clock.NowMs, false);
            return LiveSettings.TooSmallPauseMs;
        }

        var source = _source!;
        source.HideBorder = _services.Settings.HideCaptureBorder;
        var arrival = source.AcquireNextFrame(gate.Handle);
        var arrivedAt = _clock.NowMs;
        switch (arrival.Status)
        {
            case FrameWait.NewFrame:
            {
                var frameMs = Math.Max(arrival.FrameMs, _lastFrameMs);
                _lastFrameMs = frameMs;
                _stats?.AddGameFrame(frameMs);
                _stats?.AddCaptureDelay(Math.Max(0.0, arrivedAt - frameMs));
                if (arrivedAt - frameMs > LiveSettings.LateFrameThresholdMs)
                    _stats?.AddLateFrame(arrivedAt - frameMs);
                if (_lateFrames.OnFrame(frameMs, arrivedAt) is { } lateLine)
                    Log(lateLine);
                UpdateLateNote(arrivedAt, LateFrameWindow.IsLate(arrivedAt - frameMs));
                if (!_schedule.Offer(frameMs, TrackingCapFps()))
                {
                    source.ReleaseHeldFrame();
                    return 0;
                }

                PublishFrame(frameMs, arrivedAt, width, height);
                return 0;
            }

            case FrameWait.Unchanged:
                UpdateLateNote(arrivedAt, false);
                return 0;

            default:
                UpdateLateNote(arrivedAt, false);
                _stats?.AddUnavailable();
                if (arrival.Message is { } message && message != _lastCaptureProblem)
                {
                    _lastCaptureProblem = message;
                    Log("Live detection: window capture unavailable (" + message + ")");
                }

                return LiveSettings.CaptureUnavailablePauseMs;
        }
    }

    private void UpdateLateNote(double now, bool late)
    {
        if (_lateNote.Update(now, late))
            _queue.Submit(new IndexUpdate[] { new LateNoteUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, _lateNote.Active) });
    }

    private bool EnsureEnvironment(int width, int height)
    {
        var boundary = TrackingBoundary.From(_services.Settings);
        var current = _environment;
        if (current is not null && current.Geometry.Matches(width, height, boundary))
            return true;
        if (!ScanGeometry.TryCreate(width, height, NameplateDetectorSettings.Default, boundary, out var created))
        {
            if (!_tooSmallLogged)
            {
                _tooSmallLogged = true;
                Log($"Live detection: the game window ({width}x{height}) is too small to scan");
            }

            return false;
        }

        var now = _clock.NowMs;
        var updates = new List<IndexUpdate>(2);
        if (current is not null && !current.Geometry.Matches(width, height))
        {
            updates.Add(new ResetUpdate(PipelineGroup.Capture, now, _index.Current.Epoch));
            if (_lateNote.Active)
                updates.Add(new LateNoteUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, true));
        }

        updates.Add(new SettingsUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, boundary, width, height));
        _queue.Submit(updates);
        _environment = new PipelineEnvironment(created, boundary);
        _tooSmallLogged = false;
        return true;
    }

    private void PublishFrame(double frameMs, double arrivedAt, int width, int height)
    {
        var source = _source!;
        var snapshot = _index.Current;
        var frame = _frames.Rent();
        if (frame is null)
        {
            source.ReleaseHeldFrame();
            return;
        }

        CapturedFrame? lateScan = null;
        try
        {
            frame.Begin(frameMs, arrivedAt, width, height);
            var chosen = CaptureRule.SelectUnseen(snapshot.Requests, frameMs, _newestSeenRequestId);
            var requests = new List<(CaptureRequest Request, Rect Window)>(chosen.Count);
            foreach (var request in chosen.OrderBy(r => r.Kind == RequestKind.ScanArea ? 0 : 1))
            {
                var rect = RegionPlanner.ToWindowRect(request.Region, width, height);
                if (rect.Width > 0 && rect.Height > 0)
                    requests.Add((request, rect));
            }

            var firstIsScan = requests.Count > 0 && requests[0].Request.Kind == RequestKind.ScanArea;
            var windowRegions = new List<Rect>(requests.Count);
            var buffers = new List<Mat>(requests.Count);
            var patch = 0;
            foreach (var (request, window) in requests)
            {
                windowRegions.Add(window);
                buffers.Add(request.Kind == RequestKind.ScanArea ? frame.ScanBuffer : frame.PatchBuffer(patch++));
            }

            var grab = source.CopyHeldFrameInto(windowRegions, firstIsScan, buffers, deferScan: true);
            if (grab.Status == GrabStatus.Unavailable)
            {
                _stats?.AddUnavailable();
                if (grab.Message is { } message && message != _lastCaptureProblem)
                {
                    _lastCaptureProblem = message;
                    Log("Live detection: window capture unavailable (" + message + ")");
                }

                return;
            }

            _lastCaptureProblem = null;
            for (var i = 0; i < requests.Count; i++)
                frame.Add(requests[i].Request, requests[i].Window, buffers[i]);
            var requestIds = frame.RequestIds.ToArray();
            if (firstIsScan)
            {
                frame.MarkScanPending();
                frame.TryRetain();
                lateScan = frame;
            }

            frame.MarkPublished(_clock.NowMs);
            _frames.Publish(frame);
            frame = null;
            _newestSeenRequestId = Math.Max(_newestSeenRequestId, CaptureRule.NewestId(snapshot.Requests));
            _intervals.Add(frameMs);
            _queue.Submit(new IndexUpdate[] { new NewFrameUpdate(PipelineGroup.Capture, frameMs, snapshot.Epoch, arrivedAt, _intervals.MedianMs, requestIds) });
            if (lateScan is not null)
            {
                var done = lateScan;
                lateScan = null;
                FinishScanRead(source, done);
            }
        }
        finally
        {
            frame?.Release();
            if (lateScan is not null)
            {
                lateScan.CompleteScan(false);
                lateScan.Release();
            }
        }
    }

    private void FinishScanRead(WgcFrameSource source, CapturedFrame frame)
    {
        var ok = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ok = source.ReadPendingScan(out var problem);
            if (!ok && problem is not null && problem != _lastCaptureProblem)
            {
                _lastCaptureProblem = problem;
                Log("Live detection: window capture unavailable (" + problem + ")");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("Live detection: the scan region could not be read (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
        finally
        {
            frame.CompleteScan(ok);
            frame.Release();
        }

        if (ok)
            _stats?.AddScanRead((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }
}
