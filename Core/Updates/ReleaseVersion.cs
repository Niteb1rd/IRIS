using System.Globalization;

namespace Iris.Core.Updates;

public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Label) : IComparable<ReleaseVersion>
{
    public bool IsDev => Label.Equals("dev", StringComparison.OrdinalIgnoreCase) || Label.StartsWith("dev.", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}{(Label.Length > 0 ? "-" + Label : string.Empty)}");

    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];
        var label = string.Empty;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            label = s[(dash + 1)..];
            s = s[..dash];
            if (label.Length == 0 || label.Split('.').Any(part => part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return null;
        }

        var parts = s.Split('.');
        if (parts.Length != 3)
            return null;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return new ReleaseVersion(numbers[0], numbers[1], numbers[2], label);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0)
            c = Minor.CompareTo(other.Minor);
        if (c == 0)
            c = Patch.CompareTo(other.Patch);
        if (c != 0)
            return c;
        if (Label.Length == 0 || other.Label.Length == 0)
            return (Label.Length == 0 ? 1 : 0) - (other.Label.Length == 0 ? 1 : 0);

        var mine = Label.Split('.');
        var theirs = other.Label.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var a = mine[i];
            var b = theirs[i];
            var aNumber = a.All(char.IsAsciiDigit);
            var bNumber = b.All(char.IsAsciiDigit);
            int part;
            if (aNumber && bNumber)
                part = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            else if (aNumber != bNumber)
                part = aNumber ? -1 : 1;
            else
                part = string.CompareOrdinal(a, b);
            if (part != 0)
                return part;
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public bool IsNewerThan(ReleaseVersion other) => CompareTo(other) > 0;
}
