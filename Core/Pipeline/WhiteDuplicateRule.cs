using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class WhiteDuplicateRule
{
    private sealed class Sighting
    {
        public readonly Dictionary<long, string> Names = new();
        public PixelRect Box;
    }

    private readonly Dictionary<int, Sighting> _plates = new();
    private readonly int _window;

    public WhiteDuplicateRule(int windowTicks) => _window = windowTicks;

    public void Forget(int plateId) => _plates.Remove(plateId);

    public void Clear() => _plates.Clear();

    public int? Note(int plateId, string name, long tick, bool white, PixelRect box)
    {
        if (!white)
            return null;

        if (!_plates.TryGetValue(plateId, out var mine))
            _plates[plateId] = mine = new Sighting();
        mine.Names[tick] = name;
        mine.Box = box;
        foreach (var stale in mine.Names.Keys.Where(k => k < tick - _window).ToList())
            mine.Names.Remove(stale);

        foreach (var (otherId, other) in _plates)
        {
            if (otherId == plateId)
                continue;
            var shared = false;
            for (var n = tick - _window; n <= tick && !shared; n++)
                shared = other.Names.TryGetValue(n, out var otherName) && otherName == name && mine.Names.TryGetValue(n, out var myName) && myName == name;
            if (!shared || Overlaps(other.Box, box))
                continue;
            return otherId;
        }

        return null;
    }

    private static bool Overlaps(PixelRect a, PixelRect b) =>
        Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 0 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > 0;
}
