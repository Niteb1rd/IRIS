namespace Iris.Core.Pipeline;

public sealed class TrackingSchedule
{
    private double _nextAllowed = double.NaN;

    public double NextAllowedMs => _nextAllowed;

    public void Reset() => _nextAllowed = double.NaN;

    public static double PeriodMs(int capFps) => capFps <= 0 ? 0.0 : 1000.0 / capFps;

    public bool Offer(double frameMs, int capFps)
    {
        if (capFps <= 0)
        {
            _nextAllowed = double.NaN;
            return true;
        }

        var period = PeriodMs(capFps);
        if (double.IsNaN(_nextAllowed))
        {
            _nextAllowed = frameMs + period;
            return true;
        }

        if (frameMs < _nextAllowed - PipelineSettings.SlackMs(period))
            return false;
        _nextAllowed = Math.Max(_nextAllowed + period, frameMs - period);
        return true;
    }
}

public static class FrameClock
{
    public static double QpcToMs(long qpcTicks, long originTicks, long frequency)
    {
        if (frequency <= 0)
            return double.NaN;
        return (double)(qpcTicks - originTicks) * 1000.0 / frequency;
    }

    public static double FrameMs(long lastPresentQpc, long originTicks, long frequency, double acquiredAtMs)
    {
        if (lastPresentQpc == 0)
            return acquiredAtMs;
        var ms = QpcToMs(lastPresentQpc, originTicks, frequency);
        if (double.IsNaN(ms) || ms > acquiredAtMs + LiveSettings.FrameMaxAheadMs || ms < acquiredAtMs - LiveSettings.FrameMaxBehindMs)
            return acquiredAtMs;
        return ms;
    }
}
