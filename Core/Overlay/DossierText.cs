using Iris.Core.Config;

namespace Iris.Core.Overlay;

public sealed record DossierOrgBox(string Title, string? FullName, string? Archetype, string? Primary, string? Secondary, string? Sid, AttributeTarget? Target);

public readonly record struct DossierPlacement(double Left, double Top, double Width, double Bottom)
{
    public bool IsEmpty => Width <= 0 || Bottom <= Top;
}

public static class DossierText
{
    public const double PanelGapShare = 0.10;

    public const double LeftMarginShare = 0.05;

    public const string NoCitizenRecord = "n/a";

    public static IReadOnlyList<DossierOrgBox> OrgBoxes(IReadOnlyList<OrgMembership> orgs)
    {
        var boxes = new List<DossierOrgBox>();
        var main = orgs.FirstOrDefault(o => o.IsMain);
        if (main is null)
            boxes.Add(new DossierOrgBox("Main: NONE", null, null, null, null, null, new AttributeTarget(AttributeKind.NoOrg)));
        else
            boxes.Add(Box(main, "Main"));
        foreach (var affiliate in orgs.Where(o => !o.IsMain))
            boxes.Add(Box(affiliate, "Affiliate"));
        return boxes;
    }

    private static DossierOrgBox Box(OrgMembership org, string prefix)
    {
        if (org.Sid is null)
        {
            return org.IsMain
                ? new DossierOrgBox("Main: HIDDEN", null, null, null, null, null, new AttributeTarget(AttributeKind.HiddenMainOrg))
                : new DossierOrgBox("Affiliate: REDACTED", null, null, null, null, null, new AttributeTarget(AttributeKind.Redacted));
        }

        var activities = org.ActivityList;
        return new DossierOrgBox($"{prefix}: {org.Sid}", org.Name,
            org.Archetype, activities.Count > 0 ? activities[0] : null, activities.Count > 1 ? activities[1] : null, org.Sid, null);
    }

    public static string ClassificationFrom(MatchKind kind, ColorMatch? match, SocialConfig config) => kind switch
    {
        MatchKind.PlayerList => "Named List",
        MatchKind.OrgList => match?.DecidingOrgSid is { Length: > 0 } sid ? $"Org Affiliation ({sid})" : "Org Affiliation",
        MatchKind.OrgAttribute => $"Org-Attribute ({AttributeText(match?.DecidingAttribute, config)})",
        _ => "No match",
    };

    private static string AttributeText(string? text, SocialConfig config)
    {
        if (string.IsNullOrEmpty(text))
            return "?";
        foreach (var rule in config.OrgRules)
        {
            if (rule.Kind is AttributeKind.Archetype or AttributeKind.Activity
                && string.Equals((rule.Value ?? string.Empty).Trim(), text, StringComparison.OrdinalIgnoreCase))
                return (rule.Kind == AttributeKind.Archetype ? "Archetype: " : "Activity: ") + text;
        }
        return text;
    }

    public static DossierPlacement Place(double windowWidth, double panelLeft, double panelTop, double panelBottom, int widthPercent)
    {
        var right = panelLeft - windowWidth * PanelGapShare;
        var width = windowWidth * widthPercent / 100.0;
        return right <= 0 || width <= 0 ? new DossierPlacement(0, panelTop, 0, panelBottom) : new DossierPlacement(right - width, panelTop, width, panelBottom);
    }

    public static (double Left, double Top, double Width, double Bottom) PanelArea(AppSettings settings, double windowWidth, double windowHeight)
    {
        var width = windowWidth * settings.PanelWidthPercent / 100.0;
        var right = windowWidth - windowWidth * settings.PanelRightGapPercent / 100.0;
        var top = windowHeight * settings.PanelTopGapPercent / 100.0;
        return (Math.Max(0, right - width), top, width, windowHeight * (1 - PanelLayout.BottomReservePercent / 100.0));
    }

    public static DossierPlacement PlaceFor(AppSettings settings, double windowWidth, double windowHeight)
    {
        var (left, top, _, bottom) = PanelArea(settings, windowWidth, windowHeight);
        return Place(windowWidth, left, top, bottom, settings.DossierWidthPercent);
    }

    public static string Cut(string? text, int chars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (text.Length <= chars)
            return text;
        return chars <= 2 ? text.Substring(0, Math.Max(0, chars)) : text.Substring(0, chars - 2) + "..";
    }
}
