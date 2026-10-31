using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Config;

namespace Iris.App.Views;

public sealed record KindChoice(AttributeKind Kind, string Name);

public static class Choices
{
    public const int DefaultPriority = 5;

    public static IReadOnlyList<OverlayColor> Colors { get; } = Enum.GetValues<OverlayColor>();

    public static IReadOnlyList<int> Priorities { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    public static IReadOnlyList<KindChoice> Kinds { get; } = new[]
    {
        new KindChoice(AttributeKind.Archetype, "Org-Archetype"),
        new KindChoice(AttributeKind.Activity, "Org-Activity"),
        new KindChoice(AttributeKind.Redacted, "Redacted"),
        new KindChoice(AttributeKind.NoOrg, "No Org"),
    };

    public static IReadOnlyList<string> SuggestionsFor(AttributeKind kind) => OrgRuleValues.VocabularyFor(kind);

    public static string DisplayName(AttributeKind kind) =>
        kind == AttributeKind.NoMatch ? "No Match" : Kinds.FirstOrDefault(k => k.Kind == kind)?.Name ?? kind.ToString();

    public static string InfoTextFor(AttributeKind kind) => kind switch
    {
        AttributeKind.Redacted => "Player belongs in a Redacted org",
        AttributeKind.NoOrg => "Player is in no orgs/hidden orgs",
        AttributeKind.NoMatch => "Player does not meet any other rule",
        _ => string.Empty,
    };

    public static bool KindNeedsValue(AttributeKind kind) => OrgRuleValues.NeedsValue(kind);
}
