namespace Iris.Core.Pipeline;

public sealed class LateFrameWindow
{
    public const string NoteText = "IRIS: Video capture is delayed, tracking may lag.";

    private readonly Queue<double> _lateAt = new();

    public bool Active { get; private set; }

    public static bool IsLate(double delayMs) => delayMs > LiveSettings.LateFrameThresholdMs;

    public bool Update(double nowMs, bool late)
    {
        if (late)
            _lateAt.Enqueue(nowMs);
        while (_lateAt.Count > 0 && nowMs - _lateAt.Peek() >= LiveSettings.LateFrameWindowMs)
            _lateAt.Dequeue();
        var active = _lateAt.Count >= LiveSettings.LateFrameMinCount;
        var changed = active != Active;
        Active = active;
        return changed;
    }

    public void Reset()
    {
        _lateAt.Clear();
        Active = false;
    }
}
