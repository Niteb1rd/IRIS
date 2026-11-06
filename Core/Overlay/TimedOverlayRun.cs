using Iris.Core.Config;

namespace Iris.Core.Overlay;

public enum TimedOverlayPress
{
    Ignored,

    Started,

    Restarted,
}

public sealed class TimedOverlayRun
{
    private long _endMs;

    public bool IsActive { get; private set; }

    public TimedOverlayPress Press(bool overlayDisabled, int seconds, long nowMs)
    {
        if (!overlayDisabled && !IsActive)
            return TimedOverlayPress.Ignored;
        var restarted = IsActive;
        IsActive = true;
        _endMs = nowMs + Math.Clamp(seconds, AppSettings.TimedOverlaySecondsMin, AppSettings.TimedOverlaySecondsMax) * 1000L;
        return restarted ? TimedOverlayPress.Restarted : TimedOverlayPress.Started;
    }

    public int SecondsLeft(long nowMs)
    {
        if (!IsActive)
            return 0;
        var left = _endMs - nowMs;
        return left <= 0 ? 0 : (int)((left + 999) / 1000);
    }

    public bool Expire(long nowMs)
    {
        if (!IsActive || nowMs < _endMs)
            return false;
        IsActive = false;
        return true;
    }

    public void Stop() => IsActive = false;

    public static string CountdownText(int seconds) => $"Overlay ON ({seconds} sec)";
}
