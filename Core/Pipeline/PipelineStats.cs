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
    Mining,
}

public sealed class PipelineStats : IContrastMaskSink
{
    public const int GroupCount = 9;

    private readonly double[] _busyMs = new double[GroupCount];
    private readonly int[] _skipped = new int[GroupCount];
    private readonly object _gate = new();
    private readonly List<double> _scanMs = new();
    private readonly List<double> _miningSearchMs = new();
    private readonly List<double> _miningReadMs = new();
    private int _miningLocks;
    private int _lossNames, _lossNamesUnsure, _lossMining, _lossMiningUnsure, _lossOverCap;
    private readonly List<double> _scanReadMs = new();
    private readonly List<double> _scanMaskMs = new();
    private readonly List<double> _scanLinesMs = new();
    private readonly List<double> _scanDistanceMs = new();
    private readonly List<double> _scanChecksMs = new();
    private readonly List<double> _checkMaskMs = new();
    private readonly List<double> _checkReadsMs = new();
    private readonly List<double> _checkTemplatesMs = new();
    private readonly List<double> _checkRestMs = new();
    private readonly List<double> _checkEachMs = new();
    private int _checksMost;
    private int _checkReads, _checkReadTries, _checkReadsFound, _checkReadsNoTry, _checkFailOwnName, _checkFailOtherPlate, _checkFailOther;
    private readonly List<double> _trackFindMs = new();
    private readonly List<double> _trackAssignMs = new();
    private readonly List<double> _trackRestMs = new();
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
    private int _templateMoves;
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
    private int _partialNames;
    private string _captureMethodText = "window (WGC)";
    private int _anchoredHits;
    private int _unverifiedHits;
    private int _overrides;
    private NameGateCounts _nameFailed;
    private int _colourMismatches;
    private int _lowFingerprints;
    private int _lostRefused;
    private int _lostSearchesSkipped;
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

    public int TemplateMoves
    {
        get
        {
            lock (_gate)
                return _templateMoves;
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

    public void AddMiningSearch(double ms)
    {
        lock (_gate)
            _miningSearchMs.Add(ms);
    }

    public void AddMiningRead(double ms)
    {
        lock (_gate)
            _miningReadMs.Add(ms);
    }

    public void AddMiningLock()
    {
        lock (_gate)
            _miningLocks++;
    }

    public void AddLossGroup(LossKind kind, bool unsure, bool written)
    {
        lock (_gate)
        {
            if (!written)
                _lossOverCap++;
            else if (kind == LossKind.Name && unsure)
                _lossNamesUnsure++;
            else if (kind == LossKind.Name)
                _lossNames++;
            else if (unsure)
                _lossMiningUnsure++;
            else
                _lossMining++;
        }
    }

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

    public void AddScanParts(ScanParts parts)
    {
        lock (_gate)
        {
            _scanMaskMs.Add(parts.MaskMs);
            _scanLinesMs.Add(parts.LinesMs);
            _scanDistanceMs.Add(parts.DistanceReadsMs);
        }
    }

    public void AddScanChecks(double milliseconds)
    {
        lock (_gate)
            _scanChecksMs.Add(milliseconds);
    }

    public void AddScanCheckParts(double maskMs, double readsMs, double templatesMs, double restMs, IReadOnlyCollection<double> eachMs)
    {
        lock (_gate)
        {
            _checkMaskMs.Add(maskMs);
            _checkReadsMs.Add(readsMs);
            _checkTemplatesMs.Add(templatesMs);
            _checkRestMs.Add(restMs);
            _checkEachMs.AddRange(eachMs);
            _checksMost = Math.Max(_checksMost, eachMs.Count);
        }
    }

    public void AddCheckTries(int tries, int failedOwnName, int failedOtherPlate, int failedOther, bool found)
    {
        lock (_gate)
        {
            _checkReads++;
            _checkReadTries += tries;
            if (found)
                _checkReadsFound++;
            if (tries == 0)
                _checkReadsNoTry++;
            _checkFailOwnName += failedOwnName;
            _checkFailOtherPlate += failedOtherPlate;
            _checkFailOther += failedOther;
        }
    }

    public void AddTrackParts(double findMs, double assignMs, double restMs)
    {
        lock (_gate)
        {
            _trackFindMs.Add(findMs);
            _trackAssignMs.Add(assignMs);
            _trackRestMs.Add(restMs);
        }
    }

    public IReadOnlyList<(string Stage, int Count, double AverageMs, double P95Ms)> TimingRows()
    {
        lock (_gate)
        {
            (string, int, double, double) Row(string stage, List<double> values) => (stage, values.Count, Average(values), LiveStats.Percentile95(values));
            return new[]
            {
                Row("scan", _scanMs), Row("scan mask", _scanMaskMs), Row("scan lines", _scanLinesMs), Row("scan distance reads", _scanDistanceMs),
                Row("scan checks", _scanChecksMs), Row("scan checks mask", _checkMaskMs), Row("scan checks reads", _checkReadsMs), Row("scan checks templates", _checkTemplatesMs),
                Row("scan checks rest", _checkRestMs), Row("scan check each", _checkEachMs), Row("reads", _readMs), Row("tracking", _trackMs), Row("tracking find", _trackFindMs),
                Row("tracking assign", _trackAssignMs), Row("tracking rest", _trackRestMs),
            };
        }
    }

    public void AddLostSearchSkipped()
    {
        lock (_gate)
            _lostSearchesSkipped++;
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

    public void AddTemplateMove()
    {
        lock (_gate)
            _templateMoves++;
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

    public void AddNameEvidence(int partial, int anchored, int unverified)
    {
        lock (_gate)
        {
            _partialNames += Math.Max(0, partial);
            _anchoredHits += Math.Max(0, anchored);
            _unverifiedHits += Math.Max(0, unverified);
        }
    }

    public string CaptureMethodText
    {
        get
        {
            lock (_gate)
                return _captureMethodText;
        }
        set
        {
            lock (_gate)
                _captureMethodText = value;
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
                string.Create(inv, $"Live detection: {_scanMs.Count} scans in {seconds:0} s (average {Average(_scanMs):0.0} ms, p95 {LiveStats.Percentile95(_scanMs):0.0} ms, longest scan {Max(_scanMs):0.0} ms; scan region read after publish: average {Average(_scanReadMs):0.0} ms, p95 {LiveStats.Percentile95(_scanReadMs):0.0} ms, {_scanUnavailable} scan ticks without the scan region; scan parts (average / p95): text mask {Average(_scanMaskMs):0.0} / {LiveStats.Percentile95(_scanMaskMs):0.0} ms, lines and pairs {Average(_scanLinesMs):0.0} / {LiveStats.Percentile95(_scanLinesMs):0.0} ms, distance reads {Average(_scanDistanceMs):0.0} / {LiveStats.Percentile95(_scanDistanceMs):0.0} ms), scan marker checks (average / p95) {Average(_scanChecksMs):0.0} / {LiveStats.Percentile95(_scanChecksMs):0.0} ms "),
                string.Create(inv, $"(text mask {Average(_checkMaskMs):0.0} / {LiveStats.Percentile95(_checkMaskMs):0.0} ms, lines and reads {Average(_checkReadsMs):0.0} / {LiveStats.Percentile95(_checkReadsMs):0.0} ms, templates {Average(_checkTemplatesMs):0.0} / {LiveStats.Percentile95(_checkTemplatesMs):0.0} ms, rest {Average(_checkRestMs):0.0} / {LiveStats.Percentile95(_checkRestMs):0.0} ms; one check {Average(_checkEachMs):0.00} / {LiveStats.Percentile95(_checkEachMs):0.00} ms, at most {_checksMost} checks a tick), "),
                string.Create(inv, $"check distance reads: {_checkReadTries} lines tried in {_checkReads} checks, {_checkReadsFound} found a distance, {_checkReadsNoTry} had no line to try; failed tries: own name row {_checkFailOwnName}, another plate {_checkFailOtherPlate}, other {_checkFailOther}; "),
                string.Create(inv, $"{_reads} plate reads in {_readTicks} of {_plateTicks} plate ticks (budget use {use:0.0} of {budget} per tick, {_saturated} ticks at the budget, average {Average(_readMs):0.0} ms, p95 {LiveStats.Percentile95(_readMs):0.0} ms, longest read tick {Max(_readMs):0.0} ms), "),
                string.Create(inv, $"ticks skipped (worker busy): reads {_skipped[(int)PipelineGroup.Reads]}, scan {_skipped[(int)PipelineGroup.Scan]}; {_unavailable} without a picture; "),
                string.Create(inv, $"markers tracked {markerAverage:0.0} on average, at most {_markerMax}, {markersNow} now; ended by the marker check: {_endConfirmedReads} confirmed plates by failed distance reads, {_endConfirmedNoTracking} confirmed plates by no tracking hits, {_endUnconfirmed} unconfirmed markers by checks; {_boundaryEnds} left the boundary, {_confirmed} confirmed ({confirmedNow} now), {_restored} restored, {_stopped} stopped; "),
                string.Create(inv, $"template keep-alives {_templateKeepAlives}, template rejects (below threshold) {_templateRejects}, template guard rejects (other plate) {_templateGuardRejects}, lost plates refused (name did not match) {_lostRefused}, lost-plate searches skipped {_lostSearchesSkipped}; lost plates moved to the template's place {_templateMoves}; "),
                string.Create(inv, $"tracking: {_trackMs.Count} ticks, average {Average(_trackMs):0.000} ms, p95 {LiveStats.Percentile95(_trackMs):0.000} ms, {share:0.0} % of {_attempts} distance lines found; tracking parts (average / p95): line search {Average(_trackFindMs):0.000} / {LiveStats.Percentile95(_trackFindMs):0.000} ms, assignment {Average(_trackAssignMs):0.000} / {LiveStats.Percentile95(_trackAssignMs):0.000} ms, rest {Average(_trackRestMs):0.000} / {LiveStats.Percentile95(_trackRestMs):0.000} ms; "),
                string.Create(inv, $"confirmed plates: distance line found {confirmedShare:0.0} % of {_confirmedAttempts} tracking attempts; "),
                string.Create(inv, $"assignment: {_contested} contested frames, {_overrides} assignment overrides, "),
                string.Create(inv, $"name-line failures: width {_nameFailed.Width}, gap {_nameFailed.Gap}, centre {_nameFailed.Centre}, not found {_nameFailed.NotFound}; colour mismatches {_colourMismatches}, low fingerprint {_lowFingerprints}; "),
                string.Create(inv, $"covered plates: {_partialNames} hits on a partly covered name row, {_anchoredHits} placed from the name row, {_unverifiedHits} on the distance line alone; "),
                string.Create(inv, $"contrast mask: {_contrastMasks} masks with bright background, share of mask pixels from the contrast rule {contrastShare:0.0} %; "),
                string.Create(inv, $"capture: {_captureMethodText}, delay average {Average(_captureDelayMs):0.0} ms, p95 {LiveStats.Percentile95(_captureDelayMs):0.0} ms, late frames {_lateFrames}, worst {_lateWorstMs:0} ms; "),
                string.Create(inv, $"game frames ~{gameRate:0}/s, tracked ~{trackedRate:0}/s ({cap}); "),
                string.Create(inv, $"mining: {_miningSearchMs.Count} searches (average {Average(_miningSearchMs):0.00} ms, p95 {LiveStats.Percentile95(_miningSearchMs):0.00} ms), {_miningLocks} locks, {_miningReadMs.Count} lock reads (average {Average(_miningReadMs):0.00} ms, p95 {LiveStats.Percentile95(_miningReadMs):0.00} ms); "),
                string.Create(inv, $"loss crops: names {_lossNames} (unsure {_lossNamesUnsure}), mining {_lossMining} (unsure {_lossMiningUnsure}), {_lossOverCap} over the folder cap; "),
                IndexText(index),
                CpuText(seconds * 1000.0));
            _lostRefusalText = LostRefusalSample.Text(_lostRefusalSamples);
            _lostRefusalSamples.Clear();
            _scanMs.Clear();
            _scanMaskMs.Clear();
            _scanLinesMs.Clear();
            _scanChecksMs.Clear();
            _checkMaskMs.Clear();
            _checkReadsMs.Clear();
            _checkTemplatesMs.Clear();
            _checkRestMs.Clear();
            _checkEachMs.Clear();
            _checksMost = 0;
            _checkReads = _checkReadTries = _checkReadsFound = _checkReadsNoTry = _checkFailOwnName = _checkFailOtherPlate = _checkFailOther = 0;
            _trackFindMs.Clear();
            _trackAssignMs.Clear();
            _trackRestMs.Clear();
            _scanDistanceMs.Clear();
            _lostSearchesSkipped = 0;
            _miningSearchMs.Clear();
            _miningReadMs.Clear();
            _miningLocks = 0;
            _lossNames = _lossNamesUnsure = _lossMining = _lossMiningUnsure = _lossOverCap = 0;
            _scanReadMs.Clear();
            _scanUnavailable = 0;
            _readMs.Clear();
            _trackMs.Clear();
            _captureDelayMs.Clear();
            _plateTicks = _readTicks = _reads = _saturated = _unavailable = 0;
            Array.Clear(_skipped);
            Array.Clear(_busyMs);
            _endConfirmedReads = _endConfirmedNoTracking = _endUnconfirmed = _restored = 0;
            _templateKeepAlives = _templateRejects = _templateGuardRejects = _templateMoves = 0;
            _boundaryEnds = _confirmed = _stopped = 0;
            _markerSum = 0;
            _markerSamples = 0;
            _markerMax = 0;
            _attempts = _found = 0;
            _confirmedAttempts = _confirmedFound = 0;
            _contested = _overrides = 0;
            _nameFailed = default;
            _colourMismatches = _lowFingerprints = _lostRefused = 0;
            _partialNames = _anchoredHits = _unverifiedHits = 0;
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
            $"index: updates rejected as older than the stored value: tracking {counts.RejectedBy(PipelineGroup.Tracking)}, stopped {counts.RejectedBy(PipelineGroup.Stopped)}, reads {counts.RejectedBy(PipelineGroup.Reads)}, scan {counts.RejectedBy(PipelineGroup.Scan)}, capture {counts.RejectedBy(PipelineGroup.Capture)}, cache {counts.RejectedBy(PipelineGroup.Cache)}, mining {counts.RejectedBy(PipelineGroup.Mining)}; "
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
            + Share(PipelineGroup.Scan) + Share(PipelineGroup.Render) + Share(PipelineGroup.Cache) + Share(PipelineGroup.Mining);
        return string.Create(inv,
            $"CPU: capture {Share(PipelineGroup.Capture):0.0}%, tracking {Share(PipelineGroup.Tracking):0.0}%, index {Share(PipelineGroup.Index):0.0}%, stopped {Share(PipelineGroup.Stopped):0.0}%, reads {Share(PipelineGroup.Reads):0.0}%, cache {Share(PipelineGroup.Cache):0.0}%, scan {Share(PipelineGroup.Scan):0.0}%, render {Share(PipelineGroup.Render):0.0}%, mining {Share(PipelineGroup.Mining):0.0}%, total {total:0.0}% of one core");
    }

    private static double Max(List<double> values) => values.Count == 0 ? 0.0 : values.Max();

    private static double Average(List<double> values) => values.Count == 0 ? 0.0 : values.Average();
}
