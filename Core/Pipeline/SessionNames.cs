using Iris.Core.Detection;

namespace Iris.Core.Pipeline;

public sealed class SessionNames
{
    private readonly object _gate = new();
    private readonly HashSet<string> _notFound = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _byFold = new(StringComparer.Ordinal);

    public bool IsNotFound(string name)
    {
        lock (_gate)
            return _notFound.Contains(name);
    }

    public bool AddNotFound(string name)
    {
        lock (_gate)
            return _notFound.Add(name);
    }

    public void AddVerified(string name)
    {
        lock (_gate)
        {
            if (!_verified.Add(name))
                return;
            var fold = LookAlikes.FoldO0(name);
            _byFold[fold] = _byFold.TryGetValue(fold, out var other) && other != name ? null : name;
        }
    }

    public string SpellingOf(string name)
    {
        lock (_gate)
        {
            if (_verified.Contains(name))
                return name;
            return _byFold.TryGetValue(LookAlikes.FoldO0(name), out var spelling) && spelling is not null ? spelling : name;
        }
    }
}
