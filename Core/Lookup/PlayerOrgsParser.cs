using AngleSharp.Dom;
using Iris.Core.Overlay;

namespace Iris.Core.Lookup;

public static class PlayerOrgsParser
{

    private const string MainClass = "main";
    private const string AffiliationClass = "affiliation";
    private static readonly string[] HiddenClasses = { "visibility-R", "visibility-H" };

    private const string ActivityLabel = "Activity";

    private const string ProfileMarkerSelector = ".profile, .profile-content";

    private const string OrgBlockSelector = ".box-content.org";

    private const string BlockHeadingSelector = ".title, .heading, h3";

    private const string EntrySelector = ".entry";
    private const string LabelSelector = ".label";
    private const string ValueSelector = ".value";

    private const string OrgLinkSelector = "a[href*='/orgs/']";

    private const string HandleLabel = "Handle name";
    private const string SidLabel = "Spectrum Identification";
    private const string ArchetypeLabel = "Archetype";

    private const string MainHeading = "main organization";
    private const string AffiliationHeading = "affiliation";

    private const string RedactedText = "redacted";

    private static readonly string[] NoOrgMarkers = { "no main org", "no affiliation", "public records" };

    public static RsiResult<PlayerOrgs> Parse(string html, string requestedHandle)
    {
        var doc = HtmlText.Parse(html);

        var orgs = new List<OrgMembership>();
        int blocksSeen = 0;
        foreach (var block in doc.QuerySelectorAll(OrgBlockSelector))
        {
            var heading = HtmlText.Of(block.QuerySelector(BlockHeadingSelector)).ToLowerInvariant();
            bool isMain = block.ClassList.Contains(MainClass) || heading.Contains(MainHeading, StringComparison.Ordinal);
            bool isAffiliation = !isMain && (block.ClassList.Contains(AffiliationClass)
                                             || heading.Contains(AffiliationHeading, StringComparison.Ordinal));
            if (!isMain && !isAffiliation)
                continue;

            blocksSeen++;
            var membership = ReadBlock(block, isMain);
            if (membership is not null)
                orgs.Add(membership);
        }

        if (blocksSeen == 0)
        {
            var pageText = HtmlText.Clean(doc.Body?.TextContent).ToLowerInvariant();
            bool mentionsHeadings = pageText.Contains(MainHeading, StringComparison.Ordinal)
                                    || pageText.Contains(AffiliationHeading, StringComparison.Ordinal);
            bool looksLikeProfile = doc.QuerySelector(ProfileMarkerSelector) is not null;
            if (mentionsHeadings || !looksLikeProfile)
                return RsiResult<PlayerOrgs>.Failed("player page: no organization blocks recognized (page layout may have changed)");
        }

        var shown = doc.DocumentElement is { } root ? ValueForLabel(root, HandleLabel) : null;
        var handle = RsiNames.IsValidHandle(shown) ? shown! : requestedHandle;

        return RsiResult<PlayerOrgs>.Found(new PlayerOrgs(handle, orgs));
    }

    private static OrgMembership? ReadBlock(IElement block, bool isMain)
    {
        if (HiddenClasses.Any(c => block.ClassList.Contains(c)))
            return new OrgMembership(null, isMain);

        var sid = SidFromLabel(block) ?? SidFromLink(block);

        if (sid is null)
        {
            var text = HtmlText.Of(block).ToLowerInvariant();
            bool redacted = text.Contains(RedactedText, StringComparison.Ordinal);
            if (!redacted && NoOrgMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
                return null;

            return new OrgMembership(null, isMain);
        }

        if (!isMain)
        {
            var affiliateArchetype = ValueForLabel(block, ArchetypeLabel);
            var affiliateActivities = ValuesForLabel(block, ActivityLabel);
            if (affiliateArchetype is null && affiliateActivities.Count == 0)
                return new OrgMembership(sid, false);
            return new OrgMembership(
                sid, false,
                affiliateArchetype is null ? null : (OrgVocabulary.MatchArchetype(affiliateArchetype) ?? affiliateArchetype),
                affiliateActivities);
        }

        var labeled = ValueForLabel(block, ArchetypeLabel);
        var labeledActivities = ValuesForLabel(block, ActivityLabel);
        var scan = OrgAttributeScanner.Scan(block);
        var archetype = labeled is null ? scan.Archetype : (OrgVocabulary.MatchArchetype(labeled) ?? labeled);
        IReadOnlyList<string> activities = labeledActivities.Count > 0 ? labeledActivities : scan.Activities;
        return new OrgMembership(sid, true, archetype, activities);
    }

    private static string? SidFromLabel(IElement block)
    {
        var value = ValueForLabel(block, SidLabel);
        return RsiNames.IsValidSid(value) ? value : null;
    }

    private static string? SidFromLink(IElement block)
    {
        var href = block.QuerySelector(OrgLinkSelector)?.GetAttribute("href");
        if (href is null)
            return null;
        var path = href.Split('?', '#')[0].TrimEnd('/');
        var segment = path.Substring(path.LastIndexOf('/') + 1);
        return RsiNames.IsValidSid(segment) ? segment : null;
    }

    private static List<string> ValuesForLabel(IElement scope, string label)
    {
        var values = new List<string>();
        foreach (var entry in scope.QuerySelectorAll(EntrySelector))
        {
            if (!HtmlText.Of(entry.QuerySelector(LabelSelector)).Contains(label, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = HtmlText.Of(entry.QuerySelector(ValueSelector));
            if (value.Length > 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }
        return values;
    }

    private static string? ValueForLabel(IElement scope, string label)
    {
        foreach (var entry in scope.QuerySelectorAll(EntrySelector))
        {
            var labelText = HtmlText.Of(entry.QuerySelector(LabelSelector));
            if (!labelText.Contains(label, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = HtmlText.Of(entry.QuerySelector(ValueSelector));
            if (value.Length > 0)
                return value;
        }
        return null;
    }
}
