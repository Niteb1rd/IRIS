
namespace Iris.Core.Detection;

public readonly record struct DistanceReading(string Text, double Value, bool Kilometers, bool HasSpeedGroup = false);

public static class DistanceLineRule
{
    public static string Alphabet { get; } = "0123456789.km" + LookAlikes.EfficiencyLetters;

    public static DistanceReading? Evaluate(PieceRead? read, NameplateDetectorSettings settings, Func<ReadPiece, bool>? isOpenBracket = null)
    {
        if (read is null || read.Pieces.Count == 0)
            return null;
        var pieces = read.Pieces;
        var cap = read.CapHeight;

        var first = 0;
        var last = pieces.Count - 1;
        while (last > first && pieces[last].Score < settings.DistanceEndScore)
            last--;
        while (first < last && pieces[first].Score < settings.DistanceEndScore)
            first++;
        if (first != 0 || last != pieces.Count - 1)
            pieces = pieces.Skip(first).Take(last - first + 1).ToList();

        var speedGroup = false;
        for (var i = 0; i + 1 < pieces.Count; i++)
        {
            if (pieces[i + 1].Start - pieces[i].End <= settings.WordGap * cap)
                continue;
            if (isOpenBracket is null || !isOpenBracket(pieces[i + 1]))
                return null;
            pieces = pieces.Take(i + 1).ToList();
            speedGroup = true;
            break;
        }

        var text = new System.Text.StringBuilder();
        var chars = 0;
        var total = 0.0;
        foreach (var piece in pieces)
        {
            foreach (var ch in piece.Text)
                text.Append(LookAlikes.EfficiencyDigit(ch) ?? ch);
            chars += piece.Text.Length;
            total += piece.Score;
        }

        var mean = total / Math.Max(1, chars);

        var gapSum = 0.0;
        var count = Math.Min(6, pieces.Count);
        for (var i = 0; i < count; i++)
            gapSum += (pieces[i].FreeScore - pieces[i].Score) / Math.Max(1, pieces[i].Text.Length);
        var gap = gapSum / count;

        if (mean < settings.MinDistanceScore || gap > settings.MaxDistanceGap)
            return null;

        var parsed = Parse(text.ToString(), settings.MaxDistanceValue);
        return parsed is { } reading ? reading with { HasSpeedGroup = speedGroup } : null;
    }

    public static DistanceReading? Parse(string text, int maxValue)
    {
        var at = 0;
        while (at < text.Length && at < 4 && text[at] is >= '0' and <= '9')
            at++;
        if (at == 0 || at >= text.Length || text[at] != '.')
            return null;
        var whole = int.Parse(text.AsSpan(0, at), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        if (whole > maxValue)
            return null;
        var dot = at;
        if (dot + 1 >= text.Length || text[dot + 1] is not (>= '0' and <= '9'))
            return null;
        var tenth = text[dot + 1] - '0';
        var unit = text.AsSpan(dot + 2);
        bool kilometers;
        if (unit.SequenceEqual("km"))
            kilometers = true;
        else if (unit.SequenceEqual("m"))
            kilometers = false;
        else
            return null;
        return new DistanceReading(text, whole + tenth / 10.0, kilometers);
    }
}
