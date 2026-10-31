
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
    private void IndexLoop()
    {
        RegisterCpu(PipelineGroup.Index);
        string? lastProblem = null;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _queue.Wait(LiveSettings.WorkerIdleWaitMs);
                var snapshot = _queue.Pump();
                if (snapshot is null)
                    continue;
                foreach (var text in _counters.TakeTexts())
                    Log("Live detection: an update was rejected as older than the stored value: " + text);
                Notify(snapshot);
                lastProblem = null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var text = ex.GetType().Name + ": " + ex.Message;
                if (text != lastProblem)
                {
                    lastProblem = text;
                    Log($"Live detection: the index thread failed ({text}); it is tried again at the next update");
                }
            }
        }
    }

    private void Notify(IndexSnapshot snapshot)
    {
        var at = _clock.NowMs;
        var model = IndexModel.ModelAt(snapshot, at, _pipe, _live, _lastShown);
        var stalled = at - _lastIndexPublishMs > IndexModel.FreshnessLimitMs(snapshot, _pipe);
        _lastIndexPublishMs = at;
        var last = _lastShown;
        _lastShown = model;
        if (!stalled && (model.Brackets.Count > 0) == _overlay.BracketsShown && ReferenceEquals(model.Panel, last.Panel) && model.Note == last.Note)
            return;
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DeliverChange));
    }

    private void DeliverChange()
    {
        Interlocked.Exchange(ref _notifyPending, 0);
        if (!_disposed)
            _overlay.LiveChanged();
    }

    private void RunWorker(string name, PipelineGroup group, Action step)
    {
        RegisterCpu(group);
        string? lastProblem = null;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                step();
                lastProblem = null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var text = ex.GetType().Name + ": " + ex.Message;
                if (text != lastProblem)
                {
                    lastProblem = text;
                    Log($"Live detection: the {name} worker failed ({text}); it is tried again at the next step");
                }

                _stop.Token.WaitHandle.WaitOne(LiveSettings.WorkerErrorPauseMs);
            }
        }
    }

    private bool Ready(out PipelineEnvironment environment, out NameplateReader reader)
    {
        environment = _environment!;
        reader = _reader!;
        return _running && _environment is not null && _reader is not null;
    }

    private void TrackingLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NewFrame);
        NameplateDetector? finder = null;
        MarkerTracker? tracker = null;
        TrackingWorker? worker = null;
        var height = 0;
        long lastSequence = 0;
        try
        {
            RunWorker("tracking", PipelineGroup.Tracking, () =>
            {
                subscription.Wait(LiveSettings.WorkerIdleWaitMs);
                if (!Ready(out var env, out var reader))
                    return;
                if (worker is null || height != env.WindowHeight)
                {
                    finder?.Dispose();
                    var settings = NameplateDetectorSettings.Default with { RegionLeft = 0.0, RegionRight = 1.0, RegionTop = 0.0, RegionBottom = 1.0, ScaleHeight = env.WindowHeight };
                    finder = new NameplateDetector(reader, settings) { ContrastSink = _stats };
                    tracker = new MarkerTracker(_pipe, finder, env.WindowHeight);
                    worker = new TrackingWorker(_pipe, tracker, _queue, _clock, _stats);
                    height = env.WindowHeight;
                }

                var frame = _frames.TakeNewest();
                if (frame is null)
                    return;
                try
                {
                    if (frame.Sequence == lastSequence)
                        return;
                    lastSequence = frame.Sequence;
                    worker.Process(_index.Current, frame, env);
                }
                finally
                {
                    frame.Release();
                }
            });
        }
        finally
        {
            finder?.Dispose();
        }
    }

    private void StoppedLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NewFrame);
        NameplateDetector? finder = null;
        StoppedWorker? worker = null;
        var height = 0;
        long lastSequence = 0;
        try
        {
            RunWorker("stopped", PipelineGroup.Stopped, () =>
            {
                subscription.Wait(LiveSettings.WorkerIdleWaitMs);
                if (!Ready(out var env, out var reader))
                    return;
                if (worker is null || height != env.WindowHeight)
                {
                    finder?.Dispose();
                    var settings = NameplateDetectorSettings.Default with { RegionLeft = 0.0, RegionRight = 1.0, RegionTop = 0.0, RegionBottom = 1.0, ScaleHeight = env.WindowHeight };
                    finder = new NameplateDetector(reader, settings) { ContrastSink = _stats };
                    var tracker = new MarkerTracker(_pipe, finder, env.WindowHeight);
                    worker = new StoppedWorker(_pipe, tracker, _queue, _clock);
                    height = env.WindowHeight;
                }

                var frame = _frames.TakeNewest();
                if (frame is null)
                    return;
                try
                {
                    if (frame.Sequence == lastSequence)
                        return;
                    lastSequence = frame.Sequence;
                    if (worker.Offer(frame.FrameMs))
                        worker.Process(_index.Current, frame, env);
                }
                finally
                {
                    frame.Release();
                }
            });
        }
        finally
        {
            finder?.Dispose();
        }
    }

    private static int WaitMs(RateClock clock, double now) =>
        double.IsNegativeInfinity(clock.NextDueMs) ? LiveSettings.WorkerIdleWaitMs : (int)Math.Clamp(Math.Ceiling(clock.NextDueMs - now), 1.0, LiveSettings.WorkerMaxWaitMs);

    private void ReadsLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NewFrame);
        var rate = new RateClock(_pipe.PlateHz);
        ReadsWorker? worker = null;
        DetectorImageOps? ops = null;
        var engineRetryAtMs = 0.0;
        var engineFailures = 0;
        string? lastEngineProblem = null;
        try
        {
            RunWorker("reads", PipelineGroup.Reads, () =>
            {
                if (!Ready(out var env, out var reader))
                {
                    rate.Reset();
                    subscription.Wait(LiveSettings.WorkerIdleWaitMs);
                    return;
                }

                if (worker is null)
                {
                    if (_clock.NowMs < engineRetryAtMs)
                    {
                        subscription.Wait(LiveSettings.WorkerIdleWaitMs);
                        return;
                    }

                    try
                    {
                        worker = StartReads();
                        engineFailures = 0;
                        lastEngineProblem = null;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        var retryMs = LiveSettings.EngineRetryBackoffMs[Math.Min(engineFailures, LiveSettings.EngineRetryBackoffMs.Count - 1)];
                        engineFailures++;
                        var problem = ex.GetType().Name + ": " + ex.Message;
                        if (problem != lastEngineProblem)
                        {
                            lastEngineProblem = problem;
                            Log("Live detection could not start (" + problem + "); trying again in " + retryMs / 1000 + " s, then less often while it keeps failing");
                        }

                        engineRetryAtMs = _clock.NowMs + retryMs;
                        return;
                    }
                }

                if (ops is null || !ops.Geometry.Matches(env.WindowWidth, env.WindowHeight, env.Boundary))
                {
                    var previous = ops;
                    ops = new DetectorImageOps(reader, env.Geometry, NameplateDetectorSettings.Default, _stats);
                    worker.Ops = ops;
                    previous?.Dispose();
                }

                var now = _clock.NowMs;
                var config = _services.CurrentConfig;
                if (rate.TryTick(now, out _))
                    worker.Prepare(_index.Current, env, now, config);
                if (worker.HasPending && _frames.TakeNewestWith(PipelineGroup.Reads) is { } frame)
                {
                    try
                    {
                        if (worker.Accepts(frame))
                            worker.Process(_index.Current, frame, env, config);
                    }
                    finally
                    {
                        frame.Release();
                    }
                }

                subscription.Wait(WaitMs(rate, _clock.NowMs));
            });
        }
        finally
        {
            ops?.Dispose();
        }
    }

    private void CacheLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NameConfirmed | IndexTrigger.SettingsChanged);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken, _stop.Token);
        var worker = new CacheWorker(
            new RefresherNameSource(_services.Database, _services.Refresher, _services.Clock), _sessionNames, _queue, _live, DiagnosticLog, _services.Clock);
        try
        {
            var handles = new[] { subscription.Handle, worker.Signal, _stop.Token.WaitHandle };
            RunWorker("cache", PipelineGroup.Cache, () =>
            {
                if (_running)
                    worker.Step(_index.Current, _services.CurrentConfig, _clock.NowMs, cts.Token);
                WaitHandle.WaitAny(handles, LiveSettings.WorkerIdleWaitMs);
            });
        }
        finally
        {
            try
            {
                cts.Cancel();
                worker.Running?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
            {
            }

            worker.Dispose();
            cts.Dispose();
        }
    }

    private void ScanLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NewFrame);
        var rate = new RateClock(_pipe.ScanHz);
        ScanWorker? worker = null;
        DetectorImageOps? ops = null;
        TemplateMatcher? matcher = null;
        try
        {
            RunWorker("scan", PipelineGroup.Scan, () =>
            {
                if (!Ready(out var env, out var reader))
                {
                    rate.Reset();
                    subscription.Wait(LiveSettings.WorkerIdleWaitMs);
                    return;
                }

                if (worker is null || ops is null || !ops.Geometry.Matches(env.WindowWidth, env.WindowHeight, env.Boundary))
                {
                    var previous = ops;
                    ops = new DetectorImageOps(reader, env.Geometry, NameplateDetectorSettings.Default, _stats);
                    matcher ??= new TemplateMatcher();
                    worker ??= new ScanWorker(_pipe, ops, matcher, _queue, _clock, _stats, DiagnosticLog, _services.Clock);
                    worker.Ops = ops;
                    previous?.Dispose();
                }

                var now = _clock.NowMs;
                if (rate.TryTick(now, out _))
                    worker.Prepare(_index.Current, env, now);
                if (worker.HasPending && _frames.TakeNewestWith(PipelineGroup.Scan) is { } frame)
                {
                    try
                    {
                        if (worker.Accepts(frame))
                            worker.Process(_index.Current, frame, env);
                    }
                    finally
                    {
                        frame.Release();
                    }
                }

                subscription.Wait(WaitMs(rate, _clock.NowMs));
            });
        }
        finally
        {
            worker?.Dispose();
            ops?.Dispose();
            matcher?.Dispose();
        }
    }

    private ReadsWorker StartReads()
    {
        var judge = new PlateNameJudge(
            _services.Exclusions, _sessionExclusions, LookupCachedOrgs, (sid, handle) => _services.Database.IsOrgMember(sid, handle), _live);
        return new ReadsWorker(_pipe, new NullImageOps(), judge, _sessionExclusions, _queue, _clock, _sessionNames, _stats, DiagnosticLog, _services.Clock);
    }

    private IReadOnlyList<OrgMembership>? LookupCachedOrgs(string handle) => _services.Database.GetPlayer(handle)?.Orgs;
}
