namespace Iris.Core.Detection;

public sealed class SessionExclusions
{
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public bool Contains(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        lock (_gate)
            return _names.Contains(name.Trim());
    }

    public bool TryAdd(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        lock (_gate)
            return _names.Add(name.Trim());
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _names.Count;
        }
    }
}
