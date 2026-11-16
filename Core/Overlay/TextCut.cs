namespace Iris.Core.Overlay;

public static class TextCut
{
    public static string ToWidth(string text, double maxWidth, Func<string, double> widthOf)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(widthOf);
        if (text.Length == 0 || !double.IsFinite(maxWidth) || widthOf(text) <= maxWidth)
            return text;

        int low = 0, high = text.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (widthOf(Cut(text, mid)) <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }
        return Cut(text, low);
    }

    private static string Cut(string text, int length) => text.Substring(0, length).TrimEnd() + PanelText.NameCut;
}
