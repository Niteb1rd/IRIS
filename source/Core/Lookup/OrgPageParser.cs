using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Iris.Core.Lookup;

public static class OrgPageParser
{

    private static readonly string[] HeadingSelectors = { "#organization h1", "h1" };

    private const string HeadingSeparator = " / ";

    private const string OrgAreaSelector = "#organization";

    private const string TagSelector = ".tags li, .tags span, .tags a, .focus li, .focus img, .focus span";

    private const string MemberCountSelector = ".count, .logo .count";

    private static readonly Regex MemberCountPattern =
        new(@"(\d[\d,]*)\s+members?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RsiResult<OrgInfo> Parse(string html, string requestedSid)
    {
        var doc = HtmlText.Parse(html);

        string? heading = null;
        foreach (var selector in HeadingSelectors)
        {
            foreach (var h in doc.QuerySelectorAll(selector))
            {
                var text = HtmlText.Of(h);
                if (text.Contains(HeadingSeparator, StringComparison.Ordinal))
                {
                    heading = text;
                    break;
                }
            }
            if (heading is not null)
                break;
        }
        if (heading is null)
            return RsiResult<OrgInfo>.Failed("org page: heading 'Name / SID' not found (page layout may have changed)");

        int cut = heading.LastIndexOf(HeadingSeparator, StringComparison.Ordinal);
        var longName = heading.Substring(0, cut).Trim();
        var symbol = heading.Substring(cut + HeadingSeparator.Length).Trim();
        if (longName.Length == 0)
            return RsiResult<OrgInfo>.Failed("org page: heading has no org name");
        var sid = string.Equals(symbol, requestedSid, StringComparison.OrdinalIgnoreCase) ? symbol : requestedSid;

        int? memberCount = null;
        foreach (var el in doc.QuerySelectorAll(MemberCountSelector))
        {
            memberCount = ReadMemberCount(HtmlText.Of(el));
            if (memberCount is not null)
                break;
        }
        if (memberCount is null)
            memberCount = ReadMemberCount(HtmlText.Clean(doc.Body?.TextContent));

        string? archetype = null;
        string? commitment = null;
        bool exclusive = false;
        var activities = new List<string>();
        bool anyTag = false;

        foreach (var el in doc.QuerySelectorAll(TagSelector))
        {
            foreach (var text in TagTexts(el))
                anyTag |= Classify(text, ref archetype, ref commitment, ref exclusive, activities);
        }

        if (!anyTag)
        {
            IElement? area = doc.QuerySelector(OrgAreaSelector) ?? doc.Body;
            if (area is not null)
            {
                var scan = OrgAttributeScanner.Scan(area);
                archetype = scan.Archetype;
                commitment = scan.Commitment;
                exclusive = scan.IsExclusive;
                activities.AddRange(scan.Activities);
            }
        }

        return RsiResult<OrgInfo>.Found(new OrgInfo(sid, longName, memberCount, archetype, commitment, exclusive, activities));
    }

    private static int? ReadMemberCount(string text)
    {
        var m = MemberCountPattern.Match(text);
        if (!m.Success)
            return null;
        var digits = m.Groups[1].Value.Replace(",", "");
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static IEnumerable<string> TagTexts(IElement el)
    {
        var text = HtmlText.Of(el);
        if (text.Length > 0 && text.Length <= 40)
            yield return text;
        foreach (var attribute in new[] { "alt", "title" })
        {
            var value = HtmlText.Clean(el.GetAttribute(attribute));
            if (value.Length > 0 && value.Length <= 40)
                yield return value;
        }
    }

    private static bool Classify(string text, ref string? archetype, ref string? commitment, ref bool exclusive, List<string> activities)
    {
        var a = OrgVocabulary.MatchArchetype(text);
        if (a is not null)
        {
            archetype ??= a;
            return true;
        }

        var c = OrgVocabulary.MatchCommitment(text);
        if (c is not null)
        {
            commitment ??= c;
            return true;
        }

        if (OrgVocabulary.IsExclusive(text))
        {
            exclusive = true;
            return true;
        }

        var act = OrgVocabulary.MatchActivity(text);
        if (act is not null)
        {
            if (!activities.Contains(act, StringComparer.OrdinalIgnoreCase))
                activities.Add(act);
            return true;
        }

        return false;
    }
}
