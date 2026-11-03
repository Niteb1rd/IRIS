using Iris.Core.Config;
using Iris.Core.Detection;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class SyncPipeline : IUpdateSink, IDisposable
{
    private readonly RateClock _readClock;
    private readonly RateClock _scanClock;
    private readonly FrameIntervals _intervals = new();
    private readonly PipelineStats? _stats;
    private double _lastPublishedAtMs = double.NegativeInfinity;
    private bool _started;

    public SyncPipeline(
        PipelineSettings settings, PipelineEnvironment environment, ILineTracker tracker, ILineTracker stoppedTracker, IPlateImageOps ops, ITemplateMatcher? matcher,
        PlateNameJudge judge, SessionExclusions session, SocialConfig config, PipelineStats? stats = null, ILog? log = null, IClock? wallClock = null, INameResolver? resolver = null,
        IndexCounters? counters = null, INameSource? names = null, SessionNames? sessionNames = null)
    {
        _stats = stats;
        Environment = environment;
        Config = config;
        Clock = new ManualMonotonicClock();
        Index = new MarkerIndex(settings, counters ?? new IndexCounters());
        Hub = new FrameHub();
        Tracking = new TrackingWorker(settings, tracker, this, Clock, stats);
        Stopped = new StoppedWorker(settings, stoppedTracker, this, Clock);
        Scan = new ScanWorker(settings, ops, matcher, this, Clock, stats, log, wallClock);
        var known = sessionNames ?? new SessionNames();
        Reads = new ReadsWorker(settings, ops, judge, session, this, Clock, known, stats, log, wallClock, resolver);
        Cache = new CacheWorker(names, known, this, LiveSettings.Default, log, wallClock);
        _readClock = new RateClock(settings.PlateHz);
        _scanClock = new RateClock(settings.ScanHz);
    }

    public MarkerIndex Index { get; }

    public FrameHub Hub { get; }

    public ManualMonotonicClock Clock { get; }

    public PipelineEnvironment Environment { get; }

    public SocialConfig Config { get; set; }

    public TrackingWorker Tracking { get; }

    public StoppedWorker Stopped { get; }

    public ScanWorker Scan { get; }

    public ReadsWorker Reads { get; }

    public CacheWorker Cache { get; }

    public IndexSnapshot Snapshot => Index.Current;

    public void Submit(IReadOnlyList<IndexUpdate> updates)
    {
        if (updates.Count > 0)
            Index.Apply(updates, Clock.NowMs);
    }

    public void Feed(double frameMs, Mat image)
    {
        if (!_started)
        {
            _started = true;
            Clock.NowMs = frameMs;
            Submit(new IndexUpdate[] { new SettingsUpdate(PipelineGroup.Capture, frameMs, Index.Current.Epoch, Environment.Boundary, image.Width, image.Height) });
        }

        Clock.NowMs = frameMs;
        _stats?.AddGameFrame(frameMs);
        var frame = Capture(frameMs, image);
        frame.MarkPublished(Clock.NowMs);
        Hub.Publish(frame);
        _lastPublishedAtMs = frameMs;
        _intervals.Add(frameMs);
        Submit(new IndexUpdate[] { new NewFrameUpdate(PipelineGroup.Capture, frameMs, Index.Current.Epoch, frameMs, _intervals.MedianMs, frame.RequestIds) });

        var taken = Hub.TakeNewest() ?? throw new InvalidOperationException("The published frame is gone.");
        try
        {
            Tracking.Process(Index.Current, taken, Environment);
            if (Stopped.Offer(frameMs))
                Stopped.Process(Index.Current, taken, Environment);
            if (Scan.Accepts(taken))
                Scan.Process(Index.Current, taken, Environment);
            if (Reads.Accepts(taken))
                Reads.Process(Index.Current, taken, Environment, Config);
        }
        finally
        {
            taken.Release();
        }

        Clock.NowMs = frameMs + 0.001;
        Cache.Step(Index.Current, Config, Clock.NowMs);
        if (_readClock.TryTick(frameMs, out _))
            Reads.Prepare(Index.Current, Environment, Clock.NowMs, Config);
        if (_scanClock.TryTick(frameMs, out _))
            Scan.Prepare(Index.Current, Environment, Clock.NowMs);
    }

    private CapturedFrame Capture(double frameMs, Mat image)
    {
        var frame = Hub.Rent() ?? throw new InvalidOperationException("No frame is free.");
        frame.Begin(frameMs, frameMs, image.Width, image.Height);
        var included = CaptureRule.Select(Index.Current.Requests, frameMs, _lastPublishedAtMs);
        var patch = 0;
        foreach (var request in included)
        {
            var rect = RegionPlanner.ToWindowRect(request.Region, image.Width, image.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            var buffer = request.Kind == RequestKind.ScanArea ? frame.ScanBuffer : frame.PatchBuffer(patch++);
            using (var view = new Mat(image, rect))
                view.CopyTo(buffer);
            frame.Add(request, rect, buffer);
        }

        return frame;
    }

    public void Dispose()
    {
        Scan.Dispose();
        Cache.Dispose();
        Hub.Dispose();
    }
}
