using Iris.Core.Detection;

namespace Iris.Core.Pipeline;

public readonly record struct ResolvedName(string Name, bool Decided);

public interface INameResolver
{
    ResolvedName Resolve(PlateState plate, string name, IReadOnlyList<O0Glyph> glyphs);
}

public sealed class EvidenceNameResolver : INameResolver
{
    public ResolvedName Resolve(PlateState plate, string name, IReadOnlyList<O0Glyph> glyphs)
    {
        var resolved = plate.Evidence.Resolve(name, glyphs);
        return new ResolvedName(resolved, IsDecided(plate.Evidence, resolved));
    }

    public static bool IsDecided(O0EvidenceTable evidence, string name)
    {
        double[]? totals = null;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is not ('O' or '0'))
                continue;
            totals ??= evidence.TotalsOf(name);
            if (totals is null || i >= totals.Length || Math.Abs(totals[i]) < NameplateDetectorSettings.EvidenceDecideLlr)
                return false;
        }

        return true;
    }
}
