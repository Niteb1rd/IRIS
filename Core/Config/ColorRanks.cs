namespace Iris.Core.Config;

public sealed record RankFix(SocialConfig Config, bool OrgsFixed, bool RulesFixed)
{
    public bool Fixed => OrgsFixed || RulesFixed;
}

public static class ColorRanks
{
    public const int Unranked = 100;

    public static IReadOnlyList<OverlayColor> Of(IEnumerable<(OverlayColor Color, int Priority)> rows)
    {
        var best = new Dictionary<OverlayColor, (int Priority, int First)>();
        var index = 0;
        foreach (var (color, priority) in rows)
        {
            if (best.TryGetValue(color, out var known))
                best[color] = (Math.Min(known.Priority, priority), known.First);
            else
                best[color] = (priority, index);
            index++;
        }

        return best.OrderBy(kv => kv.Value.Priority).ThenBy(kv => kv.Value.First).Select(kv => kv.Key).ToList();
    }

    public static bool HasConflict(IEnumerable<(OverlayColor Color, int Priority)> rows)
    {
        var byColor = new Dictionary<OverlayColor, int>();
        var byPriority = new Dictionary<int, OverlayColor>();
        foreach (var (color, priority) in rows)
        {
            if (byColor.TryGetValue(color, out var p) && p != priority)
                return true;
            if (byPriority.TryGetValue(priority, out var c) && c != color)
                return true;
            byColor[color] = priority;
            byPriority[priority] = color;
        }

        return false;
    }

    public static IReadOnlyList<OverlayColor> Sync(IReadOnlyList<OverlayColor> ranking, IEnumerable<OverlayColor> used)
    {
        var inUse = used.Distinct().ToList();
        var result = ranking.Where(inUse.Contains).ToList();
        result.AddRange(inUse.Where(c => !result.Contains(c)));
        return result;
    }

    public static IReadOnlyList<OverlayColor> Combine(IReadOnlyList<OverlayColor> first, IReadOnlyList<OverlayColor> second)
    {
        var result = first.ToList();
        result.AddRange(second.Where(c => !result.Contains(c)));
        return result;
    }

    public static IReadOnlyList<OverlayColor> Move(IReadOnlyList<OverlayColor> ranking, OverlayColor color, int rank)
    {
        var result = ranking.ToList();
        if (!result.Remove(color))
            return ranking;
        result.Insert(Math.Clamp(rank - 1, 0, result.Count), color);
        return result;
    }

    public static int RankOf(IReadOnlyList<OverlayColor> ranking, OverlayColor color)
    {
        for (var i = 0; i < ranking.Count; i++)
            if (ranking[i] == color)
                return i + 1;
        return Unranked;
    }

    public static IReadOnlyList<OverlayColor> OrgRanking(SocialConfig config) => Of(config.Orgs.Select(o => (o.Color, o.Priority)));

    public static IReadOnlyList<OverlayColor> RuleRanking(SocialConfig config) => Of(UserRules(config).Select(r => (r.Color, r.Priority)));

    public static SocialConfig WithRankings(SocialConfig config, IReadOnlyList<OverlayColor> orgRanking, IReadOnlyList<OverlayColor> ruleRanking)
    {
        var orgs = Sync(orgRanking, config.Orgs.Select(o => o.Color));
        var rules = Sync(ruleRanking, UserRules(config).Select(r => r.Color));
        var copy = SocialListEdits.Copy(config);
        for (var i = 0; i < copy.Orgs.Count; i++)
            copy.Orgs[i] = copy.Orgs[i] with { Priority = RankOf(orgs, copy.Orgs[i].Color) };
        for (var i = 0; i < copy.OrgRules.Count; i++)
        {
            var rule = copy.OrgRules[i];
            copy.OrgRules[i] = rule with { Priority = rule.Kind == AttributeKind.NoMatch ? NoMatchRule.Priority : RankOf(rules, rule.Color) };
        }

        return copy;
    }

    public static RankFix Normalize(SocialConfig config)
    {
        var orgRows = config.Orgs.Select(o => (o.Color, o.Priority)).ToList();
        var ruleRows = UserRules(config).Select(r => (r.Color, r.Priority)).ToList();
        var fixedConfig = WithRankings(config, Of(orgRows), Of(ruleRows));
        return new RankFix(fixedConfig, HasConflict(orgRows), HasConflict(ruleRows));
    }

    private static IEnumerable<OrgAttributeRule> UserRules(SocialConfig config) => config.OrgRules.Where(r => r.Kind != AttributeKind.NoMatch);
}
