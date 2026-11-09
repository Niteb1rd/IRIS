namespace Iris.Core.Config;

public sealed record AttributeTarget(AttributeKind Kind, string? Value = null);

public static class SocialListEdits
{
    public static PlayerEntry? PlayerRow(SocialConfig config, string handle) =>
        config.Players.FirstOrDefault(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));

    public static OrgEntry? OrgRow(SocialConfig config, string sid) =>
        config.Orgs.FirstOrDefault(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));

    public static OrgAttributeRule? RuleRow(SocialConfig config, AttributeTarget target) =>
        config.OrgRules.FirstOrDefault(r => r.Kind == target.Kind
            && (!OrgRuleValues.NeedsValue(target.Kind) || string.Equals((r.Value ?? string.Empty).Trim(), (target.Value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)));

    public static SocialConfig SetPlayerColor(SocialConfig config, string handle, OverlayColor color)
    {
        var copy = Copy(config);
        int i = copy.Players.FindIndex(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            copy.Players[i] = copy.Players[i] with { Color = color };
        else
            copy.Players.Add(new PlayerEntry(handle, color));
        return copy;
    }

    public static SocialConfig RemovePlayer(SocialConfig config, string handle)
    {
        var copy = Copy(config);
        copy.Players.RemoveAll(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));
        return copy;
    }

    public static SocialConfig SetOrgColor(SocialConfig config, string sid, OverlayColor color)
    {
        var copy = Copy(config);
        int i = copy.Orgs.FindIndex(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            copy.Orgs[i] = copy.Orgs[i] with { Color = color };
        else
            copy.Orgs.Add(new OrgEntry(sid, ColorRanks.Unranked, color));
        return KeepRankings(config, copy);
    }

    public static SocialConfig SetOrgColorRank(SocialConfig config, string sid, int rank)
    {
        var row = OrgRow(config, sid);
        if (row is null)
            return Copy(config);
        var ranking = MoveTo(ColorRanks.OrgRanking(config), row.Color, rank);
        return ColorRanks.WithRankings(config, ranking, ColorRanks.RuleRanking(config));
    }

    public static SocialConfig RemoveOrg(SocialConfig config, string sid)
    {
        var copy = Copy(config);
        copy.Orgs.RemoveAll(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));
        return ColorRanks.Normalize(copy).Config;
    }

    public static SocialConfig SetRuleColor(SocialConfig config, AttributeTarget target, OverlayColor color)
    {
        var copy = Copy(config);
        int i = IndexOfRule(copy, target);
        if (i >= 0)
            copy.OrgRules[i] = copy.OrgRules[i] with { Color = color };
        else if (target.Kind == AttributeKind.NoMatch)
            copy.OrgRules.Add(NoMatchRule.CreateDefault() with { Color = color });
        else
            copy.OrgRules.Insert(InsertAt(copy), new OrgAttributeRule(target.Kind, OrgRuleValues.NeedsValue(target.Kind) ? target.Value : null, ColorRanks.Unranked, color));
        NoMatchRule.Ensure(copy);
        return KeepRankings(config, copy);
    }

    public static SocialConfig SetRuleColorRank(SocialConfig config, AttributeTarget target, int rank)
    {
        var row = RuleRow(config, target);
        if (row is null || target.Kind == AttributeKind.NoMatch)
            return Copy(config);
        var ranking = MoveTo(ColorRanks.RuleRanking(config), row.Color, rank);
        return ColorRanks.WithRankings(config, ColorRanks.OrgRanking(config), ranking);
    }

    public static SocialConfig RemoveRule(SocialConfig config, AttributeTarget target)
    {
        var copy = Copy(config);
        if (target.Kind == AttributeKind.NoMatch)
            return copy;
        int i = IndexOfRule(copy, target);
        if (i >= 0)
            copy.OrgRules.RemoveAt(i);
        return ColorRanks.Normalize(copy).Config;
    }

    public static SocialConfig Copy(SocialConfig config)
    {
        var copy = new SocialConfig
        {
            Name = config.Name,
            IrisVersion = config.IrisVersion,
            OrgsMainOrgOnly = config.OrgsMainOrgOnly,
            OrgRulesMainOrgOnly = config.OrgRulesMainOrgOnly,
        };
        copy.Players.AddRange(config.Players);
        copy.Orgs.AddRange(config.Orgs);
        copy.OrgRules.AddRange(config.OrgRules);
        return copy;
    }

    internal static IReadOnlyList<OverlayColor> MoveTo(IReadOnlyList<OverlayColor> ranking, OverlayColor color, int rank)
    {
        return ColorRanks.Move(ranking, color, rank);
    }

    private static SocialConfig KeepRankings(SocialConfig before, SocialConfig edited) =>
        ColorRanks.WithRankings(edited, ColorRanks.OrgRanking(before), ColorRanks.RuleRanking(before));

    private static int IndexOfRule(SocialConfig config, AttributeTarget target)
    {
        var row = RuleRow(config, target);
        return row is null ? -1 : config.OrgRules.IndexOf(row);
    }

    private static int InsertAt(SocialConfig config)
    {
        int noMatch = config.OrgRules.FindIndex(r => r.Kind == AttributeKind.NoMatch);
        return noMatch >= 0 ? noMatch : config.OrgRules.Count;
    }
}
