
namespace Iris.Core.Detection;

public sealed class O0EvidenceTable
{
    private readonly Dictionary<string, double[]> _totals = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public int KeyCount => _totals.Count;

    public static double Llr(double probabilityO)
    {
        if (double.IsNaN(probabilityO))
            return 0.0;
        if (probabilityO <= 0.0)
            return -NameplateDetectorSettings.EvidenceLlrClip;
        if (probabilityO >= 1.0)
            return NameplateDetectorSettings.EvidenceLlrClip;
        return Math.Max(-NameplateDetectorSettings.EvidenceLlrClip, Math.Min(NameplateDetectorSettings.EvidenceLlrClip, Math.Log(probabilityO / (1.0 - probabilityO))));
    }

    public double[]? TotalsOf(string name) =>
        _totals.TryGetValue(LookAlikes.FoldO0(name), out var totals) ? (double[])totals.Clone() : null;

    public string Resolve(string name, IReadOnlyList<O0Glyph>? glyphs)
    {
        var key = LookAlikes.FoldO0(name);
        if (!_totals.TryGetValue(key, out var totals))
        {
            if (_totals.Count >= NameplateDetectorSettings.EvidenceMaxKeys)
                _totals.Remove(_order.Dequeue());
            totals = new double[name.Length];
            _totals[key] = totals;
            _order.Enqueue(key);
        }

        if (glyphs is not null)
        {
            foreach (var glyph in glyphs)
            {
                if (glyph.Index >= 0 && glyph.Index < name.Length && name[glyph.Index] is 'O' or '0')
                    totals[glyph.Index] += Llr(glyph.ProbabilityO);
            }
        }

        char[]? resolved = null;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is not ('O' or '0'))
                continue;
            var total = totals[i];
            if (Math.Abs(total) < NameplateDetectorSettings.EvidenceDecideLlr)
                continue;
            var decided = total > 0 ? 'O' : '0';
            if (decided != name[i])
            {
                resolved ??= name.ToCharArray();
                resolved[i] = decided;
            }
        }

        return resolved is null ? name : new string(resolved);
    }
}
