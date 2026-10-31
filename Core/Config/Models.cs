namespace Iris.Core.Config;

public enum OverlayColor
{
    Red,
    Orange,
    Yellow,
    Green,
    Cyan,
    Blue,
    Magenta,
    Purple,
}

public enum AttributeKind
{
    Archetype,

    Activity,

    Redacted,

    NoOrg,

    NoMatch,
}

public sealed record PlayerEntry(string Handle, OverlayColor Color);

public sealed record OrgEntry(string Sid, int Priority, OverlayColor Color);

public sealed record OrgAttributeRule(AttributeKind Kind, string? Value, int Priority, OverlayColor Color);

public sealed class SocialConfig
{
    public string? Name { get; set; }

    public List<PlayerEntry> Players { get; } = new();
    public List<OrgEntry> Orgs { get; } = new();
    public List<OrgAttributeRule> OrgRules { get; } = new();

    public int UserRuleCount => OrgRules.Count(r => r.Kind != AttributeKind.NoMatch);
}

public static class NoMatchRule
{
    public const int Priority = 10;

    public const OverlayColor DefaultColor = OverlayColor.Blue;

    public static OrgAttributeRule CreateDefault() => new(AttributeKind.NoMatch, null, Priority, DefaultColor);

    public static OrgAttributeRule? Find(SocialConfig config) =>
        config.OrgRules.FirstOrDefault(r => r.Kind == AttributeKind.NoMatch);

    public static void Ensure(SocialConfig config)
    {
        var keep = Find(config) ?? CreateDefault();
        config.OrgRules.RemoveAll(r => r.Kind == AttributeKind.NoMatch);
        config.OrgRules.Add(keep with { Value = null, Priority = Priority });
    }

    public static SocialConfig Normalized(SocialConfig config)
    {
        var copy = new SocialConfig { Name = config.Name };
        copy.Players.AddRange(config.Players);
        copy.Orgs.AddRange(config.Orgs);
        copy.OrgRules.AddRange(config.OrgRules);
        Ensure(copy);
        return copy;
    }
}
