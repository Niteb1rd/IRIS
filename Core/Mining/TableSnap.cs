using System.Globalization;

namespace Iris.Core.Mining;

public static class TableSnap
{
    public static SignalRead Apply(SignalRead read, MiningTable table, MiningSettings settings)
    {
        if (read.State is not (SignalState.Signal or SignalState.Unreadable) || read.DigitScores is not { Count: > 0 } scores
            || !MiningSignalReader.IsNumber(read.Text) || read.Text.Count(char.IsAsciiDigit) != scores.Count)
            return read;

        var best = new int[scores.Count];
        var uncertain = -1;
        for (var place = 0; place < scores.Count; place++)
        {
            var s = scores[place];
            if (s.Length != 10)
                return read;
            int b = 0, second = -1;
            for (var d = 1; d < 10; d++)
            {
                if (s[d] > s[b])
                {
                    second = b;
                    b = d;
                }
                else if (second < 0 || s[d] > s[second])
                {
                    second = d;
                }
            }

            best[place] = b;
            if (s[b] < settings.MinDigitScore)
                return read;
            if (s[b] - s[second] < settings.ConfidentMargin)
            {
                if (uncertain >= 0)
                    return read;
                uncertain = place;
            }
        }

        if (uncertain < 0)
            return read;

        var choices = scores[uncertain];
        int? found = null;
        string? foundText = null;
        var foundDigit = -1;
        for (var d = 0; d < 10; d++)
        {
            if (choices[d] < choices[best[uncertain]] - settings.SnapChoiceMargin || choices[d] < settings.MinDigitScore)
                continue;
            var text = WithDigit(read.Text, uncertain, d);
            if (!int.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || !table.Contains(value))
                continue;
            if (found is not null)
                return read;
            found = value;
            foundText = text;
            foundDigit = d;
        }

        if (found is not { } snapped || (read.State == SignalState.Signal && read.Value == snapped))
            return read;
        var minScore = choices[foundDigit];
        for (var place = 0; place < scores.Count; place++)
            if (place != uncertain)
                minScore = Math.Min(minScore, scores[place][best[place]]);
        return read with { State = SignalState.Signal, Text = foundText!, Value = snapped, MinScore = minScore };
    }

    private static string WithDigit(string text, int place, int digit)
    {
        var chars = text.ToCharArray();
        chars[IndexOfPlace(text, place)] = (char)('0' + digit);
        return new string(chars);
    }

    private static int IndexOfPlace(string text, int place)
    {
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
                continue;
            if (seen == place)
                return i;
            seen++;
        }

        throw new ArgumentOutOfRangeException(nameof(place));
    }
}

public sealed class PerValueCooldown
{
    private readonly double _cooldownMs;
    private readonly Dictionary<int, double> _lastMs = new();

    public PerValueCooldown(double cooldownMs) => _cooldownMs = cooldownMs;

    public bool TryTake(int value, double frameMs)
    {
        if (_lastMs.TryGetValue(value, out var last) && frameMs - last < _cooldownMs && frameMs >= last)
            return false;
        _lastMs[value] = frameMs;
        return true;
    }
}
