namespace Iris.Core.Config;

public sealed class MergeResult
{
    public MergeResult(SocialConfig config)
    {
        Config = config;
    }

    public SocialConfig Config { get; }

    public int AddedPlayers { get; internal set; }
    public int AddedOrgs { get; internal set; }
    public int AddedRules { get; internal set; }

    public int SkippedPlayers { get; internal set; }
    public int SkippedOrgs { get; internal set; }
    public int SkippedRules { get; internal set; }

    public int SkippedDuplicates => SkippedPlayers + SkippedOrgs + SkippedRules;
}

public static class SocialConfigMerger
{
    public static MergeResult Merge(SocialConfig original, SocialConfig imported)
    {
        var merged = new SocialConfig { Name = original.Name };
        var result = new MergeResult(merged);

        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in original.Players)
        {
            merged.Players.Add(p);
            handles.Add(p.Handle);
        }
        foreach (var p in imported.Players)
        {
            if (handles.Add(p.Handle))
            {
                merged.Players.Add(p);
                result.AddedPlayers++;
            }
            else
            {
                result.SkippedPlayers++;
            }
        }

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in original.Orgs)
        {
            merged.Orgs.Add(o);
            sids.Add(o.Sid);
        }
        foreach (var o in imported.Orgs)
        {
            if (sids.Add(o.Sid))
            {
                merged.Orgs.Add(o);
                result.AddedOrgs++;
            }
            else
            {
                result.SkippedOrgs++;
            }
        }

        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in original.OrgRules.Where(r => r.Kind != AttributeKind.NoMatch))
        {
            merged.OrgRules.Add(r);
            rules.Add(RuleKey(r));
        }
        foreach (var r in imported.OrgRules.Where(r => r.Kind != AttributeKind.NoMatch))
        {
            if (rules.Add(RuleKey(r)))
            {
                merged.OrgRules.Add(r);
                result.AddedRules++;
            }
            else
            {
                result.SkippedRules++;
            }
        }

        merged.OrgRules.Add(NoMatchRule.Find(original) ?? NoMatchRule.Find(imported) ?? NoMatchRule.CreateDefault());
        NoMatchRule.Ensure(merged);

        return result;
    }

    private static string RuleKey(OrgAttributeRule rule)
    {
        var hasValue = rule.Kind is AttributeKind.Archetype or AttributeKind.Activity;
        return rule.Kind + "|" + (hasValue ? (rule.Value ?? string.Empty).Trim() : string.Empty);
    }
}
