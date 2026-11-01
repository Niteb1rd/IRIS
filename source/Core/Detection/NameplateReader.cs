using System.Text;
using OpenCvSharp;

namespace Iris.Core.Detection;

public readonly record struct NameplateRead(string Text, double Score)
{
    public static NameplateRead Empty { get; } = new("", 0.0);
}

public readonly record struct ReadPiece(int Start, int End, string Text, double Score, double FreeScore);

public sealed record PieceRead(IReadOnlyList<ReadPiece> Pieces, int CapHeight, IReadOnlyList<(int Start, int End)>? CharacterSpans = null, LineMask? Mask = null);

public sealed class NameplateReader
{
    private const double SplitFailureScore = -50.0;

    private readonly LetterModel _model;

    public NameplateReader(LetterModel model)
    {
        if (model.InputSize != NameplateFeatures.VectorLength)
            throw new ArgumentException($"The model expects {model.InputSize} inputs, the features have {NameplateFeatures.VectorLength}.", nameof(model));
        _model = model;
    }

    public NameplateRead Read(Mat bgr, Rect box)
    {
        var mask = NameplateFeatures.CreateMask(bgr, box);
        return mask is null ? NameplateRead.Empty : ReadMask(mask);
    }

    public NameplateRead Read(byte[] bgr, int width, int height, int stride, Rect box)
    {
        var mask = NameplateFeatures.CreateMask(bgr, width, height, stride, box);
        return mask is null ? NameplateRead.Empty : ReadMask(mask);
    }

    public NameplateRead ReadMask(LineMask m) => ReadCore(m, null, false, NameplateDetectorSettings.NameMinInk, null, out _);

    public bool[] CreateAlphabet(string characters)
    {
        var allowed = new bool[_model.ClassCount];
        var any = false;
        for (var i = 0; i < allowed.Length; i++)
        {
            if (characters.Contains(_model.Charset[i]))
            {
                allowed[i] = true;
                any = true;
            }
        }

        if (!any)
            throw new ArgumentException("None of the characters is in the model's charset.", nameof(characters));
        return allowed;
    }

    public PieceRead? ReadPieces(Mat bgr, Rect box, bool[]? allowed = null, double minInk = NameplateDetectorSettings.NameMinInk, bool keepColors = false)
    {
        var mask = NameplateFeatures.CreateMask(bgr, box, keepColors: keepColors);
        return mask is null ? null : ReadPieces(mask, allowed, minInk);
    }

    public PieceRead ReadPieces(LineMask m, bool[]? allowed = null, double minInk = NameplateDetectorSettings.NameMinInk)
    {
        if (allowed is not null && allowed.Length != _model.ClassCount)
            throw new ArgumentException("The alphabet has to have one entry per character of the model.", nameof(allowed));
        var pieces = new List<ReadPiece>();
        var spans = new List<(int Start, int End)>();
        ReadCore(m, allowed, true, minInk, pieces, out var cap, spans);
        return new PieceRead(pieces, cap, spans, m);
    }

    private NameplateRead ReadCore(LineMask m, bool[]? allowed, bool limitWork, double minInk, List<ReadPiece>? pieces, out int capHeight, List<(int Start, int End)>? spans = null)
    {
        var band = NameplateFeatures.FindCapBand(m);
        var cap = band.Bottom - band.Top + 1;
        capHeight = cap;
        var text = new StringBuilder();
        var total = 0.0;

        var cuts = NameplateFeatures.GapCuts(m);
        if (cuts.Count == 0)
            return new NameplateRead("", 0.0);

        Span<double> vector = stackalloc double[NameplateFeatures.VectorLength];
        Span<double> lp = _model.ClassCount <= 256 ? stackalloc double[_model.ClassCount] : new double[_model.ClassCount];
        Span<double> network = _model.ScratchSize <= 2048 ? stackalloc double[_model.ScratchSize] : new double[_model.ScratchSize];
        using var scratch = new GlyphScratch(m.Height, m.Width);

        foreach (var (a, b) in cuts)
        {
            var ink = 0;
            for (var y = 0; y < m.Height; y++)
                for (var x = a; x < b; x++)
                    ink += m.Data[y * m.Width + x];
            if (ink < minInk * cap * cap)
                continue;

            if (b - a <= 1.2 * cap)
            {
                if (!NameplateFeatures.GlyphVector(m, a, b, band, vector, scratch))
                    continue;
                _model.PredictLogProbs(vector, lp, network);
                var best = ArgMax(lp, allowed);
                text.Append(_model.Charset[best]);
                total += lp[best];
                spans?.Add((a, b));
                if (pieces is not null)
                {
                    var free = allowed is null ? best : ArgMax(lp, null);
                    pieces.Add(new ReadPiece(a, b, _model.Charset[best].ToString(), lp[best], lp[free]));
                }
            }
            else
            {
                var (piece, score) = Split(m, a, b, band, allowed, limitWork, vector, lp, network, scratch, spans);
                text.Append(piece);
                total += score;
                pieces?.Add(new ReadPiece(a, b, piece, score, score));
            }
        }

        return new NameplateRead(text.ToString(), total);
    }

    private (string Text, double Score) Split(LineMask m, int x0, int x1, CapBand band, bool[]? allowed, bool limitWork, Span<double> vector, Span<double> lp, Span<double> network, GlyphScratch scratch, List<(int Start, int End)>? spansOut = null)
    {
        var cap = band.Bottom - band.Top + 1;
        var width = x1 - x0;
        if (limitWork && width > NameplateDetectorSettings.MaxPieceCaps * cap)
            return ("", SplitFailureScore);

        var col = new int[width];
        for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < width; x++)
                col[x] += m.Data[y * m.Width + x0 + x];

        var cand = new List<int> { 0 };
        for (var x = 2; x < width - 2; x++)
            if (col[x] <= col[x - 1] && col[x] <= col[x + 1] && col[x] <= 0.5 * cap)
                cand.Add(x);
        cand.Add(width);

        if (limitWork)
        {
            var pairCount = 0;
            for (var i = 0; i < cand.Count; i++)
            {
                for (var j = i + 1; j < cand.Count; j++)
                {
                    var span = cand[j] - cand[i];
                    if (span > 1.2 * cap)
                        break;
                    if (span < 0.25 * cap)
                        continue;
                    pairCount++;
                }
            }

            if (pairCount > NameplateDetectorSettings.MaxSplitSpans)
                return ("", SplitFailureScore);
        }

        var spanFrom = new List<int>();
        var spanTo = new List<int>();
        var spanChar = new List<int>();
        var spanLogP = new List<double>();
        for (var i = 0; i < cand.Count; i++)
        {
            for (var j = i + 1; j < cand.Count; j++)
            {
                var span = cand[j] - cand[i];
                if (span > 1.2 * cap)
                    break;
                if (span < 0.25 * cap)
                    continue;
                if (!NameplateFeatures.GlyphVector(m, x0 + cand[i], x0 + cand[j], band, vector, scratch))
                    continue;
                _model.PredictLogProbs(vector, lp, network);
                var best = ArgMax(lp, allowed);
                spanFrom.Add(i);
                spanTo.Add(j);
                spanChar.Add(best);
                spanLogP.Add(lp[best]);
            }
        }

        if (spanFrom.Count == 0)
            return ("", SplitFailureScore);

        var score = new double[cand.Count];
        var backFrom = new int[cand.Count];
        var backChar = new int[cand.Count];
        Array.Fill(score, -1e9);
        Array.Fill(backFrom, -1);
        score[0] = 0.0;
        for (var k = 0; k < spanFrom.Count; k++)
        {
            var from = spanFrom[k];
            var to = spanTo[k];
            var s = score[from] + spanLogP[k];
            if (score[from] > -1e8 && s > score[to])
            {
                score[to] = s;
                backFrom[to] = from;
                backChar[to] = spanChar[k];
            }
        }

        var end = cand.Count - 1;
        if (score[end] <= -1e8)
            return ("", SplitFailureScore);

        var letters = new List<char>();
        var letterSpans = new List<(int, int)>();
        var at = end;
        while (at != 0 && backFrom[at] >= 0)
        {
            letters.Add(_model.Charset[backChar[at]]);
            letterSpans.Add((x0 + cand[backFrom[at]], x0 + cand[at]));
            at = backFrom[at];
        }

        letters.Reverse();
        letterSpans.Reverse();
        spansOut?.AddRange(letterSpans);
        return (new string(letters.ToArray()), score[end]);
    }

    public bool IsOpenBracket(LineMask m, int start, int end, double minScore)
    {
        var bracket = _model.Charset.IndexOf('[');
        if (bracket < 0)
            return false;
        var band = NameplateFeatures.FindCapBand(m);
        var vector = NameplateFeatures.GlyphVector(m, start, end, band);
        if (vector is null)
            return false;
        var lp = _model.PredictLogProbs(vector);
        var best = ArgMax(lp, null);
        return best == bracket && lp[best] >= minScore;
    }

    private static int ArgMax(ReadOnlySpan<double> values, bool[]? allowed)
    {
        var best = -1;
        for (var i = 0; i < values.Length; i++)
        {
            if (allowed is not null && !allowed[i])
                continue;
            if (best < 0 || values[i] > values[best])
                best = i;
        }

        return Math.Max(best, 0);
    }
}
