
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Config;
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
            DropCopies();
            _frames.Clear();
            _source?.Dispose();
            _pollWait.Dispose();
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
        EnsureSource();
        _frames.Clear();
        _queue.Submit(new IndexUpdate[] { new ResetUpdate(PipelineGroup.Capture, now, _index.Current.Epoch) });
        CollectCpu();
        _stats?.Restart(now);
        _lateFrames.Restart(now);
        _lateNote.Reset();
        _blankNote.Reset();
        _blankLogged = false;
        _counters.Take();
        _intervals.Reset();
        _lastFrameMs = now;
        _lastCaptureProblem = null;
        _tooSmallLogged = false;
        _environment = null;
        _runStartMs = now;
        _anyFrameThisRun = false;
        _running = true;
        var bounds = gate.Bounds;
        _notedCpuUsage = _services.Settings.CpuUsage;
        TraceRunStart(bounds);
        if (!AppVersion.Diagnostics)
        {
            Log("Live detection started");
            return;
        }
        Log(string.Format(
            CultureInfo.InvariantCulture,
            "Live detection started (game window {0:0}x{1:0}; CPU Usage {2}: {3}, at most {4} plate reads per tick, markers tracked on every new frame of the game, brackets drawn at monitor refresh)",
            bounds.Width, bounds.Height, AppSettingsToml.CpuUsageText(_services.Settings.CpuUsage), PipelineRates.Of(Rated(_services.Settings.CpuUsage)).Describe(), _pipe.MaxReadsPerTick));
    }

    private void StopRun(double now)
    {
        _running = false;
        _source?.ReleaseHeldFrame();
        DropCopies();
        if (_stats is { } stats)
        {
            Log(TakeSummary(stats, now));
            LogLostRefusals();
            LogRenderSummary();
        }
        _environment = null;
        _frames.Clear();
        _lateNote.Reset();
        _blankNote.Reset();
        if (Trace("cap", "run_stop") is { } stopLine)
            Emit(stopLine.Bool("overlayDisabled", _services.IsOverlayDisabled));
        if (_services.IsOverlayDisabled)
            _trace?.Close();
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
            if (_source is { } waiting)
                CollectReadbacks(waiting);
            UpdateLateNote(_clock.NowMs, false);
            return LiveSettings.TooSmallPauseMs;
        }

        NoteCpuUsage();
        var source = EnsureSource();
        if (source is WgcFrameSource wgc)
        {
            wgc.HideBorder = _services.Settings.HideCaptureBorder;
            wgc.UseCopyQueue = _services.Settings.CaptureCopyQueue;
        }
        CollectReadbacks(source);
        var busy = CopiesOnTheirWay;
        var arrival = source.AcquireNextFrame(gate.Handle, busy ? 0 : LiveSettings.CaptureAcquireTimeoutMs);
        var arrivedAt = _clock.NowMs;
        switch (arrival.Status)
        {
            case FrameWait.NewFrame:
            {
                _anyFrameThisRun = true;
                var frameMs = Math.Max(arrival.FrameMs, _lastFrameMs);
                _lastFrameMs = frameMs;
                _stats?.AddGameFrame(frameMs);
                _stats?.AddCaptureDelay(Math.Max(0.0, arrivedAt - frameMs));
                if (arrivedAt - frameMs > LiveSettings.LateFrameThresholdMs)
                    _stats?.AddLateFrame(arrivedAt - frameMs);
                if (_lateFrames.OnFrame(frameMs, arrivedAt) is { } lateLine)
                    Log(lateLine);
                UpdateLateNote(arrivedAt, LateFrameWindow.IsLate(arrivedAt - frameMs));
                if (Trace("cap", "frame") is { } frameLine)
                {
                    frameLine.Num("fms", frameMs).Num("raw", arrival.FrameMs).Num("delay", arrivedAt - frameMs).Bool("taken", true);
                    if (source is DuplicationFrameSource duplication)
                        frameLine.Num("share", duplication.LastUpdatedShare, 3).Int("skipped", duplication.TakeSkippedUpdates());
                    Emit(frameLine);
                }
                IssueFrame(frameMs, arrivedAt, width, height);
                return 0;
            }

            case FrameWait.Unchanged:
                UpdateLateNote(arrivedAt, false);
                NoteNoFrameYet(arrivedAt);
                if (busy)
                    WaitForPictureOrCopies(source);
                return 0;

            default:
                UpdateLateNote(arrivedAt, false);
                _stats?.AddUnavailable();
                if (Trace("cap", "unavailable") is { } unavailableLine)
                    Emit(unavailableLine.Str("why", arrival.Message));
                if (arrival.Message is { } message && message != _lastCaptureProblem)
                {
                    _lastCaptureProblem = message;
                    Log("Live detection: " + source.Name + " unavailable (" + message + ")");
                    _services.Milestones.CaptureFailed(message);
                }
                NoteNoFrameYet(arrivedAt);

                return LiveSettings.CaptureUnavailablePauseMs;
        }
    }

    private IFrameSource EnsureSource()
    {
        var method = _services.Settings.CaptureMethod;
        var wanted = method == CaptureMethod.WindowCapture;
        if (_source is not null && (_source is WgcFrameSource) == wanted)
            return _source;
        DropCopies();
        _source?.Dispose();
        _source = wanted ? new WgcFrameSource { Log = Log } : new DuplicationFrameSource { Log = Log };
        _lastCaptureProblem = null;
        if (_stats is { } stats)
            stats.CaptureMethodText = wanted ? "window (WGC)" : "desktop duplication";
        Log("Live detection: capturing with " + _source.Name);
        return _source;
    }

    private void NoteNoFrameYet(double now)
    {
        if (!_anyFrameThisRun && now - _runStartMs > LiveSettings.FirstFrameTimeoutMs)
            _services.Milestones.CaptureFailed($"no picture from the game window within {LiveSettings.FirstFrameTimeoutMs / 1000} s");
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
            DropCopies();
            updates.Add(new ResetUpdate(PipelineGroup.Capture, now, _index.Current.Epoch));
            if (_lateNote.Active)
                updates.Add(new LateNoteUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, true));
            if (_blankNote.Active)
                updates.Add(new BlankNoteUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, true));
        }

        updates.Add(new SettingsUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, boundary, width, height));
        _queue.Submit(updates);
        _environment = new PipelineEnvironment(created, boundary);
        _tooSmallLogged = false;
        return true;
    }

    private TraceLine RatesLine(TraceLine line, CpuUsage usage)
    {
        var rates = PipelineRates.Of(Rated(usage));
        return line.Str("cpuUsage", AppSettingsToml.CpuUsageText(usage))
            .Num("scanHz", rates.ScanHz).Num("plateHz", rates.PlateHz).Num("confirmedReadHz", rates.ConfirmedReadHz).Num("disputedReadHz", rates.DisputedReadHz)
            .Num("stoppedHz", rates.StoppedTrackHz).Int("lostSearchEveryFrames", rates.LostSearchEveryFrames).Num("confirmSpanMs", rates.ConfirmSpanMs);
    }

    private void NoteCpuUsage()
    {
        var usage = _services.Settings.CpuUsage;
        if (usage == _notedCpuUsage)
            return;
        _notedCpuUsage = usage;
        if (!_running)
            return;
        if (Trace("cap", "rates") is { } line)
            Emit(RatesLine(line, usage));
        if (AppVersion.Diagnostics)
            Log("Live detection: CPU Usage " + AppSettingsToml.CpuUsageText(usage) + ": " + PipelineRates.Of(Rated(usage)).Describe());
    }

    private void TraceRunStart(PixelRect bounds)
    {
        if (_trace is null)
            return;
        if (!_trace.IsOpen)
        {
            var header = RatesLine(TraceLine.Begin(_clock.NowMs, "cap", "session"), _services.Settings.CpuUsage)
                .Str("wall", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Str("version", AppVersion.Text)
                .Box("window", bounds)
                .Str("capture", AppSettingsToml.CaptureMethodText(_services.Settings.CaptureMethod))
                .Num("lostAfterMs", _pipe.LostAfterMs).Num("maxLookAheadMs", _pipe.MaxLookAheadMs).Num("missCost", _pipe.MissCost)
                .Text();
            _trace.Open(DateTime.Now, header);
        }

        if (Trace("cap", "run_start") is { } line)
            Emit(RatesLine(line.Box("window", bounds), _services.Settings.CpuUsage).Str("capture", AppSettingsToml.CaptureMethodText(_services.Settings.CaptureMethod)).Str("wall", DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)));
    }

    private void CheckBlankPicture(CapturedFrame frame)
    {
        var now = _clock.NowMs;
        var first = !_services.Milestones.FrameSeen;
        if (!first && !_blankNote.Due(now))
            return;
        var brightest = BrightestColourValue(frame.ScanBuffer);
        if (first)
            _services.Milestones.FrameReceived(frame.WindowWidth, frame.WindowHeight, brightest);
        if (_blankNote.Update(now, brightest))
        {
            _queue.Submit(new IndexUpdate[] { new BlankNoteUpdate(PipelineGroup.Capture, now, _index.Current.Epoch, _blankNote.Active) });
            if (_blankNote.Active && !_blankLogged)
            {
                _blankLogged = true;
                Log("Live detection: the captured picture has been black for " + (LiveSettings.BlankNoteAfterMs / 1000) + " s; no names can be read");
            }
        }
    }

    private static double BrightestColourValue(Mat image)
    {
        var max = 0.0;
        using var channel = new Mat();
        for (var i = 0; i < Math.Min(3, image.Channels()); i++)
        {
            Cv2.ExtractChannel(image, channel, i);
            Cv2.MinMaxLoc(channel, out double _, out double value);
            max = Math.Max(max, value);
        }
        return max;
    }

}
