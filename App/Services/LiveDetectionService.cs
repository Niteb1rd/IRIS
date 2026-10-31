using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Lookup;
using Iris.Core.Detection;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.App.Services;

public sealed partial class LiveDetectionService : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayController _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly IMonotonicClock _clock = StopwatchClock.Shared;
    private readonly LiveSettings _live = LiveSettings.Default;

    private readonly SessionExclusions _sessionExclusions = new();
    private readonly SessionNames _sessionNames = new();
    private readonly PipelineSettings _pipe = PipelineSettings.Default;
    private readonly PipelineStats? _stats = AppVersion.Diagnostics ? new(0) : null;
    private readonly IndexCounters _counters = new(AppVersion.Diagnostics);
    private readonly MarkerIndex _index;
    private readonly IndexUpdateQueue _queue;
    private readonly FrameHub _frames = new(LiveSettings.FramePoolSize);
    private readonly FrameIntervals _intervals = new();
    private readonly TrackingSchedule _schedule = new();
    private readonly ThreadCpuMeter?[] _cpu = new ThreadCpuMeter?[PipelineStats.GroupCount];
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _captureThread;
    private readonly Thread _indexThread;
    private readonly Thread _trackingThread;
    private readonly Thread _stoppedThread;
    private readonly Thread _readsThread;
    private readonly Thread _scanThread;
    private readonly Thread _cacheThread;
    private volatile NameplateReader? _reader;
    private volatile PipelineEnvironment? _environment;
    private volatile bool _running;

    private volatile CaptureGate _gate;
    private int _notifyPending;
    private bool _started;
    private bool _disposed;

    private WgcFrameSource? _source;
    private readonly LateFrameMonitor _lateFrames = new(0);
    private readonly LateFrameWindow _lateNote = new();
    private double _lastFrameMs;
    private long _newestSeenRequestId;
    private string? _lastCaptureProblem;
    private bool _tooSmallLogged;
    private int _readerFailures;
    private double _readerRetryAtMs;
    private string? _lastReaderProblem;

    private OverlayModel _lastShown = OverlayModel.Empty;
    private double _lastIndexPublishMs;

    public LiveDetectionService(AppServices services, OverlayController overlay)
    {
        _services = services;
        _overlay = overlay;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _gate = overlay.Gate;
        _index = new MarkerIndex(_pipe, _counters);
        _queue = new IndexUpdateQueue(_index, _clock);
        overlay.AttachLive(_index, _pipe, _live);
        overlay.GateChanged += OnGateChanged;
        _captureThread = new Thread(CaptureLoop) { Name = "IRIS capture", IsBackground = true };
        _indexThread = new Thread(IndexLoop) { Name = "IRIS index", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _trackingThread = new Thread(TrackingLoop) { Name = "IRIS tracking", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _stoppedThread = new Thread(StoppedLoop) { Name = "IRIS stopped", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _readsThread = new Thread(ReadsLoop) { Name = "IRIS reads", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _scanThread = new Thread(ScanLoop) { Name = "IRIS scan", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _cacheThread = new Thread(CacheLoop) { Name = "IRIS cache", IsBackground = true, Priority = ThreadPriority.BelowNormal };
    }

    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _indexThread.Start();
        _trackingThread.Start();
        _stoppedThread.Start();
        _readsThread.Start();
        _scanThread.Start();
        _cacheThread.Start();
        _captureThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _overlay.GateChanged -= OnGateChanged;
        _stop.Cancel();
        _wake.Set();
        _queue.Wake();
        var captureEnded = JoinThread(_captureThread, "capture", 2);
        var trackingEnded = JoinThread(_trackingThread, "tracking", 3);
        var stoppedEnded = JoinThread(_stoppedThread, "stopped", 3);
        var readsEnded = JoinThread(_readsThread, "reads", 3);
        var scanEnded = JoinThread(_scanThread, "scan", 3);
        var cacheEnded = JoinThread(_cacheThread, "cache", 5);
        var indexEnded = JoinThread(_indexThread, "index", 3);

        if (!trackingEnded || !stoppedEnded || !readsEnded || !scanEnded || !cacheEnded || !indexEnded)
            return;

        if (captureEnded)
        {
            for (var i = 0; i < _cpu.Length; i++)
                _cpu[i]?.Dispose();
            _frames.Dispose();
            _queue.Dispose();
            _wake.Dispose();
            _stop.Dispose();
        }
    }

    private bool JoinThread(Thread thread, string name, int seconds)
    {
        var ended = !thread.IsAlive || thread.Join(TimeSpan.FromSeconds(seconds));
        if (!ended)
            Log($"Live detection: the {name} thread did not end within {seconds} s of shutdown; its wait handles are left alone (it is a background thread and ends with the process)");
        return ended;
    }

    private void OnGateChanged(object? sender, EventArgs e)
    {
        _gate = _overlay.Gate;
        _wake.Set();
    }

    private void RegisterCpu(PipelineGroup group)
    {
        if (AppVersion.Diagnostics)
            Volatile.Write(ref _cpu[(int)group], ThreadCpuMeter.ForCurrentThread());
    }

    private void CollectCpu()
    {
        if (_stats is null)
            return;
        for (var i = 0; i < _cpu.Length; i++)
        {
            var meter = Volatile.Read(ref _cpu[i]);
            if (meter is not null)
                _stats.AddBusy((PipelineGroup)i, meter.TakeDeltaMs());
        }

        _stats.AddBusy(PipelineGroup.Render, _overlay.TakeRenderCpuMs());
    }

    private void LogLostRefusals()
    {
        if (_stats?.TakeLostRefusalText() is { } text)
            Log(text);
    }

    private string TakeSummary(PipelineStats stats, double now)
    {
        CollectCpu();
        var markers = _index.Current.Markers;
        return stats.TakeSummary(now, markers.Count, markers.Count(m => m.Status == PlateStatus.Confirmed), _pipe.MaxReadsPerTick, TrackingCapFps(), _counters.Take());
    }

    private int TrackingCapFps() => _services.Settings.TrackingCapFps;

    private void LogRenderSummary()
    {
        if (_disposed)
            return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed)
                _overlay.LogRenderSummary();
        }));
    }

    private void Log(string text) => _services.Log.Write(_services.Clock.UtcNow, text);

    private ILog? DiagnosticLog => AppVersion.Diagnostics ? _services.Log : null;
}
