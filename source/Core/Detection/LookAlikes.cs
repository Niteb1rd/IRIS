namespace Iris.Core.Detection;

public static class LookAlikes
{
    public static IReadOnlyList<string> AccuracyGroups { get; } = Array.Empty<string>();

    public static IReadOnlyList<string> EfficiencyGroups { get; } = new[] { "0OQD", "1I7", "5S", "8B", "6G", "2Z" };

    public const string EfficiencyLetters = "OQDISBGZ";

    public static bool NamesEqual(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string FoldO0(string name) => name.Replace('0', 'O');

    public static char? EfficiencyDigit(char c)
    {
        if (c is >= '0' and <= '9')
            return c;
        foreach (var group in EfficiencyGroups)
            if (group.IndexOf(c) > 0)
                return group[0];
        return null;
    }
}
