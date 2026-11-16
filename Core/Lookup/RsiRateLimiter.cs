namespace Iris.Core.Lookup;

public readonly record struct BackoffChange(TimeSpan Before, TimeSpan After, TimeSpan NextDelay)
{
    public bool Changed => Before != After;
}

public sealed class RsiRateLimiter
{
    private readonly Lock _gate = new();
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;
    private readonly int _burst;
    private readonly TimeSpan _burstGap;

    private TimeSpan _gap;

    private double _tokens;
    private DateTimeOffset _tokensAt = DateTimeOffset.MinValue;

    private DateTimeOffset _lastSlot = DateTimeOffset.MinValue;

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    private long _generation;

    private DateTimeOffset _lastNow = DateTimeOffset.MinValue;

    public RsiRateLimiter(IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? interval = null, int burst = 1, TimeSpan? burstGap = null)
    {
        _clock = clock;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _interval = interval ?? CacheSettings.RsiDefaultInterval;
        _burst = Math.Max(1, burst);
        var gap = burstGap ?? _interval;
        _burstGap = gap < TimeSpan.Zero ? TimeSpan.Zero : gap > _interval ? _interval : gap;
        _gap = _interval;
        _tokens = _burst;
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

            var now = Now();
            var until = now + wait;
            if (until > _notBefore)
                _notBefore = until;
            _tokens = 0.0;
            _tokensAt = now;
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
                var now = Now();
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

    private DateTimeOffset Now()
    {
        var now = _clock.UtcNow;
        if (now < _lastNow)
        {
            var back = _lastNow - now;
            _lastSlot = MoveBack(_lastSlot, back);
            _notBefore = MoveBack(_notBefore, back);
            _tokensAt = MoveBack(_tokensAt, back);
        }

        _lastNow = now;
        return now;
    }

    private static DateTimeOffset MoveBack(DateTimeOffset time, TimeSpan back) =>
        time == DateTimeOffset.MinValue || time - DateTimeOffset.MinValue <= back ? DateTimeOffset.MinValue : time - back;

    private Reservation Reserve(DateTimeOffset now)
    {
        DateTimeOffset slot;
        double tokensLeft;
        if (_gap > _interval)
        {
            slot = Later(Later(_lastSlot + _gap, _notBefore), now);
            tokensLeft = 0.0;
        }
        else
        {
            slot = Later(Later(_lastSlot + _burstGap, _notBefore), now);
            var tokens = TokensAt(slot);
            if (tokens < 1.0)
            {
                slot = Later(slot, _tokensAt + TimeSpan.FromTicks((long)Math.Ceiling((1.0 - _tokens) * _interval.Ticks)));
                tokens = Math.Max(1.0, TokensAt(slot));
            }

            tokensLeft = tokens - 1.0;
        }

        var reservation = new Reservation(slot, _lastSlot, slot - now, _tokens, _tokensAt);
        _lastSlot = slot;
        _tokens = tokensLeft;
        _tokensAt = slot;
        return reservation;
    }

    private double TokensAt(DateTimeOffset time)
    {
        if (_tokensAt == DateTimeOffset.MinValue || time <= _tokensAt)
            return _tokensAt == DateTimeOffset.MinValue ? _burst : _tokens;
        return Math.Min(_burst, _tokens + (double)(time - _tokensAt).Ticks / _interval.Ticks);
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private void Release(Reservation reservation)
    {
        if (_lastSlot != reservation.Slot)
            return;
        _lastSlot = reservation.PreviousSlot;
        if (_tokensAt == reservation.Slot)
        {
            _tokens = reservation.PreviousTokens;
            _tokensAt = reservation.PreviousTokensAt;
        }
    }

    private readonly record struct Reservation(DateTimeOffset Slot, DateTimeOffset PreviousSlot, TimeSpan Wait, double PreviousTokens, DateTimeOffset PreviousTokensAt);
}
