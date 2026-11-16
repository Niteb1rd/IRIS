using System.Text.RegularExpressions;

namespace Iris.Core.Config;

public static class TomlGuard
{
    public const int MaxOpenBrackets = 32;

    private static readonly Regex HeaderLine = new(
        @"^[ \t]*(\[[ \t]*(?<k>[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*)[ \t]*\]|\[\[[ \t]*(?<k2>[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*)[ \t]*\]\])[ \t]*(#[^\[\]{}\r\n]*)?\r?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static string? Check(string text)
    {
        var count = OpenBrackets(text);
        return count > MaxOpenBrackets
            ? $"TOML structure: {count} or more [ or {{ outside table headers (at most {MaxOpenBrackets} are allowed; IRIS files use no arrays or inline tables)."
            : null;
    }

    public static int OpenBrackets(string text)
    {
        var count = 0;
        var start = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
                end = text.Length;
            var line = text.AsSpan(start, end - start);
            if (line.IndexOfAny('[', '{') >= 0 && !HeaderLine.IsMatch(line))
            {
                foreach (var c in line)
                {
                    if (c is '[' or '{' && ++count > MaxOpenBrackets)
                        return count;
                }
            }
            start = end + 1;
        }
        return count;
    }
}
