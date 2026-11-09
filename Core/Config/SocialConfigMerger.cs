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
    public static MergeResult Merge(SocialConfig original, SocialConfig imported, bool importedWins = false)
    {
        var winner = importedWins ? imported : original;
        var merged = new SocialConfig { Name = original.Name, OrgsMainOrgOnly = winner.OrgsMainOrgOnly, OrgRulesMainOrgOnly = winner.OrgRulesMainOrgOnly };
        var importedPlayers = importedWins ? imported.Players.GroupBy(p => p.Handle, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) : null;
        var importedOrgs = importedWins ? imported.Orgs.GroupBy(o => o.Sid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) : null;
        var importedRules = importedWins
            ? imported.OrgRules.Where(r => r.Kind != AttributeKind.NoMatch).GroupBy(RuleKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : null;
        var result = new MergeResult(merged);

        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in original.Players)
        {
            merged.Players.Add(importedPlayers is not null && importedPlayers.TryGetValue(p.Handle, out var newer) ? newer : p);
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
            merged.Orgs.Add(importedOrgs is not null && importedOrgs.TryGetValue(o.Sid, out var newer) ? newer : o);
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
            merged.OrgRules.Add(importedRules is not null && importedRules.TryGetValue(RuleKey(r), out var newer) ? newer : r);
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

        merged.OrgRules.Add(NoMatchRule.Find(winner) ?? NoMatchRule.Find(importedWins ? original : imported) ?? NoMatchRule.CreateDefault());
        NoMatchRule.Ensure(merged);

        return result;
    }

    private static string RuleKey(OrgAttributeRule rule) => OrgRuleValues.RuleKey(rule);
}
