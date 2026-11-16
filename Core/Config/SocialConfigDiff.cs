namespace Iris.Core.Config;

public sealed record TabChanges(int Added, int Removed, int Recoloured)
{
    public bool Any => Added + Removed + Recoloured > 0;
}

public sealed record SocialConfigDiff(TabChanges Players, TabChanges Orgs, TabChanges Rules, bool RanksChanged = false, bool MainOrgOnlyChanged = false)
{
    public static SocialConfigDiff Compare(SocialConfig current, SocialConfig after) => new(
        Count(current.Players, after.Players, p => p.Handle, p => p.Color),
        Count(current.Orgs, after.Orgs, o => o.Sid, o => o.Color),
        Count(current.OrgRules, after.OrgRules, OrgRuleValues.RuleKey, r => r.Color),
        RanksChanged: !SameOrder(ColorRanks.OrgRanking(current), ColorRanks.OrgRanking(after))
            || !SameOrder(ColorRanks.RuleRanking(current), ColorRanks.RuleRanking(after)),
        MainOrgOnlyChanged: current.OrgsMainOrgOnly != after.OrgsMainOrgOnly || current.OrgRulesMainOrgOnly != after.OrgRulesMainOrgOnly);

    public bool Any => Players.Any || Orgs.Any || Rules.Any || RanksChanged || MainOrgOnlyChanged;

    public string Describe()
    {
        var parts = new List<string>();
        Add(parts, Players, "player", "players");
        Add(parts, Orgs, "org", "orgs");
        Add(parts, Rules, "org-attribute rule", "org-attribute rules");
        if (RanksChanged)
            parts.Add("color ranking changed");
        if (MainOrgOnlyChanged)
            parts.Add("\"Only check main org\" changed");
        return parts.Count == 0 ? "no changes" : string.Join("; ", parts);
    }

    private static bool SameOrder(IReadOnlyList<OverlayColor> before, IReadOnlyList<OverlayColor> after)
    {
        var shared = new HashSet<OverlayColor>(before);
        shared.IntersectWith(after);
        return before.Where(shared.Contains).SequenceEqual(after.Where(shared.Contains));
    }

    private static void Add(List<string> parts, TabChanges changes, string one, string many)
    {
        if (!changes.Any)
            return;
        var bits = new List<string>();
        var named = false;
        void Bit(int count, string what)
        {
            if (count == 0)
                return;
            bits.Add(named ? $"{count} {what}" : $"{count} {(count == 1 ? one : many)} {what}");
            named = true;
        }
        Bit(changes.Added, "added");
        Bit(changes.Removed, "removed");
        Bit(changes.Recoloured, "recolored");
        parts.Add(string.Join(", ", bits));
    }

    private static TabChanges Count<T>(IEnumerable<T> current, IEnumerable<T> after, Func<T, string> key, Func<T, OverlayColor> color)
    {
        var before = FirstByKey(current, key);
        var later = FirstByKey(after, key);
        var added = later.Keys.Count(k => !before.ContainsKey(k));
        var removed = before.Keys.Count(k => !later.ContainsKey(k));
        var recoloured = later.Count(pair => before.TryGetValue(pair.Key, out var old) && color(old) != color(pair.Value));
        return new TabChanges(added, removed, recoloured);
    }

    private static Dictionary<string, T> FirstByKey<T>(IEnumerable<T> rows, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            map.TryAdd(key(row), row);
        return map;
    }
}
