namespace Iris.Core.Lookup;

public sealed class RsiCircuitBreaker
{
    private readonly object _gate = new();
    private int _count;
    private bool _open;
    private DateTimeOffset _openUntil;
    private DateTimeOffset _lastCounted = DateTimeOffset.MinValue;

    public bool IsOpen(DateTimeOffset now, out TimeSpan remaining, out bool justClosed)
    {
        lock (_gate)
        {
            remaining = TimeSpan.Zero;
            justClosed = false;
            if (!_open)
                return false;
            if (now >= _openUntil)
            {
                _open = false;
                _count = 0;
                justClosed = true;
                return false;
            }

            remaining = _openUntil - now;
            return true;
        }
    }

    public bool RecordFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_open || (now - _lastCounted).Duration() < CacheSettings.RsiDefaultInterval)
                return false;
            _lastCounted = now;
            _count++;
            if (_count < CacheSettings.RsiBreakerThreshold)
                return false;
            _open = true;
            _openUntil = now + CacheSettings.RsiBreakerPause;
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            if (_open)
                return;
            _count = 0;
            _lastCounted = DateTimeOffset.MinValue;
        }
    }
}
