using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Iris.Core.Lookup;

internal static class HtmlText
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    [ThreadStatic]
    private static HtmlParser? _parser;

    public static IHtmlDocument Parse(string html) => (_parser ??= new HtmlParser()).ParseDocument(html);

    public static string Clean(string? text) => text is null ? "" : Whitespace.Replace(text, " ").Trim();

    public static string Of(IElement? element) => element is null ? "" : Clean(element.TextContent);
}

public static class OrgVocabulary
{
    public static readonly IReadOnlyList<string> Archetypes =
        new[] { "PMC", "Syndicate", "Corporation", "Organization", "Faith", "Club" };

    public static readonly IReadOnlyList<string> Commitments =
        new[] { "Casual", "Regular", "Hardcore" };

    public static readonly IReadOnlyList<string> Activities = new[]
    {
        "Security", "Resources", "Exploration", "Smuggling", "Piracy", "Social", "Freelancing",
        "Trading", "Transport", "Scouting", "Medical", "Engineering", "Bounty Hunting", "Data Running",
    };

    public const string Exclusive = "Exclusive";

    public static string? MatchArchetype(string text) => Match(Archetypes, text);

    public static string? MatchCommitment(string text) => Match(Commitments, text);

    public static string? MatchActivity(string text) => Match(Activities, text);

    public static bool IsExclusive(string text) => string.Equals(text, Exclusive, StringComparison.OrdinalIgnoreCase);

    private static string? Match(IReadOnlyList<string> words, string text)
    {
        foreach (var word in words)
            if (string.Equals(word, text, StringComparison.OrdinalIgnoreCase))
                return word;
        return null;
    }
}

internal static class OrgAttributeScanner
{
    public sealed record Result(
        string? Archetype,
        string? Commitment,
        bool IsExclusive,
        IReadOnlyList<string> Activities);

    private const int MaxTagLength = 40;

    public static Result Scan(IElement scope)
    {
        string? archetype = null;
        string? commitment = null;
        bool exclusive = false;
        var activities = new List<string>();

        foreach (var element in scope.QuerySelectorAll("*"))
        {
            if (IsInsideLink(element, scope))
                continue;

            foreach (var text in Candidates(element))
            {
                if (archetype is null)
                    archetype = OrgVocabulary.MatchArchetype(text);
                if (commitment is null)
                    commitment = OrgVocabulary.MatchCommitment(text);
                if (OrgVocabulary.IsExclusive(text))
                    exclusive = true;
                var activity = OrgVocabulary.MatchActivity(text);
                if (activity is not null && !activities.Contains(activity, StringComparer.OrdinalIgnoreCase))
                    activities.Add(activity);
            }
        }

        return new Result(archetype, commitment, exclusive, activities);
    }

    private static IEnumerable<string> Candidates(IElement element)
    {
        if (element.Children.Length == 0)
        {
            var text = HtmlText.Of(element);
            if (text.Length > 0 && text.Length <= MaxTagLength)
                yield return text;
        }

        foreach (var attribute in new[] { "alt", "title" })
        {
            var value = HtmlText.Clean(element.GetAttribute(attribute));
            if (value.Length > 0 && value.Length <= MaxTagLength)
                yield return value;
        }
    }

    private static bool IsInsideLink(IElement element, IElement scope)
    {
        for (IElement? e = element; e is not null && !ReferenceEquals(e, scope); e = e.ParentElement)
        {
            if (string.Equals(e.LocalName, "a", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
