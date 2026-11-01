using System.Globalization;

namespace Iris.Core.Pipeline;

public static class LiveStats
{
    public static double FrameRate(int frames, double firstMs, double lastMs)
    {
        var span = lastMs - firstMs;
        return frames < 2 || !(span > 0) ? 0.0 : (frames - 1) * 1000.0 / span;
    }

    public static double Percentile95(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0.0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}

public enum RenderClockKind
{
    Vblank,
    Dwm,
    Timer,
    DwmTimer,
    KernelVblank,
}

public sealed class RenderStats
{
    private readonly object _gate = new();
    private int _frames;
    private int _redraws;
    private int _lateWaits;
    private double _totalMs;
    private double _windowStartMs;
    private RenderClockKind _clock = RenderClockKind.Timer;
    private double _monitorHz;
    private int _lastSummaryFrames;

    public RenderStats(double startMs) => _windowStartMs = startMs;

    public int Frames
    {
        get
        {
            lock (_gate)
                return _frames;
        }
    }

    public int LastSummaryFrames
    {
        get
        {
            lock (_gate)
                return _lastSummaryFrames;
        }
    }

    public void AddLateWait()
    {
        lock (_gate)
            _lateWaits++;
    }

    public void AddFrame(double milliseconds, bool redrew)
    {
        lock (_gate)
        {
            _frames++;
            if (redrew)
                _redraws++;
            _totalMs += milliseconds;
        }
    }

    public void SetClock(RenderClockKind clock, double monitorHz)
    {
        lock (_gate)
        {
            _clock = clock;
            _monitorHz = monitorHz;
        }
    }

    public bool IsDue(double nowMs, double everyMs)
    {
        lock (_gate)
            return nowMs - _windowStartMs >= everyMs;
    }

    public string TakeSummary(double nowMs)
    {
        lock (_gate)
        {
            var seconds = Math.Max(0.0, nowMs - _windowStartMs) / 1000.0;
            var average = _frames == 0 ? 0.0 : _totalMs / _frames;
            var perSecond = seconds > 0.0 ? _frames / seconds : 0.0;
            var clock = _clock switch
            {
                RenderClockKind.DwmTimer => "dwm-timer",
                RenderClockKind.KernelVblank => "kernel-vblank",
                _ => _clock.ToString().ToLowerInvariant(),
            };
            var line = string.Create(CultureInfo.InvariantCulture,
                $"Overlay render: {_frames} frames in {seconds:0} s ({perSecond:0.0} per second, monitor {_monitorHz:0.#} Hz, clock: {clock}), {_redraws} redraws, {_lateWaits} late vblank waits, average {average:0.000} ms per frame");
            _lastSummaryFrames = _frames;
            _frames = 0;
            _redraws = 0;
            _lateWaits = 0;
            _totalMs = 0;
            _windowStartMs = nowMs;
            return line;
        }
    }
}
