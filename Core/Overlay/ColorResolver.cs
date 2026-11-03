using Iris.Core.Config;
using Iris.Core.Detection;

namespace Iris.Core.Overlay;

public sealed record OrgMembership(string? Sid, bool IsMain, string? Archetype = null, IReadOnlyList<string>? Activities = null)
{
    public bool IsRedacted => Sid is null;
    public IReadOnlyList<string> ActivityList => Activities ?? Array.Empty<string>();
}

public enum MatchKind
{
    PlayerList,
    OrgList,
    OrgAttribute,
    NoMatch,
}

public sealed record ColorMatch(MatchKind Kind, OverlayColor Color, string? DecidingOrgSid, string? DecidingAttribute);

public static class ColorResolver
{
    public static ColorMatch Resolve(string handle, IReadOnlyList<OrgMembership> orgs, SocialConfig config)
    {
        var player = config.Players.FirstOrDefault(p => LookAlikes.NamesEqual(p.Handle, handle));
        if (player is not null)
            return new ColorMatch(MatchKind.PlayerList, player.Color, null, null);

        OrgEntry? bestOrg = null;
        foreach (var row in config.Orgs)
        {
            bool member = orgs.Any(o => o.Sid is not null && string.Equals(o.Sid, row.Sid, StringComparison.OrdinalIgnoreCase));
            if (member && (bestOrg is null || row.Priority < bestOrg.Priority))
                bestOrg = row;
        }
        if (bestOrg is not null)
            return new ColorMatch(MatchKind.OrgList, bestOrg.Color, bestOrg.Sid, null);

        OrgAttributeRule? bestRule = null;
        string? ruleOrg = null;
        string? ruleText = null;
        foreach (var rule in config.OrgRules)
        {
            var (hit, sid, text) = RuleHits(rule, orgs);
            if (hit && (bestRule is null || rule.Priority < bestRule.Priority))
            {
                bestRule = rule;
                ruleOrg = sid;
                ruleText = text;
            }
        }
        if (bestRule is not null)
            return new ColorMatch(MatchKind.OrgAttribute, bestRule.Color, ruleOrg, ruleText);

        var noMatch = NoMatchRule.Find(config);
        return new ColorMatch(MatchKind.NoMatch, noMatch?.Color ?? NoMatchRule.DefaultColor, null, null);
    }

    public static bool IsDecidedByPlayerPage(string handle, IReadOnlyList<OrgMembership> orgs, SocialConfig config)
    {
        if (config.Players.Any(p => LookAlikes.NamesEqual(p.Handle, handle)))
            return true;
        if (config.Orgs.Any(row => orgs.Any(o => o.Sid is not null && string.Equals(o.Sid, row.Sid, StringComparison.OrdinalIgnoreCase))))
            return true;
        return !config.OrgRules.Any(rule => rule.Kind is AttributeKind.Archetype or AttributeKind.Activity);
    }

    public static ColorMatch ResolveUnknown(string handle, SocialConfig config)
    {
        var player = config.Players.FirstOrDefault(p => LookAlikes.NamesEqual(p.Handle, handle));
        if (player is not null)
            return new ColorMatch(MatchKind.PlayerList, player.Color, null, null);
        var noMatch = NoMatchRule.Find(config);
        return new ColorMatch(MatchKind.NoMatch, noMatch?.Color ?? NoMatchRule.DefaultColor, null, null);
    }

    private static (bool hit, string? sid, string? text) RuleHits(OrgAttributeRule rule, IReadOnlyList<OrgMembership> orgs)
    {
        switch (rule.Kind)
        {
            case AttributeKind.NoOrg:
                return (orgs.Count == 0, null, "No Org");
            case AttributeKind.Redacted:
                return (orgs.Any(o => o.IsRedacted), null, "REDACTED");
            case AttributeKind.NoMatch:
                return (false, null, null);
            case AttributeKind.Archetype:
                foreach (var o in OrderMainFirst(orgs))
                    if (o.Archetype is not null && string.Equals(o.Archetype, rule.Value, StringComparison.OrdinalIgnoreCase))
                        return (true, o.Sid, rule.Value);
                return (false, null, null);
            case AttributeKind.Activity:
                foreach (var o in OrderMainFirst(orgs))
                    if (o.ActivityList.Any(a => string.Equals(a, rule.Value, StringComparison.OrdinalIgnoreCase)))
                        return (true, o.Sid, rule.Value);
                return (false, null, null);
            default:
                return (false, null, null);
        }
    }

    private static IEnumerable<OrgMembership> OrderMainFirst(IReadOnlyList<OrgMembership> orgs) =>
        orgs.Where(o => o.IsMain).Concat(orgs.Where(o => !o.IsMain));
}
