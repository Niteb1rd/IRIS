using Iris.Core.Config;

namespace Iris.Core.Overlay;

public static class PanelMotion
{
    public const double ScrimFadeMs = 150;

    public const double SlideMs = 200;

    public const double BodyFadeStartMs = 60;

    public const double BodyFadeEndMs = 220;

    public const double BodyRisePx = 8;

    public const double RowStartMs = 120;

    public const double RowStaggerMs = 20;

    public const double RowFlickerMs = 60;

    public const int RowStaggerMax = 15;

    public const double DossierMs = 200;

    public const double CloseMs = 150;

    public const double EntryAppearMs = 180;

    public const double EntryRisePx = 6;

    public const double EntryLeaveMs = 150;

    public const double PulseMs = 250;

    public const double PulseWidthGain = 1.0;

    public static double Flicker(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t < 1.0 / 3)
            return t * 3;
        if (t < 2.0 / 3)
            return 1 - (t - 1.0 / 3) * 3 * 0.45;
        return 0.55 + (t - 2.0 / 3) * 3 * 0.45;
    }

    public static double EaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var u = 1 - t;
        return 1 - u * u * u;
    }

    public static double RowBeginMs(int index) => RowStartMs + RowStaggerMs * Math.Clamp(index, 0, RowStaggerMax);
}

public readonly record struct EntryLook(double Opacity, double DropPx, double EdgeGain)
{
    public static readonly EntryLook Still = new(1, 0, 0);
}

public sealed class PanelEntryMotion
{
    private readonly Dictionary<string, Shown> _shown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Leaving> _leaving = new();
    private IReadOnlyList<PanelEntry> _last = Array.Empty<PanelEntry>();
    private IReadOnlyList<PanelEntry> _laidOut = Array.Empty<PanelEntry>();
    private bool _settled = true;

    private sealed record Shown(long SinceMs, OverlayColor Color, long ColourChangedMs);

    private sealed record Leaving(PanelEntry Entry, int Index, long SinceMs);

    public IReadOnlyList<PanelEntry> Entries(IReadOnlyList<PanelEntry> entries, long nowMs)
    {
        if (_settled || !ReferenceEquals(entries, _last))
            Note(entries, nowMs);
        _leaving.RemoveAll(l => nowMs - l.SinceMs >= PanelMotion.EntryLeaveMs);
        if (_leaving.Count == 0)
            return _laidOut = entries;
        var merged = new List<PanelEntry>(entries);
        foreach (var gone in _leaving.OrderBy(l => l.Index))
            merged.Insert(Math.Min(gone.Index, merged.Count), gone.Entry);
        return _laidOut = merged;
    }

    private void Note(IReadOnlyList<PanelEntry> entries, long nowMs)
    {
        var appearAt = _settled ? nowMs - (long)PanelMotion.EntryAppearMs : nowMs;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            present.Add(entry.Handle);
            _leaving.RemoveAll(l => string.Equals(l.Entry.Handle, entry.Handle, StringComparison.OrdinalIgnoreCase));
            if (!_shown.TryGetValue(entry.Handle, out var known))
                _shown[entry.Handle] = new Shown(appearAt, entry.Color, long.MinValue);
            else if (known.Color != entry.Color)
                _shown[entry.Handle] = known with { Color = entry.Color, ColourChangedMs = _settled ? long.MinValue : nowMs };
        }

        if (!_settled)
        {
            for (var i = 0; i < _laidOut.Count; i++)
            {
                var old = _laidOut[i];
                if (!present.Contains(old.Handle) && _leaving.All(l => !string.Equals(l.Entry.Handle, old.Handle, StringComparison.OrdinalIgnoreCase)))
                    _leaving.Add(new Leaving(old, i, nowMs));
            }
        }

        foreach (var handle in _shown.Keys.Where(h => !present.Contains(h)).ToList())
            _shown.Remove(handle);
        _last = entries;
        _settled = false;
    }

    public EntryLook LookOf(PanelEntry entry, long nowMs)
    {
        foreach (var gone in _leaving)
        {
            if (ReferenceEquals(gone.Entry, entry))
                return new EntryLook(1 - Math.Clamp((nowMs - gone.SinceMs) / PanelMotion.EntryLeaveMs, 0, 1), 0, 0);
        }

        if (!_shown.TryGetValue(entry.Handle, out var shown))
            return EntryLook.Still;
        var appear = (nowMs - shown.SinceMs) / PanelMotion.EntryAppearMs;
        var opacity = appear >= 1 ? 1 : PanelMotion.Flicker(appear);
        var drop = appear >= 1 ? 0 : PanelMotion.EntryRisePx * (1 - PanelMotion.EaseOut(appear));
        var pulse = shown.ColourChangedMs == long.MinValue ? 1 : (nowMs - shown.ColourChangedMs) / PanelMotion.PulseMs;
        var gain = pulse is >= 0 and < 1 ? PanelMotion.PulseWidthGain * Math.Sin(Math.PI * pulse) : 0;
        return new EntryLook(opacity, drop, gain);
    }

    public bool IsMoving(long nowMs)
    {
        if (_leaving.Any(l => nowMs - l.SinceMs < PanelMotion.EntryLeaveMs))
            return true;
        foreach (var shown in _shown.Values)
        {
            if (nowMs - shown.SinceMs < PanelMotion.EntryAppearMs)
                return true;
            if (shown.ColourChangedMs != long.MinValue && nowMs - shown.ColourChangedMs < PanelMotion.PulseMs)
                return true;
        }
        return false;
    }

    public void Settle()
    {
        _leaving.Clear();
        _shown.Clear();
        _last = Array.Empty<PanelEntry>();
        _laidOut = Array.Empty<PanelEntry>();
        _settled = true;
    }
}
