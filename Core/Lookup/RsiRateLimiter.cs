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
    private readonly int _burst;

    private TimeSpan _gap;

    private DateTimeOffset _nextFree = DateTimeOffset.MinValue;

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    private DateTimeOffset _burstOffUntil = DateTimeOffset.MinValue;

    private long _generation;

    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;

    private static readonly TimeSpan ClockStepTolerance = TimeSpan.FromSeconds(1);

    public RsiRateLimiter(IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? interval = null, int? burst = null)
    {
        _clock = clock;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _interval = interval ?? CacheSettings.RsiDefaultInterval;
        _burst = Math.Max(1, burst ?? CacheSettings.RsiLiveBurst);
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

    public int AvailableNow(bool allowBurst = false)
    {
        lock (_gate)
        {
            var now = Now();
            if (_notBefore > now)
                return 0;
            var sustained = _nextFree > now ? _nextFree : now;
            var latest = now + TimeSpan.FromTicks(_gap.Ticks * SpareTurns(now, allowBurst));
            return sustained > latest ? 0 : 1 + (int)((latest - sustained).Ticks / _gap.Ticks);
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

    public BackoffChange ReportThrottled(TimeSpan? retryAfter = null, long? turn = null)
    {
        lock (_gate)
        {
            var before = _gap;
            var alreadyCounted = turn is { } sentIn && sentIn != _generation;
            if (!alreadyCounted)
            {
                _generation++;
                var doubled = TimeSpan.FromTicks(_gap.Ticks * 2);
                _gap = doubled > CacheSettings.RsiMaxBackoff ? CacheSettings.RsiMaxBackoff : doubled;
                if (_gap < _interval)
                    _gap = _interval;
            }

            var wait = _gap;
            if (retryAfter is { } asked && asked > wait)
                wait = asked > CacheSettings.RsiMaxRetryAfter ? CacheSettings.RsiMaxRetryAfter : asked;

            var now = Now();
            var until = now + wait;
            if (until > _notBefore)
                _notBefore = until;
            if (until > _nextFree)
                _nextFree = until;

            var burstOff = now + (wait > CacheSettings.RsiBurstCooldown ? wait : CacheSettings.RsiBurstCooldown);
            if (burstOff > _burstOffUntil)
                _burstOffUntil = burstOff;
            return new BackoffChange(before, _gap, wait);
        }
    }

    public async Task<long> WaitForTurnAsync(CancellationToken ct = default, bool allowBurst = false)
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

                reservation = Reserve(now, allowBurst);
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
        var stepBack = _lastSeen - now;
        if (_lastSeen != DateTimeOffset.MinValue && stepBack > ClockStepTolerance)
        {
            _nextFree = Earlier(_nextFree, stepBack);
            _notBefore = Earlier(_notBefore, stepBack);
            _burstOffUntil = Earlier(_burstOffUntil, stepBack);
            _lastSeen = now;
        }
        else if (now > _lastSeen)
        {
            _lastSeen = now;
        }

        return now;
    }

    private static DateTimeOffset Earlier(DateTimeOffset at, TimeSpan by) => at == DateTimeOffset.MinValue ? at : at - by;

    private Reservation Reserve(DateTimeOffset now, bool allowBurst)
    {
        var sustained = _nextFree > now ? _nextFree : now;
        var slot = sustained - TimeSpan.FromTicks(_gap.Ticks * SpareTurns(now, allowBurst));
        if (slot < now)
            slot = now;
        if (_notBefore > slot)
            slot = _notBefore;

        var reservation = new Reservation(_nextFree, sustained + _gap, slot - now);
        _nextFree = reservation.NextFreeAfter;
        return reservation;
    }

    private int SpareTurns(DateTimeOffset now, bool allowBurst) =>
        allowBurst && _gap == _interval && now >= _burstOffUntil ? _burst - 1 : 0;

    private void Release(Reservation reservation)
    {
        if (_nextFree == reservation.NextFreeAfter)
            _nextFree = reservation.NextFreeBefore;
    }

    private readonly record struct Reservation(DateTimeOffset NextFreeBefore, DateTimeOffset NextFreeAfter, TimeSpan Wait);
}
