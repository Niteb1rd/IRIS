namespace Iris.Core.Overlay;

public static class PanelText
{
    public const int MaxLineLength = 40;
    private const string Prefix = "Orgs: ";

    public const string NotLookedUp = "Orgs: not looked up";
    private const string Etc = ", etc.";

    public static string OrgLine(IReadOnlyList<OrgMembership> orgs, ColorMatch? match)
    {
        if (orgs.Count == 0)
            return "Orgs: none found";

        var ordered = orgs.Where(o => o.IsMain).Concat(orgs.Where(o => !o.IsMain)).ToList();

        int i = -1;
        if (match?.DecidingOrgSid is { } sid)
            i = ordered.FindIndex(o => o.Sid is not null && string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));
        else if (match is { Kind: MatchKind.OrgAttribute, DecidingAttribute: "REDACTED" })
            i = ordered.FindIndex(o => o.IsRedacted);
        if (i > 0)
        {
            var first = ordered[i];
            ordered.RemoveAt(i);
            ordered.Insert(0, first);
        }

        var items = new List<string>(ordered.Count);
        foreach (var o in ordered)
        {
            var text = (o.Sid ?? "REDACTED") + (o.IsMain ? "*" : "");
            if (match is { Kind: MatchKind.OrgAttribute, DecidingOrgSid: { } dsid, DecidingAttribute: { } attr }
                && o.Sid is not null && string.Equals(o.Sid, dsid, StringComparison.OrdinalIgnoreCase))
                text += $" ({attr})";
            items.Add(text);
        }

        var full = Prefix + string.Join(", ", items);
        if (full.Length <= MaxLineLength)
            return full;

        for (int k = items.Count - 1; k >= 1; k--)
        {
            var cut = Prefix + string.Join(", ", items.Take(k)) + Etc;
            if (cut.Length <= MaxLineLength)
                return cut;
        }
        var room = MaxLineLength - Prefix.Length - Etc.Length;
        return Prefix + items[0][..Math.Max(1, room)] + Etc;
    }
}
