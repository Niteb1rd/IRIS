using Iris.Core.Lookup;

namespace Iris.Core.Config;

public static class OrgRuleValues
{
    public static bool NeedsValue(AttributeKind kind) => kind is AttributeKind.Archetype or AttributeKind.Activity;

    public static IReadOnlyList<string> VocabularyFor(AttributeKind kind) => kind switch
    {
        AttributeKind.Archetype => OrgVocabulary.Archetypes,
        AttributeKind.Activity => OrgVocabulary.Activities,
        _ => Array.Empty<string>(),
    };

    public static string? Canonical(AttributeKind kind, string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        foreach (var known in VocabularyFor(kind))
            if (string.Equals(known, text, StringComparison.OrdinalIgnoreCase))
                return known;
        return null;
    }

    public static List<string> Check(SocialConfig config)
    {
        var problems = new List<string>();
        int number = 0;
        foreach (var rule in config.OrgRules)
        {
            if (rule.Kind == AttributeKind.NoMatch)
                continue;
            number++;
            if (!NeedsValue(rule.Kind))
                continue;

            var kindName = rule.Kind == AttributeKind.Archetype ? "archetype" : "activity";
            if (string.IsNullOrWhiteSpace(rule.Value))
                problems.Add($"Org rule {number}: the {kindName} rule needs a value.");
            else if (Canonical(rule.Kind, rule.Value) is null)
                problems.Add($"Org rule {number}: \"{rule.Value}\" is not a known {kindName} value. Known values: "
                    + string.Join(", ", VocabularyFor(rule.Kind)) + ".");
        }
        return problems;
    }
}
