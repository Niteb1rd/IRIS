using System.Text;
using Iris.Core.Config;

namespace Iris.Core.Overlay;

public sealed record PanelOrgs(IReadOnlyList<OrgMembership>? Orgs, ColorMatch? Match)
{
    public static readonly PanelOrgs NotLookedUp = new(null, null);
}

public static class PanelText
{
    public const string NotesPrefix = "Notes: ";

    public static string NotesLine(string note, int charsPerLine)
    {
        var text = NotesPrefix + note.Trim();
        return text.Length <= charsPerLine ? text : text.Substring(0, Math.Max(NotesPrefix.Length, charsPerLine - 2)) + "..";
    }

    public const int MaxLines = 2;
    public const int NameMaxLength = 20;
    public const int NameCutLength = 18;
    public const string NameCut = "..";
    public const string Prefix = "Orgs: ";

    public const string NotLookedUpText = "Orgs: not looked up";

    public const string NoneFoundText = "Orgs: none found";
    public const string HiddenMainOrgText = "Hidden main org";
    public const string RedactedText = "REDACTED";
    private const string Etc = ", etc.";
    private const string Separator = ", ";

    public static IReadOnlyList<string> Lines(PanelOrgs orgs, OrgDisplay display, bool mainOrgOnly, int charsPerLine)
    {
        if (orgs.Orgs is null)
            return new[] { NotLookedUpText };
        if (orgs.Orgs.Count == 0)
            return new[] { NoneFoundText };

        var items = Items(orgs.Orgs, orgs.Match, display, mainOrgOnly);
        var perLine = Math.Max(Prefix.Length + 2, charsPerLine);
        return Wrap(items.Select(i => i.Full).ToList(), perLine, 0)
               ?? Wrap(items.Select(i => i.Capped).ToList(), perLine, 0)
               ?? WithEtc(items.Select(i => i.Capped).ToList(), perLine);
    }

    public static string CleanName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        var text = name.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var raw in text)
        {
            var c = raw switch
            {
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                _ => raw,
            };
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (c is (>= '!' and <= '~') or (>= 'À' and <= 'ÿ'))
            {
                if (pendingSpace)
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private readonly record struct Item(string Full, string Capped);

    private static List<Item> Items(IReadOnlyList<OrgMembership> orgs, ColorMatch? match, OrgDisplay display, bool mainOrgOnly)
    {
        var ordered = orgs.Where(o => o.IsMain).Concat(orgs.Where(o => !o.IsMain)).ToList();
        var deciding = DecidingIndex(ordered, match);
        var main = ordered.FindIndex(o => o.IsMain);

        List<OrgMembership> shown;
        bool markMain;
        if (mainOrgOnly && main >= 0)
        {
            shown = new List<OrgMembership> { ordered[main] };
            if (deciding >= 0 && deciding != main && match is { Kind: MatchKind.OrgList or MatchKind.OrgAttribute })
                shown.Add(ordered[deciding]);
            markMain = false;
        }
        else
        {
            shown = ordered;
            if (deciding > 0)
            {
                var first = shown[deciding];
                shown.RemoveAt(deciding);
                shown.Insert(0, first);
            }

            markMain = !mainOrgOnly;
        }

        var items = new List<Item>(shown.Count);
        foreach (var o in shown)
        {
            var label = Label(o, display);
            var mark = markMain && o.IsMain && !o.IsRedacted ? "*" : "";
            var suffix = match is { Kind: MatchKind.OrgAttribute, DecidingOrgSid: { } dsid, DecidingAttribute: { } attr }
                         && o.Sid is not null && string.Equals(o.Sid, dsid, StringComparison.OrdinalIgnoreCase)
                ? $" ({attr})"
                : "";
            var capped = label.Length > NameMaxLength ? label[..NameCutLength] + NameCut : label;
            items.Add(new Item(label + mark + suffix, capped + mark + suffix));
        }

        return items;
    }

    private static string Label(OrgMembership o, OrgDisplay display)
    {
        if (o.IsRedacted)
            return o.IsMain ? HiddenMainOrgText : RedactedText;
        if (display == OrgDisplay.FullName && CleanName(o.Name) is { Length: > 0 } name)
            return name;
        return o.Sid!;
    }

    private static int DecidingIndex(List<OrgMembership> ordered, ColorMatch? match)
    {
        if (match?.DecidingOrgSid is { } sid)
            return ordered.FindIndex(o => o.Sid is not null && string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));
        if (match is { Kind: MatchKind.OrgAttribute, DecidingAttribute: RedactedText })
            return ordered.FindIndex(o => o.IsRedacted);
        if (match is { Kind: MatchKind.OrgAttribute, DecidingAttribute: ColorResolver.HiddenMainOrgText })
            return ordered.FindIndex(o => o.IsMain && o.IsRedacted);
        return -1;
    }

    private static List<string>? Wrap(IReadOnlyList<string> items, int perLine, int reserve)
    {
        var lines = new List<string>(MaxLines);
        var current = new StringBuilder(Prefix);
        var first = true;
        foreach (var item in items)
        {
            if (first)
            {
                if (current.Length + item.Length > perLine)
                    return null;
                current.Append(item);
                first = false;
                continue;
            }

            if (current.Length + Separator.Length + item.Length <= perLine)
            {
                current.Append(Separator).Append(item);
                continue;
            }

            if (lines.Count + 1 >= MaxLines || current.Length + 1 > perLine || item.Length > perLine)
                return null;
            current.Append(',');
            lines.Add(current.ToString());
            current.Clear().Append(item);
        }

        if (current.Length + reserve > perLine)
            return null;
        lines.Add(current.ToString());
        return lines;
    }

    private static IReadOnlyList<string> WithEtc(List<string> items, int perLine)
    {
        for (var k = items.Count - 1; k >= 1; k--)
        {
            if (Wrap(items.Take(k).ToList(), perLine, Etc.Length) is { } lines)
            {
                lines[^1] += Etc;
                return lines;
            }
        }

        var tail = items.Count > 1 ? Etc : "";
        var room = Math.Max(1, perLine - Prefix.Length - tail.Length);
        var text = items[0].Length <= room ? items[0] : items[0][..Math.Max(1, room - NameCut.Length)] + NameCut;
        return new[] { Prefix + text + tail };
    }
}
