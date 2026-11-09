namespace Iris.Core.Lookup;

public sealed class OrgFetchGate
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset NextAt;
        public bool Final;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool MayAsk(string sid, DateTimeOffset now)
    {
        lock (_lock)
            return !_states.TryGetValue(sid, out var state) || (!state.Final && now >= state.NextAt);
    }

    public void Succeeded(string sid) => Finish(sid);

    public void NotFound(string sid) => Finish(sid);

    public void Failed(string sid, DateTimeOffset now)
    {
        lock (_lock)
        {
            var state = Get(sid);
            state.Failures++;
            state.NextAt = now + state.Failures switch
            {
                1 => CacheSettings.OrgRetryFirst,
                2 => CacheSettings.OrgRetrySecond,
                _ => CacheSettings.OrgRetryAfterRepeatedFailures,
            };
        }
    }

    private void Finish(string sid)
    {
        lock (_lock)
            Get(sid).Final = true;
    }

    private State Get(string sid)
    {
        if (!_states.TryGetValue(sid, out var state))
            _states[sid] = state = new State();
        return state;
    }
}
