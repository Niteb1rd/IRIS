using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Iris.Core.Lookup;

public static class ProfilePageParser
{

    public const string NoCitizenRecord = "n/a";

    private const string RecordSelector = ".citizen-record";
    private const string RecordLabelText = "UEE Citizen Record";
    private const string ProfileSelector = ".profile";
    private const string InfoEntrySelector = ".profile .info .entry";
    private const string LabelSelector = ".label";
    private const string ValueSelector = ".value";
    private const string LeftEntrySelector = ".left-col .entry";
    private const string EnlistedLabel = "Enlisted";
    private static readonly string[] EnlistedFormats = { "MMM d, yyyy", "MMM dd, yyyy", "MMMM d, yyyy", "MMMM dd, yyyy" };

    private static readonly Regex RecordPattern = new(@"^#\s*(\d+)$", RegexOptions.CultureInvariant);

    public static RsiResult<PlayerProfile> Parse(string html, string requestedHandle)
    {
        var doc = HtmlText.Parse(html);

        var recordElement = doc.QuerySelector(RecordSelector);
        bool hasLabel = recordElement is not null
            || doc.QuerySelectorAll(LabelSelector).Any(l => HtmlText.Of(l).Contains(RecordLabelText, StringComparison.OrdinalIgnoreCase));
        if (doc.QuerySelector(ProfileSelector) is null && !hasLabel)
            return RsiResult<PlayerProfile>.Failed("profile page: no profile block (page layout may have changed)");

        string? record = null;
        if (recordElement?.QuerySelector(ValueSelector) is { } value)
        {
            var match = RecordPattern.Match(HtmlText.Of(value));
            if (match.Success)
                record = match.Groups[1].Value;
        }

        string? moniker = null;
        foreach (var entry in doc.QuerySelectorAll(InfoEntrySelector))
        {
            if (entry.QuerySelector(LabelSelector) is not null)
                continue;
            if (entry.QuerySelector(ValueSelector) is IElement shown && HtmlText.Of(shown) is { Length: > 0 } text)
            {
                moniker = text;
                break;
            }
        }

        var (enlisted, unreadable) = Enlisted(doc);
        return RsiResult<PlayerProfile>.Found(new PlayerProfile(requestedHandle, moniker, record, hasLabel, enlisted, unreadable));
    }

    private static (DateOnly? Date, bool Unreadable) Enlisted(IDocument doc)
    {
        foreach (var entry in doc.QuerySelectorAll(LeftEntrySelector))
        {
            if (entry.QuerySelector(LabelSelector) is not { } label
                || !string.Equals(HtmlText.Of(label), EnlistedLabel, StringComparison.OrdinalIgnoreCase)
                || entry.QuerySelector(ValueSelector) is not { } value)
                continue;
            return DateOnly.TryParseExact(HtmlText.Of(value), EnlistedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? (date, false)
                : (null, true);
        }
        return (null, false);
    }

    public static string Shown(string? record) => record is { Length: > 0 } digits ? digits : NoCitizenRecord;
}
