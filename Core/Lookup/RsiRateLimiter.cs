namespace Iris.Core.Lookup;

public readonly record struct BackoffChange(TimeSpan Before, TimeSpan After, TimeSpan NextDelay)
{
    public bool Changed => Before != After;
}

public sealed class RsiRateLimiter
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;

    private TimeSpan _gap;

    private DateTimeOffset _lastSlot = DateTimeOffset.MinValue;

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    private long _generation;

    public RsiRateLimiter(IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? interval = null)
    {
        _clock = clock;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _interval = interval ?? CacheSettings.RsiDefaultInterval;
        _gap = _interval;
    }

    public TimeSpan CurrentGap
    {
        get
        {
            lock (_gate)
                return _gap;
        }
    }

    public BackoffChange ReportSuccess(long? turn = null)
    {
        lock (_gate)
        {
            var before = _gap;
            if (turn is { } sentIn && sentIn != _generation)
                return new BackoffChange(before, before, TimeSpan.Zero);

            var halved = TimeSpan.FromTicks(_gap.Ticks / 2);
            _gap = halved < _interval ? _interval : halved;
            return new BackoffChange(before, _gap, TimeSpan.Zero);
        }
    }

    public BackoffChange ReportThrottled(TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var before = _gap;
            _generation++;
            var doubled = TimeSpan.FromTicks(_gap.Ticks * 2);
            _gap = doubled > CacheSettings.RsiMaxBackoff ? CacheSettings.RsiMaxBackoff : doubled;
            if (_gap < _interval)
                _gap = _interval;

            var wait = _gap;
            if (retryAfter is { } asked && asked > wait)
                wait = asked > CacheSettings.RsiMaxRetryAfter ? CacheSettings.RsiMaxRetryAfter : asked;

            var until = _clock.UtcNow + wait;
            if (until > _notBefore)
                _notBefore = until;
            return new BackoffChange(before, _gap, wait);
        }
    }

    public async Task<long> WaitForTurnAsync(CancellationToken ct = default)
    {
        Reservation? previous = null;
        while (true)
        {
            Reservation reservation;
            long generation;
            lock (_gate)
            {
                var now = _clock.UtcNow;
                generation = _generation;

                if (previous is not null && _notBefore <= now)
                    return generation;

                if (previous is { } old)
                    Release(old);

                reservation = Reserve(now);
            }
            previous = reservation;

            if (reservation.Wait <= TimeSpan.Zero)
                return generation;

            try
            {
                await _delay(reservation.Wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                    Release(reservation);
                throw;
            }
        }
    }

    private Reservation Reserve(DateTimeOffset now)
    {
        var earliest = _lastSlot + _gap;
        if (_notBefore > earliest)
            earliest = _notBefore;
        var slot = earliest > now ? earliest : now;

        var reservation = new Reservation(slot, _lastSlot, slot - now);
        _lastSlot = slot;
        return reservation;
    }

    private void Release(Reservation reservation)
    {
        if (_lastSlot == reservation.Slot)
            _lastSlot = reservation.PreviousSlot;
    }

    private readonly record struct Reservation(DateTimeOffset Slot, DateTimeOffset PreviousSlot, TimeSpan Wait);
}
