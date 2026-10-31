using System.Globalization;
using Iris.Core.Detection;

namespace Iris.Core.Pipeline;

public enum PipelineGroup
{
    Tracking,
    Stopped,
    Reads,
    Scan,
    Render,
    Capture,
    Index,
    Cache,
}

public sealed class PipelineStats : IContrastMaskSink
{
    public const int GroupCount = 8;

    private readonly double[] _busyMs = new double[GroupCount];
    private readonly int[] _skipped = new int[GroupCount];
    private readonly object _gate = new();
    private readonly List<double> _scanMs = new();
    private readonly List<double> _scanReadMs = new();
    private int _scanUnavailable;
    private readonly List<double> _readMs = new();
    private readonly List<double> _trackMs = new();
    private readonly List<double> _captureDelayMs = new();
    private int _plateTicks;
    private int _readTicks;
    private int _reads;
    private int _saturated;
    private int _unavailable;
    private int _endConfirmedReads;
    private int _endConfirmedNoTracking;
    private int _endUnconfirmed;
    private int _templateKeepAlives;
    private int _templateRejects;
    private int _templateGuardRejects;
    private int _restored;
    private int _boundaryEnds;
    private int _confirmed;
    private int _stopped;
    private long _markerSum;
    private int _markerMax;
    private int _markerSamples;
    private long _attempts;
    private long _found;
    private long _confirmedAttempts;
    private long _confirmedFound;
    private int _contested;
    private int _overrides;
    private NameGateCounts _nameFailed;
    private int _colourMismatches;
    private int _lowFingerprints;
    private int _lostRefused;
    private int _contrastMasks;
    private long _contrastMaskPixels;
    private long _contrastRulePixels;
    private readonly List<LostRefusalSample> _lostRefusalSamples = new();
    private string? _lostRefusalText;
    private double _windowStartMs;
    private int _gameFrames;
    private double _firstGameFrameMs;
    private double _lastGameFrameMs;
    private int _trackedFrames;
    private int _lateFrames;
    private double _lateWorstMs;

    public PipelineStats(double startMs) => _windowStartMs = startMs;

    public int Scans
    {
        get
        {
            lock (_gate)
                return _scanMs.Count;
        }
    }

    public int Reads
    {
        get
        {
            lock (_gate)
                return _reads;
        }
    }

    public int B3Ends
    {
        get
        {
            lock (_gate)
                return _endConfirmedReads + _endConfirmedNoTracking + _endUnconfirmed;
        }
    }

    public int EndedConfirmedReads
    {
        get
        {
            lock (_gate)
                return _endConfirmedReads;
        }
    }

    public int EndedConfirmedNoTracking
    {
        get
        {
            lock (_gate)
                return _endConfirmedNoTracking;
        }
    }

    public int EndedUnconfirmed
    {
        get
        {
            lock (_gate)
                return _endUnconfirmed;
        }
    }

    public int TemplateKeepAlives
    {
        get
        {
            lock (_gate)
                return _templateKeepAlives;
        }
    }

    public int LostPlatesRefused
    {
        get
        {
            lock (_gate)
                return _lostRefused;
        }
    }

    public int TemplateRejects
    {
        get
        {
            lock (_gate)
                return _templateRejects;
        }
    }

    public int TemplateGuardRejects
    {
        get
        {
            lock (_gate)
                return _templateGuardRejects;
        }
    }

    public int Restored
    {
        get
        {
            lock (_gate)
                return _restored;
        }
    }

    public int Confirmed
    {
        get
        {
            lock (_gate)
                return _confirmed;
        }
    }

    public int Skipped(PipelineGroup group)
    {
        lock (_gate)
            return _skipped[(int)group];
    }

    public double BusyMs(PipelineGroup group)
    {
        lock (_gate)
            return _busyMs[(int)group];
    }

    public static double CpuShare(double busyMs, double windowMs) => windowMs > 0.0 ? 100.0 * busyMs / windowMs : 0.0;

    public void AddGameFrame(double frameMs)
    {
        lock (_gate)
        {
            if (_gameFrames == 0)
                _firstGameFrameMs = frameMs;
            _lastGameFrameMs = frameMs;
            _gameFrames++;
        }
    }

    public void AddCaptureDelay(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
            return;
        lock (_gate)
            _captureDelayMs.Add(milliseconds);
    }

    public void AddLateFrame(double delayMs)
    {
        if (!double.IsFinite(delayMs))
            return;
        lock (_gate)
        {
            _lateFrames++;
            _lateWorstMs = Math.Max(_lateWorstMs, delayMs);
        }
    }

    public void AddTrackedFrame()
    {
        lock (_gate)
            _trackedFrames++;
    }

    public void AddPlateTick()
    {
        lock (_gate)
            _plateTicks++;
    }

    public void AddSkipped(PipelineGroup group)
    {
        lock (_gate)
            _skipped[(int)group]++;
    }

    public void AddBusy(PipelineGroup group, double milliseconds)
    {
        if (!(milliseconds > 0.0))
            return;
        lock (_gate)
            _busyMs[(int)group] += milliseconds;
    }

    public void AddUnavailable()
    {
        lock (_gate)
            _unavailable++;
    }

    public void AddScan(double milliseconds)
    {
        lock (_gate)
            _scanMs.Add(milliseconds);
    }

    public void AddScanRead(double milliseconds)
    {
        if (!double.IsFinite(milliseconds))
            return;
        lock (_gate)
        {
            if (_scanReadMs.Count < IndexCounters.MaxSamples)
                _scanReadMs.Add(milliseconds);
        }
    }

    public void AddScanUnavailable()
    {
        lock (_gate)
            _scanUnavailable++;
    }

    public void AddReadTick(int reads, int budget, double milliseconds)
    {
        if (reads <= 0)
            return;
        lock (_gate)
        {
            _readTicks++;
            _reads += reads;
            if (reads >= budget)
                _saturated++;
            _readMs.Add(milliseconds);
        }
    }

    public void AddEndConfirmedReads()
    {
        lock (_gate)
            _endConfirmedReads++;
    }

    public void AddEndConfirmedNoTracking()
    {
        lock (_gate)
            _endConfirmedNoTracking++;
    }

    public void AddEndUnconfirmed()
    {
        lock (_gate)
            _endUnconfirmed++;
    }

    public void AddTemplateKeepAlive()
    {
        lock (_gate)
            _templateKeepAlives++;
    }

    public void AddTemplateReject()
    {
        lock (_gate)
            _templateRejects++;
    }

    public void AddTemplateGuardReject()
    {
        lock (_gate)
            _templateGuardRejects++;
    }

    public void AddRestored()
    {
        lock (_gate)
            _restored++;
    }

    public void AddBoundaryEnd()
    {
        lock (_gate)
            _boundaryEnds++;
    }

    public void AddConfirmed()
    {
        lock (_gate)
            _confirmed++;
    }

    public void AddStopped()
    {
        lock (_gate)
            _stopped++;
    }

    public void AddMarkers(int tracked)
    {
        lock (_gate)
        {
            _markerSum += tracked;
            _markerSamples++;
            _markerMax = Math.Max(_markerMax, tracked);
        }
    }

    public void AddTrackTick(double milliseconds, int attempts, int found, int confirmedAttempts = 0, int confirmedFound = 0)
    {
        lock (_gate)
        {
            _trackMs.Add(milliseconds);
            _attempts += Math.Max(0, attempts);
            _found += Math.Max(0, found);
            _confirmedAttempts += Math.Max(0, confirmedAttempts);
            _confirmedFound += Math.Max(0, confirmedFound);
        }
    }

    public void AddAssignment(bool contested, int overrides, NameGateCounts nameFailed, int colourMismatches = 0, int lowFingerprints = 0, int lostRefused = 0)
    {
        lock (_gate)
        {
            if (contested)
                _contested++;
            _overrides += Math.Max(0, overrides);
            _nameFailed = new NameGateCounts(
                _nameFailed.Width + nameFailed.Width, _nameFailed.Gap + nameFailed.Gap, _nameFailed.Centre + nameFailed.Centre, _nameFailed.NotFound + nameFailed.NotFound);
            _colourMismatches += Math.Max(0, colourMismatches);
            _lowFingerprints += Math.Max(0, lowFingerprints);
            _lostRefused += Math.Max(0, lostRefused);
        }
    }

    public void AddContrastMask(int maskPixels, int contrastPixels)
    {
        lock (_gate)
        {
            _contrastMasks++;
            _contrastMaskPixels += Math.Max(0, maskPixels);
            _contrastRulePixels += Math.Max(0, contrastPixels);
        }
    }

    public void AddLostRefusals(IReadOnlyList<LostRefusalSample>? samples)
    {
        if (samples is null || samples.Count == 0)
            return;
        lock (_gate)
            foreach (var sample in samples)
            {
                if (_lostRefusalSamples.Count >= PipelineSettings.LostRefusalSamplesPerMinute)
                    break;
                _lostRefusalSamples.Add(sample);
            }
    }

    public string? TakeLostRefusalText()
    {
        lock (_gate)
        {
            var text = _lostRefusalText;
            _lostRefusalText = null;
            return text;
        }
    }

    public void Restart(double nowMs) => TakeSummary(nowMs, 0, 0, 0);

    public bool IsDue(double nowMs, double everyMs)
    {
        lock (_gate)
            return nowMs - _windowStartMs >= everyMs;
    }

    public string TakeSummary(double nowMs, int markersNow, int confirmedNow, int budget, int trackingCapFps = 0, IndexCounts? index = null)
    {
        lock (_gate)
        {
            var seconds = Math.Max(0.0, nowMs - _windowStartMs) / 1000.0;
            var inv = CultureInfo.InvariantCulture;
            var use = _readTicks == 0 ? 0.0 : (double)_reads / _readTicks;
            var markerAverage = _markerSamples == 0 ? 0.0 : (double)_markerSum / _markerSamples;
            var share = _attempts == 0 ? 0.0 : 100.0 * _found / _attempts;
            var confirmedShare = _confirmedAttempts == 0 ? 0.0 : 100.0 * _confirmedFound / _confirmedAttempts;
            var gameRate = LiveStats.FrameRate(_gameFrames, _firstGameFrameMs, _lastGameFrameMs);
            var trackedRate = seconds > 0 ? _trackedFrames / seconds : 0.0;
            var contrastShare = _contrastMaskPixels == 0 ? 0.0 : 100.0 * _contrastRulePixels / _contrastMaskPixels;
            var cap = trackingCapFps > 0 ? $"cap {trackingCapFps}" : "no cap";
            var line = string.Concat(
                string.Create(inv, $"Live detection: {_scanMs.Count} scans in {seconds:0} s (average {Average(_scanMs):0.0} ms, p95 {LiveStats.Percentile95(_scanMs):0.0} ms, longest scan {Max(_scanMs):0.0} ms; scan region read after publish: average {Average(_scanReadMs):0.0} ms, p95 {LiveStats.Percentile95(_scanReadMs):0.0} ms, {_scanUnavailable} scan ticks without the scan region), "),
                string.Create(inv, $"{_reads} plate reads in {_readTicks} of {_plateTicks} plate ticks (budget use {use:0.0} of {budget} per tick, {_saturated} ticks at the budget, average {Average(_readMs):0.0} ms, p95 {LiveStats.Percentile95(_readMs):0.0} ms, longest read tick {Max(_readMs):0.0} ms), "),
                string.Create(inv, $"ticks skipped (worker busy): reads {_skipped[(int)PipelineGroup.Reads]}, scan {_skipped[(int)PipelineGroup.Scan]}; {_unavailable} without a picture; "),
                string.Create(inv, $"markers tracked {markerAverage:0.0} on average, at most {_markerMax}, {markersNow} now; ended by the marker check: {_endConfirmedReads} confirmed plates by failed distance reads, {_endConfirmedNoTracking} confirmed plates by no tracking hits, {_endUnconfirmed} unconfirmed markers by checks; {_boundaryEnds} left the boundary, {_confirmed} confirmed ({confirmedNow} now), {_restored} restored, {_stopped} stopped; "),
                string.Create(inv, $"template keep-alives {_templateKeepAlives}, template rejects (below threshold) {_templateRejects}, template guard rejects (other plate) {_templateGuardRejects}, lost plates refused (name did not match) {_lostRefused}; "),
                string.Create(inv, $"tracking: {_trackMs.Count} ticks, average {Average(_trackMs):0.000} ms, p95 {LiveStats.Percentile95(_trackMs):0.000} ms, {share:0.0} % of {_attempts} distance lines found; "),
                string.Create(inv, $"confirmed plates: distance line found {confirmedShare:0.0} % of {_confirmedAttempts} tracking attempts; "),
                string.Create(inv, $"assignment: {_contested} contested frames, {_overrides} assignment overrides, "),
                string.Create(inv, $"name-line failures: width {_nameFailed.Width}, gap {_nameFailed.Gap}, centre {_nameFailed.Centre}, not found {_nameFailed.NotFound}; colour mismatches {_colourMismatches}, low fingerprint {_lowFingerprints}; "),
                string.Create(inv, $"contrast mask: {_contrastMasks} masks with bright background, share of mask pixels from the contrast rule {contrastShare:0.0} %; "),
                string.Create(inv, $"capture: window (WGC), delay average {Average(_captureDelayMs):0.0} ms, p95 {LiveStats.Percentile95(_captureDelayMs):0.0} ms, late frames {_lateFrames}, worst {_lateWorstMs:0} ms; "),
                string.Create(inv, $"game frames ~{gameRate:0}/s, tracked ~{trackedRate:0}/s ({cap}); "),
                IndexText(index),
                CpuText(seconds * 1000.0));
            _lostRefusalText = LostRefusalSample.Text(_lostRefusalSamples);
            _lostRefusalSamples.Clear();
            _scanMs.Clear();
            _scanReadMs.Clear();
            _scanUnavailable = 0;
            _readMs.Clear();
            _trackMs.Clear();
            _captureDelayMs.Clear();
            _plateTicks = _readTicks = _reads = _saturated = _unavailable = 0;
            Array.Clear(_skipped);
            Array.Clear(_busyMs);
            _endConfirmedReads = _endConfirmedNoTracking = _endUnconfirmed = _restored = 0;
            _templateKeepAlives = _templateRejects = _templateGuardRejects = 0;
            _boundaryEnds = _confirmed = _stopped = 0;
            _markerSum = 0;
            _markerSamples = 0;
            _markerMax = 0;
            _attempts = _found = 0;
            _confirmedAttempts = _confirmedFound = 0;
            _contested = _overrides = 0;
            _nameFailed = default;
            _colourMismatches = _lowFingerprints = _lostRefused = 0;
            _contrastMasks = 0;
            _contrastMaskPixels = _contrastRulePixels = 0;
            _gameFrames = 0;
            _trackedFrames = 0;
            _lateFrames = 0;
            _lateWorstMs = 0.0;
            _windowStartMs = nowMs;
            return line;
        }
    }

    private static string IndexText(IndexCounts? counts)
    {
        var inv = CultureInfo.InvariantCulture;
        if (counts is null)
            return string.Empty;
        var latencies = counts.LatenciesMs.ToList();
        return string.Create(inv,
            $"index: updates rejected as older than the stored value: tracking {counts.RejectedBy(PipelineGroup.Tracking)}, stopped {counts.RejectedBy(PipelineGroup.Stopped)}, reads {counts.RejectedBy(PipelineGroup.Reads)}, scan {counts.RejectedBy(PipelineGroup.Scan)}, capture {counts.RejectedBy(PipelineGroup.Capture)}, cache {counts.RejectedBy(PipelineGroup.Cache)}; "
            + $"requests expired unfulfilled: tracking {counts.ExpiredBy(PipelineGroup.Tracking)}, stopped {counts.ExpiredBy(PipelineGroup.Stopped)}, reads {counts.ExpiredBy(PipelineGroup.Reads)}, scan {counts.ExpiredBy(PipelineGroup.Scan)}; "
            + $"frame to published: average {Average(latencies):0.00} ms, p95 {LiveStats.Percentile95(latencies):0.00} ms over {latencies.Count} frames; "
            + $"{BreakdownText(counts.Breakdown)}");
    }

    private static string BreakdownText(FrameBreakdown? parts)
    {
        var inv = CultureInfo.InvariantCulture;
        static string Pair(IReadOnlyList<double> values) =>
            string.Create(CultureInfo.InvariantCulture, $"{(values.Count == 0 ? 0.0 : values.Average()):0.00} / {LiveStats.Percentile95(values):0.00}");
        parts ??= new FrameBreakdown(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
        return string.Create(inv,
            $"breakdown (average / p95): capture handover {Pair(parts.CaptureHandoverMs)} ms, copy regions {Pair(parts.CopyMs)} ms, hand-over to tracking {Pair(parts.TrackingHandOverMs)} ms, tracking work {Pair(parts.WorkMs)} ms, delivery to index {Pair(parts.DeliveryMs)} ms, index apply {Pair(parts.ApplyMs)} ms, render pickup {Pair(parts.RenderPickupMs)} ms; ");
    }

    private string CpuText(double windowMs)
    {
        var inv = CultureInfo.InvariantCulture;
        double Share(PipelineGroup g) => CpuShare(_busyMs[(int)g], windowMs);
        var total = Share(PipelineGroup.Capture) + Share(PipelineGroup.Tracking) + Share(PipelineGroup.Index) + Share(PipelineGroup.Stopped) + Share(PipelineGroup.Reads)
            + Share(PipelineGroup.Scan) + Share(PipelineGroup.Render) + Share(PipelineGroup.Cache);
        return string.Create(inv,
            $"CPU: capture {Share(PipelineGroup.Capture):0.0}%, tracking {Share(PipelineGroup.Tracking):0.0}%, index {Share(PipelineGroup.Index):0.0}%, stopped {Share(PipelineGroup.Stopped):0.0}%, reads {Share(PipelineGroup.Reads):0.0}%, cache {Share(PipelineGroup.Cache):0.0}%, scan {Share(PipelineGroup.Scan):0.0}%, render {Share(PipelineGroup.Render):0.0}%, total {total:0.0}% of one core");
    }

    private static double Max(List<double> values) => values.Count == 0 ? 0.0 : values.Max();

    private static double Average(List<double> values) => values.Count == 0 ? 0.0 : values.Average();
}
