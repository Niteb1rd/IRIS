namespace Iris.Core.Lookup;

public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private sealed class Entry
    {
        public Entry(TKey key, TValue value, DateTimeOffset lastUse)
        {
            Key = key;
            Value = value;
            LastUse = lastUse;
        }

        public TKey Key { get; }
        public TValue Value { get; set; }
        public DateTimeOffset LastUse { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly TimeSpan? _idle;
    private readonly IClock _clock;

    public LruCache(int capacity, TimeSpan? idle = null, IClock? clock = null, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be at least 1.");
        Capacity = capacity;
        _idle = idle;
        _clock = clock ?? SystemClock.Instance;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _map.Count;
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (_map.TryGetValue(key, out var node))
            {
                if (!IsExpired(node.Value, now))
                {
                    node.Value.LastUse = now;
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }

                Remove(node);
            }
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (_map.TryGetValue(key, out var node))
            {
                node.Value.Value = value;
                node.Value.LastUse = now;
                _order.Remove(node);
                _order.AddFirst(node);
            }
            else
            {
                _map[key] = _order.AddFirst(new Entry(key, value, now));
            }

            DropExpired(now);
            while (_map.Count > Capacity && _order.Last is { } oldest)
                Remove(oldest);
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
                return false;
            Remove(node);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    public IReadOnlyList<KeyValuePair<TKey, TValue>> Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            var list = new List<KeyValuePair<TKey, TValue>>(_map.Count);
            foreach (var entry in _order)
                if (!IsExpired(entry, now))
                    list.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
            return list;
        }
    }

    private bool IsExpired(Entry entry, DateTimeOffset now) => _idle is { } idle && now - entry.LastUse >= idle;

    private void DropExpired(DateTimeOffset now)
    {
        if (_idle is null)
            return;
        while (_order.Last is { } last && IsExpired(last.Value, now))
            Remove(last);
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _map.Remove(node.Value.Key);
    }
}
