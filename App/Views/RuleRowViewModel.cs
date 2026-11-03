using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Config;

namespace Iris.App.Views;

public sealed class RuleRowViewModel : ObservableObject
{
    private AttributeKind _kind;
    private string _value;
    private int _priority;
    private OverlayColor _color;

    public RuleRowViewModel(AttributeKind kind, string? value, int priority, OverlayColor color)
    {
        _kind = kind;
        _priority = priority;
        _color = color;
        NeedsValue = Choices.KindNeedsValue(kind);
        InfoText = Choices.InfoTextFor(kind);

        var text = value ?? string.Empty;
        var vocabulary = Choices.SuggestionsFor(kind);
        var known = vocabulary.FirstOrDefault(v => string.Equals(v, text.Trim(), StringComparison.OrdinalIgnoreCase));
        _value = NeedsValue ? (known ?? text) : string.Empty;
        Suggestions = BuildSuggestions(vocabulary, _value);
    }

    public AttributeKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value))
                return;
            var vocabulary = Choices.SuggestionsFor(value);
            NeedsValue = Choices.KindNeedsValue(value);
            InfoText = Choices.InfoTextFor(value);
            if (!NeedsValue || !vocabulary.Contains(_value))
                _value = string.Empty;
            Suggestions = BuildSuggestions(vocabulary, _value);
            Raise(nameof(IsNoMatch));
            Raise(nameof(KindName));
            Raise(nameof(NeedsValue));
            Raise(nameof(InfoText));
            Raise(nameof(Suggestions));
            Raise(nameof(Value));
            Raise(nameof(HasInvalidValue));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value ?? string.Empty))
                Raise(nameof(HasInvalidValue));
        }
    }

    public int Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    public OverlayColor Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    public bool IsNoMatch => _kind == AttributeKind.NoMatch;

    public string KindName => Choices.DisplayName(_kind);

    public bool NeedsValue { get; private set; }

    public string InfoText { get; private set; } = string.Empty;

    public IReadOnlyList<string> Suggestions { get; private set; }

    public bool HasInvalidValue =>
        NeedsValue && _value.Length > 0 && !Choices.SuggestionsFor(_kind).Contains(_value);

    public bool IsValueAcceptable => !NeedsValue || (_value.Length > 0 && !HasInvalidValue);

    private static IReadOnlyList<string> BuildSuggestions(IReadOnlyList<string> vocabulary, string current)
    {
        if (current.Length == 0 || vocabulary.Contains(current))
            return vocabulary;
        var list = new List<string>(vocabulary) { current };
        return list;
    }
}
