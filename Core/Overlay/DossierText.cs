using System.Globalization;
using Iris.Core.Config;

namespace Iris.Core.Overlay;

public sealed record DossierOrgBox(string Title, string? FullName, string? Archetype, string? Primary, string? Secondary, string? Sid, AttributeTarget? Target);

public readonly record struct DossierPlacement(double Left, double Top, double Width, double Bottom)
{
    public bool IsEmpty => Width <= 0 || Bottom <= Top;
}

public sealed record SearchHit(string Handle, string Tag);

public static class DossierText
{
    public const double ListGapPercent = 5;

    public const double EdgeMarginPercent = 2.5;

    public const string NotKnown = "n/a";

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

    public static string Classification(MatchKind kind, ColorMatch? match, SocialConfig config) => kind switch
    {
        MatchKind.PlayerList => "Named Roster",
        MatchKind.OrgList => match?.DecidingOrgSid is { Length: > 0 } sid ? $"Org Affiliation ({sid})" : "Org Affiliation",
        MatchKind.OrgAttribute => AttributeClassification(match?.DecidingAttribute, config),
        _ => "No match",
    };

    private static string AttributeClassification(string? text, SocialConfig config)
    {
        if (string.IsNullOrEmpty(text))
            return "Org Attribute";
        if (text.Equals(ColorResolver.RedactedText, StringComparison.OrdinalIgnoreCase))
            return "Org Affiliation (Redacted)";
        if (text.Equals(ColorResolver.NoOrgText, StringComparison.OrdinalIgnoreCase))
            return "Org Affiliation (None)";
        if (text.Equals(ColorResolver.HiddenMainOrgText, StringComparison.OrdinalIgnoreCase))
            return "Org Affiliation (Hidden)";
        foreach (var rule in config.OrgRules)
        {
            if (rule.Kind is AttributeKind.Archetype or AttributeKind.Activity
                && string.Equals((rule.Value ?? string.Empty).Trim(), text, StringComparison.OrdinalIgnoreCase))
                return (rule.Kind == AttributeKind.Archetype ? "Org Structure (" : "Org Activity (") + text + ")";
        }
        return "Org Activity (" + text + ")";
    }

    public static string CitizenNumber(string record) => record.Length > 0 && record.All(char.IsAsciiDigit) ? "#" + record : record;

    public static string EnlistDate(DateOnly? enlisted, DateFormat format)
    {
        if (enlisted is not { } date)
            return NotKnown;
        var shown = format == DateFormat.StarCitizen ? date.AddYears(DateText.UniverseYearOffset) : date;
        return shown.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static string RecordAge(DateOnly? enlisted, DateOnly today)
    {
        if (enlisted is not { } start)
            return NotKnown;
        if (start >= today)
            return "today";
        var years = today.Year - start.Year;
        if (start.AddYears(years) > today)
            years--;
        var afterYears = start.AddYears(years);
        var months = (today.Year - afterYears.Year) * 12 + today.Month - afterYears.Month;
        if (afterYears.AddMonths(months) > today)
            months--;
        var days = today.DayNumber - afterYears.AddMonths(months).DayNumber;

        if (years > 0)
            return months > 0 ? $"{Units(years, "year")} {Units(months, "month")}" : Units(years, "year");
        if (months > 0)
            return days > 0 ? $"{Units(months, "month")} {Units(days, "day")}" : Units(months, "month");
        return Units(days, "day");
    }

    private static string Units(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    public static string LastSeen(DateTimeOffset? seen, DateTimeOffset now, DateFormat format)
    {
        if (seen is not { } when)
            return NotKnown;
        var ago = now - when;
        if (ago < TimeSpan.FromMinutes(1))
            return "just now";
        if (ago < TimeSpan.FromHours(1))
            return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1))
            return $"{(int)ago.TotalHours} h ago";
        return EnlistDate(DateOnly.FromDateTime(when.ToOffset(now.Offset).DateTime), format);
    }

    public static string SearchTag(DateTimeOffset? seen, DateTimeOffset now, DateFormat format) =>
        seen is null ? "saved" : "seen " + LastSeen(seen, now, format);

    private const double HalfBudgetPercent = 50 - ListGapPercent - EdgeMarginPercent;

    public static bool Fits(int dossierPercent, int panelPercent) => dossierPercent / 2.0 + panelPercent <= HalfBudgetPercent;

    public static int MaxPanelPercent(int dossierPercent)
        => Math.Clamp((int)Math.Floor(HalfBudgetPercent - dossierPercent / 2.0), AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax);

    public static int MaxDossierPercent(int panelPercent)
        => Math.Clamp((int)Math.Floor(2 * (HalfBudgetPercent - panelPercent)), AppSettings.DossierWidthPercentMin, AppSettings.DossierWidthPercentMax);

    public static (double Top, double Bottom) VerticalSpan(AppSettings settings, double windowHeight)
    {
        var gap = windowHeight * settings.PanelVerticalGapPercent / 100.0;
        return (gap, Math.Max(gap, windowHeight - gap));
    }

    public static DossierPlacement Place(double windowWidth, double top, double bottom, int widthPercent)
    {
        var width = windowWidth * widthPercent / 100.0;
        return width <= 0 ? new DossierPlacement(0, top, 0, bottom) : new DossierPlacement((windowWidth - width) / 2, top, width, bottom);
    }

    public static (double Left, double Top, double Width, double Bottom) PanelArea(AppSettings settings, double windowWidth, double windowHeight)
    {
        var width = windowWidth * settings.PanelWidthPercent / 100.0;
        var left = windowWidth / 2 + windowWidth * (settings.DossierWidthPercent / 2.0 + ListGapPercent) / 100.0;
        left = Math.Max(0, Math.Min(left, windowWidth - width));
        var (top, bottom) = VerticalSpan(settings, windowHeight);
        return (left, top, width, bottom);
    }

    public static DossierPlacement PlaceFor(AppSettings settings, double windowWidth, double windowHeight)
    {
        var (top, bottom) = VerticalSpan(settings, windowHeight);
        return Place(windowWidth, top, bottom, settings.DossierWidthPercent);
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
