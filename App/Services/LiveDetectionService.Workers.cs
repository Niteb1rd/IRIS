
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Iris.Core.Lookup;
using Iris.Core.Detection;
using Iris.Core.Mining;
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
                if (!_services.Milestones.PipelineDone)
                    NoteMilestones(snapshot);
                if (snapshot.Aberration == AberrationState.On && !_services.Milestones.AberrationSeen)
                    _services.Milestones.AberrationDetected();
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

    private void NoteMilestones(IndexSnapshot snapshot)
    {
        if (snapshot.Markers.Count == 0)
            return;
        _services.Milestones.PlateFound();
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status == PlateStatus.Confirmed)
            {
                _services.Milestones.NameConfirmed();
                return;
            }
        }
    }

    private void Notify(IndexSnapshot snapshot)
    {
        var at = _clock.NowMs;
        var model = IndexModel.ModelAt(snapshot, at, _pipe, _live, _lastShown, _services.Settings.TrackingLossDisplaySeconds * 1000.0);
        var stalled = at - _lastIndexPublishMs > IndexModel.FreshnessLimitMs(snapshot, _pipe);
        _lastIndexPublishMs = at;
        var last = _lastShown;
        _lastShown = model;
        if (!stalled && (model.Brackets.Count > 0) == _overlay.BracketsShown && ReferenceEquals(model.Panel, last.Panel) && model.Note == last.Note
            && Equals(model.Mining, last.Mining))
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
        var currentEnvironment = _environment;
        var currentReader = _reader;
        environment = currentEnvironment!;
        reader = currentReader!;
        return _running && currentEnvironment is not null && currentReader is not null;
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
                    worker = new TrackingWorker(_pipe, tracker, _queue, _clock, _stats)
                    {
                        Trace = _trace, KeepCrop = AppVersion.Diagnostics ? KeepTrackingCrop : null, KeepLoss = AppVersion.Diagnostics ? KeepLossGroup : null,
                    };
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
                    worker = new StoppedWorker(_pipe, tracker, _queue, _clock) { Trace = _trace };
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
                var snapshot = _index.Current;
                var early = worker.WantsEarlyRead(snapshot);
                if (early)
                    rate.TickNow(now);
                if (early || rate.TryTick(now, out _))
                    worker.Prepare(snapshot, env, now, config);
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
            worker?.DropAllKept();
            ops?.Dispose();
        }
    }

    private void CacheLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NameConfirmed | IndexTrigger.SettingsChanged);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken, _stop.Token);
        var worker = new CacheWorker(
            new RefresherNameSource(_services.Database, _services.Refresher, _services.Clock), _sessionNames, _queue, _live, DiagnosticLog, _services.Clock) { Trace = _trace };
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
        MiningSignalReader? miningReader = null;
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
                    miningReader ??= new MiningSignalReader(_mining);
                    worker ??= new ScanWorker(_pipe, ops, matcher, _queue, _clock, _stats, DiagnosticLog, _services.Clock)
                    {
                        Trace = _trace, Mining = miningReader, MiningTables = _miningTables, MiningLocked = OnMiningLocked,
                    };
                    worker.Ops = ops;
                    previous?.Dispose();
                }

                var now = _clock.NowMs;
                worker.MiningEnabled = MiningEnabled;
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
            miningReader?.Dispose();
        }
    }

    private bool MiningEnabled => !_services.IsOverlayDisabled && !_services.Settings.MiningDisabled;

    private void MiningLoop()
    {
        using var subscription = _index.Subscribe(IndexTrigger.NewFrame);
        var rateHz = _mining.LockHz;
        var rate = new RateClock(rateHz);
        MiningSignalReader? reader = null;
        MiningWorker? worker = null;
        try
        {
            RunWorker("mining", PipelineGroup.Mining, () =>
            {
                var env = _environment;
                if (!_running || env is null)
                {
                    rate.Reset();
                    subscription.Wait(LiveSettings.WorkerIdleWaitMs);
                    return;
                }

                reader ??= new MiningSignalReader(_mining);
                worker ??= new MiningWorker(_mining, _pipe, reader, _miningTables, _queue, _clock, _stats)
                {
                    Trace = _trace, Changed = OnMiningChanged, KeepCrop = AppVersion.Diagnostics ? KeepMiningCrop : null,
                    KeepTableCrop = AppVersion.Diagnostics ? KeepTableCrop : null, KeepLoss = AppVersion.Diagnostics ? KeepLossGroup : null,
                };
                var wantedHz = MiningLockHz(_index.Current);
                if (Math.Abs(wantedHz - rateHz) >= MiningRateStepHz)
                {
                    rateHz = wantedHz;
                    rate = new RateClock(rateHz);
                }

                var now = _clock.NowMs;
                if (rate.TryTick(now, out _))
                    worker.Prepare(_index.Current, env, now, MiningEnabled);
                if (worker.HasPending && _frames.TakeNewestWith(PipelineGroup.Mining) is { } frame)
                {
                    try
                    {
                        if (worker.Accepts(frame))
                            worker.Process(_index.Current, frame);
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
            reader?.Dispose();
        }
    }

    private double MiningLockHz(IndexSnapshot snapshot)
    {
        var cap = TrackingCapFps();
        var hz = cap > 0 ? cap : 1000.0 / snapshot.NominalFrameIntervalMs;
        return Math.Max(_mining.LockHz, hz);
    }

    private const double MiningRateStepHz = 5.0;

    private void OnMiningLocked(SignalRead read, string? label)
    {
        var text = read.Text.Length == 0 ? "?" : read.Text;
        _services.Milestones.MiningSignalFound(text);
        DiagnosticLog?.Write(_services.Clock.UtcNow, $"Mining: signal found: {text} ({read.State}) -> {label ?? "no label"}");
    }

    private void OnMiningChanged(SignalRead read, MiningState? state)
    {
        if (DiagnosticLog is not { } log)
            return;
        log.Write(_services.Clock.UtcNow, state is null
            ? "Mining: signal gone, lock ended"
            : $"Mining: label {(state.Label is null ? "cleared" : "\"" + state.Label + "\"")} (read {read.Text}, {read.State})");
    }

    private ReadsWorker StartReads()
    {
        var judge = new PlateNameJudge(
            _services.Exclusions, _sessionExclusions, LookupCachedOrgs, (sid, handle) => _services.Database.IsOrgMember(sid, handle), _live);
        return new ReadsWorker(_pipe, new NullImageOps(), judge, _sessionExclusions, _queue, _clock, _sessionNames, _stats, DiagnosticLog, _services.Clock)
        {
            Trace = _trace, KeepCrop = AppVersion.Diagnostics ? KeepReadCrop : null,
        };
    }

    private IReadOnlyList<OrgMembership>? LookupCachedOrgs(string handle) => _services.Database.GetPlayer(handle)?.Orgs;
}
