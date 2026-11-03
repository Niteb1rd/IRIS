using System.Globalization;

namespace Iris.Core.Pipeline;

public sealed class LateFrameMonitor
{
    private const int IntervalsKept = 8;

    private readonly double[] _intervals = new double[IntervalsKept];
    private int _intervalCount;
    private int _intervalNext;
    private double _lastFrameMs = double.NaN;
    private int _late;
    private double _worstMs;
    private double _windowStartMs;
    private double _lastLogMs = double.NegativeInfinity;

    public LateFrameMonitor(double startMs) => _windowStartMs = startMs;

    public void Restart(double nowMs)
    {
        _intervalCount = 0;
        _intervalNext = 0;
        _lastFrameMs = double.NaN;
        _late = 0;
        _worstMs = 0.0;
        _windowStartMs = nowMs;
        _lastLogMs = double.NegativeInfinity;
    }

    public double MedianIntervalMs
    {
        get
        {
            if (_intervalCount == 0)
                return double.NaN;
            var sorted = _intervals.Take(_intervalCount).OrderBy(v => v).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }

    public string? OnFrame(double frameMs, double arrivedMs)
    {
        if (double.IsFinite(_lastFrameMs) && frameMs > _lastFrameMs)
        {
            _intervals[_intervalNext] = frameMs - _lastFrameMs;
            _intervalNext = (_intervalNext + 1) % IntervalsKept;
            _intervalCount = Math.Min(_intervalCount + 1, IntervalsKept);
        }

        if (!double.IsFinite(_lastFrameMs) || frameMs > _lastFrameMs)
            _lastFrameMs = frameMs;

        var delayMs = arrivedMs - frameMs;
        if (!(delayMs > LiveSettings.LateFrameThresholdMs))
            return null;
        _late++;
        _worstMs = Math.Max(_worstMs, delayMs);
        if (arrivedMs - _lastLogMs < LiveSettings.LateFrameLogEveryMs)
            return null;
        var text = Summary(_late, Math.Max(0.0, arrivedMs - _windowStartMs) / 1000.0, _worstMs, MedianIntervalMs);
        _late = 0;
        _worstMs = 0.0;
        _windowStartMs = arrivedMs;
        _lastLogMs = arrivedMs;
        return text;
    }

    public static string Summary(int count, double seconds, double worstMs, double frameIntervalMs)
    {
        var interval = double.IsFinite(frameIntervalMs) ? frameIntervalMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "unknown";
        return string.Create(CultureInfo.InvariantCulture,
            $"Live detection: frames arriving late: {count} in the last {seconds:0} s, worst {worstMs:0} ms (game frame interval {interval})");
    }
}
