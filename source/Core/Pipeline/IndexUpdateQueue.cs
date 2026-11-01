using System.Collections.Concurrent;

namespace Iris.Core.Pipeline;

public sealed class IndexUpdateQueue : IUpdateSink, IDisposable
{
    private readonly ConcurrentQueue<IReadOnlyList<IndexUpdate>> _batches = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly MarkerIndex _index;
    private readonly IMonotonicClock _clock;
    private readonly List<IndexUpdate> _merged = new();
    private bool _disposed;

    public IndexUpdateQueue(MarkerIndex index, IMonotonicClock clock)
    {
        _index = index;
        _clock = clock;
    }

    public WaitHandle Handle => _signal;

    public void Submit(IReadOnlyList<IndexUpdate> updates)
    {
        if (updates.Count == 0 || _disposed)
            return;
        _batches.Enqueue(updates);
        _signal.Set();
    }

    public void Wake()
    {
        if (!_disposed)
            _signal.Set();
    }

    public bool Wait(int timeoutMs) => !_disposed && _signal.WaitOne(timeoutMs);

    public IndexSnapshot? Pump()
    {
        _merged.Clear();
        while (_batches.TryDequeue(out var batch))
            _merged.AddRange(batch);
        if (_merged.Count == 0)
            return null;
        return _index.Apply(_merged, _clock.NowMs);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _signal.Dispose();
    }
}
