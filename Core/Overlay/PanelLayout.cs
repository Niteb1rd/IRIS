using Iris.Core.Config;

namespace Iris.Core.Overlay;

public sealed record PanelSlot(PanelEntry Entry, PixelRect Rect, IReadOnlyList<string> OrgLines);

public sealed record PanelMessage(IReadOnlyList<string> Lines, PixelRect Rect, bool Centered);

public sealed record PanelLayoutResult(
    IReadOnlyList<PanelSlot> Slots,
    string? MoreText,
    PixelRect MoreRect,
    double FontSize,
    double LineHeight,
    double PaddingX,
    double PaddingY,
    double CornerRadius,
    IReadOnlyList<PanelMessage> Messages)
{
    public static readonly PanelLayoutResult Empty = new(Array.Empty<PanelSlot>(), null, default, 0, 0, 0, 0, 0, Array.Empty<PanelMessage>());

    public bool IsEmpty => Slots.Count == 0 && MoreText is null && Messages.Count == 0;
}

public static class PanelLayout
{
    public const double BottomReservePercent = 10;

    public const double ReferenceHeight = 1440;

    public static double ScaledTextSize(int textSizeAt1440, double windowHeight) => textSizeAt1440 * windowHeight / ReferenceHeight;

    public const double NoteCharWidth = 0.62;

    public static PanelLayoutResult Compute(IReadOnlyList<PanelEntry> entries, double windowWidth, double windowHeight, AppSettings settings, string? note = null, string? toast = null)
    {
        if (windowWidth <= 0 || windowHeight <= 0)
            return PanelLayoutResult.Empty;
        if (entries.Count == 0 && note is null && toast is null)
            return PanelLayoutResult.Empty;

        var fontSize = ScaledTextSize(settings.PanelTextSize, windowHeight);
        var lineHeight = LineHeightFor(fontSize);
        var paddingX = PaddingXFor(fontSize);
        var paddingY = PaddingYFor(fontSize);
        var corner = CornerFor(fontSize);
        var gap = fontSize * 0.4;

        var panelWidth = windowWidth * settings.PanelWidthPercent / 100.0;
        var right = windowWidth - windowWidth * settings.PanelRightGapPercent / 100.0;
        var left = Math.Max(0, right - panelWidth);
        var top = windowHeight * settings.PanelTopGapPercent / 100.0;
        var limit = windowHeight - windowHeight * BottomReservePercent / 100.0;

        var messages = new List<PanelMessage>(2);
        var messageTop = top;
        if (toast is not null)
        {
            var height = lineHeight + 2 * paddingY;
            messages.Add(new PanelMessage(new[] { toast }, new PixelRect(left, messageTop, panelWidth, height), true));
            messageTop += height + gap;
        }

        if (note is not null)
        {
            var lines = WrapNote(note, panelWidth - 2 * paddingX, fontSize);
            if (lines.Count > 0)
            {
                var height = lines.Count * lineHeight + 2 * paddingY;
                messages.Add(new PanelMessage(lines, new PixelRect(left, messageTop, panelWidth, height), false));
                messageTop += height + gap;
            }
        }

        var entriesTop = messageTop;

        var perLine = CharsPerLine(panelWidth - 2 * paddingX, fontSize);
        var orgLines = new IReadOnlyList<string>[entries.Count];
        var tops = new double[entries.Count + 1];
        tops[0] = entriesTop;
        for (var i = 0; i < entries.Count; i++)
        {
            orgLines[i] = PanelText.Lines(entries[i].Orgs, settings.PanelOrgDisplay, settings.PanelMainOrgOnly, perLine);
            if (entries[i].Note is { Length: > 0 } entryNote)
                orgLines[i] = orgLines[i].Append(PanelText.NotesLine(entryNote, perLine)).ToList();
            tops[i + 1] = tops[i] + EntryHeight(orgLines[i].Count, lineHeight, paddingY) + gap;
        }

        var total = entries.Count;
        var shown = total;
        if (total > 0 && tops[total] - gap > limit)
        {
            shown = 0;
            for (var k = total - 1; k >= 0; k--)
            {
                if (tops[k] + lineHeight <= limit)
                {
                    shown = k;
                    break;
                }
            }
        }

        var slots = new List<PanelSlot>(shown);
        for (var i = 0; i < shown; i++)
            slots.Add(new PanelSlot(entries[i], new PixelRect(left, tops[i], panelWidth, EntryHeight(orgLines[i].Count, lineHeight, paddingY)), orgLines[i]));

        string? moreText = null;
        var moreRect = default(PixelRect);
        if (shown < total)
        {
            var moreTop = tops[shown];
            if (moreTop + lineHeight > windowHeight)
                moreTop = Math.Max(0, windowHeight - lineHeight);
            moreText = $"+{total - shown} more";
            moreRect = new PixelRect(left, moreTop, panelWidth, lineHeight);
        }

        return new PanelLayoutResult(slots, moreText, moreRect, fontSize, lineHeight, paddingX, paddingY, corner, messages);
    }

    public static int CharsPerLine(double width, double fontSize) => Math.Max(8, (int)Math.Floor(width / (fontSize * NoteCharWidth)));

    private static double EntryHeight(int orgLines, double lineHeight, double paddingY) => (1 + orgLines) * lineHeight + 2 * paddingY;

    public static IReadOnlyList<string> WrapNote(string text, double width, double fontSize)
    {
        var perLine = CharsPerLine(width, fontSize);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length == 0)
                current = word;
            else if (current.Length + 1 + word.Length <= perLine)
                current += " " + word;
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }

    private static double LineHeightFor(double fontSize) => fontSize * 1.25;

    private static double PaddingXFor(double fontSize) => fontSize * 0.7;

    private static double PaddingYFor(double fontSize) => fontSize * 0.45;

    private static double CornerFor(double fontSize) => fontSize * 0.5;
}
