namespace Iris.Core.Config;

public static class TomlDepth
{
    public const int Limit = 16;

    public const string Error = "TOML syntax: the file is nested too deeply";

    public static bool TooDeep(string text)
    {
        var depth = 0;
        var at = 0;
        while (at < text.Length)
        {
            var c = text[at];
            if (c == '#')
            {
                while (at < text.Length && text[at] != '\n')
                    at++;
                continue;
            }

            if (c is '"' or '\'')
            {
                at = AfterString(text, at);
                continue;
            }

            if (c is '[' or '{')
            {
                if (++depth > Limit)
                    return true;
            }
            else if (c is ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }

            at++;
        }

        return false;
    }

    private static int AfterString(string text, int start)
    {
        var quote = text[start];
        var triple = start + 2 < text.Length && text[start + 1] == quote && text[start + 2] == quote;
        var at = start + (triple ? 3 : 1);
        while (at < text.Length)
        {
            var c = text[at];
            if (c == '\\' && quote == '"')
            {
                at += 2;
                continue;
            }

            if (c == quote)
            {
                if (!triple)
                    return at + 1;
                if (at + 2 < text.Length && text[at + 1] == quote && text[at + 2] == quote)
                    return at + 3;
            }
            else if (c == '\n' && !triple)
            {
                return at;
            }

            at++;
        }

        return at;
    }
}
