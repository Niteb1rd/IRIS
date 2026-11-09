using System.Globalization;
using System.Text;
using Iris.Core.Mining;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class MiningLossCapture : IDisposable
{
    private sealed record Shot(Mat Image, Rect Window, long Sequence, double FrameMs, SignalRead Read);

    private readonly MiningSettings _settings;
    private readonly Dictionary<(int Value, bool Unsure), double> _lastSent = new();
    private readonly List<Shot> _after = new();
    private int? _streakValue;
    private int _streak;
    private Shot? _good;

    public MiningLossCapture(MiningSettings settings) => _settings = settings;

    public Action<LossGroup>? Emit { get; set; }

    public void Clear()
    {
        _streakValue = null;
        _streak = 0;
        DropGroup();
    }

    public void Dispose() => Clear();

    public void Add(SignalRead read, FrameRegion region, CapturedFrame frame, MiningTable table)
    {
        if (Emit is null)
            return;
        if (read.State is SignalState.None or SignalState.Disqualified)
        {
            Clear();
            return;
        }

        if (SignalConfidence.IsConfident(read, _settings.ConfidentMargin) && read.Value is { } value)
        {
            if (value != _streakValue)
            {
                _streakValue = value;
                _streak = 0;
            }

            _streak++;
            DropGroup();
            if (_streak >= _settings.LossStreakReads && Cut(read, region, frame) is { } good)
                _good = good;
            return;
        }

        if (read.State != SignalState.Unreadable || _good is null || _streakValue is not { } label)
            return;
        if (Cut(read, region, frame) is not { } shot)
            return;
        _after.Add(shot);
        if (_after.Count < _settings.LossAfterReads)
            return;
        Finish(label, table);
        _streak = 0;
    }

    private Shot? Cut(SignalRead read, FrameRegion region, CapturedFrame frame)
    {
        if (read.Box.IsEmpty || !(read.DigitHeight > 0))
            return null;
        var m = _settings.LossCropMarginLines * read.DigitHeight;
        var box = new PixelRect(read.Box.X - m, read.Box.Y - m, read.Box.Width + 2 * m, read.Box.Height + 2 * m);
        return LossGroup.Cut(region.Image, region.Region, box) is { } cut ? new Shot(cut.Image, cut.Window, frame.Sequence, frame.FrameMs, read) : null;
    }

    private void DropGroup()
    {
        foreach (var shot in _after)
            shot.Image.Dispose();
        _after.Clear();
        _good?.Image.Dispose();
        _good = null;
    }

    public static (bool Mismatch, bool Conflict) Compare(string digits, IEnumerable<SignalRead> reads, double margin)
    {
        var mismatch = false;
        foreach (var read in reads)
        {
            if (read.DigitScores is not { } scores || scores.Count != digits.Length)
            {
                mismatch = true;
                continue;
            }

            for (var i = 0; i < scores.Count; i++)
            {
                var (best, lead) = Best(scores[i]);
                if (lead >= margin && best != digits[i] - '0')
                    return (mismatch, true);
            }
        }

        return (mismatch, false);
    }

    private static (int Digit, double Lead) Best(double[] scores)
    {
        int best = -1;
        double top = double.NegativeInfinity, second = double.NegativeInfinity;
        for (var d = 0; d < scores.Length; d++)
        {
            if (scores[d] > top)
            {
                second = top;
                top = scores[d];
                best = d;
            }
            else if (scores[d] > second)
            {
                second = scores[d];
            }
        }

        return (best, top - second);
    }

    private void Finish(int value, MiningTable table)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        var (mismatch, conflict) = Compare(digits, _after.Select(a => a.Read), _settings.ConfidentMargin);
        var inTable = table.Contains(value) || table.AboveLimitLabel(value) is not null;
        var unsure = !inTable || mismatch;
        var good = _good!;
        var key = (value, unsure);
        if (conflict || (_lastSent.TryGetValue(key, out var lastMs) && good.FrameMs - lastMs < _settings.LossCooldownMs))
        {
            DropGroup();
            return;
        }

        _lastSent[key] = good.FrameMs;
        var shots = new List<LossShot> { new("before", good.Image, good.Window, good.Sequence, good.FrameMs) };
        for (var i = 0; i < _after.Count; i++)
            shots.Add(new LossShot("after" + (i + 1).ToString(CultureInfo.InvariantCulture), _after[i].Image, _after[i].Window, _after[i].Sequence, _after[i].FrameMs));
        var label = value.ToString("N0", CultureInfo.InvariantCulture);
        var line = TraceLine.Begin(good.FrameMs, "mng", "loss").Str("kind", "mining").Str("label", label).Bool("unsure", unsure)
            .Str("reason", !inTable ? "value not in the table" : mismatch ? "digit count differs" : "unreadable after a confident streak")
            .Str("tableLabel", table.Lookup(value) ?? table.AboveLimitLabel(value)).Num("dh", good.Read.DigitHeight, 1).Array("shots");
        for (var i = 0; i < shots.Count; i++)
        {
            var read = i == 0 ? good.Read : _after[i - 1].Read;
            var shot = shots[i];
            line.Item().Str("role", shot.Role).Int("seq", shot.Sequence).Num("fms", shot.FrameMs)
                .Box("win", new PixelRect(shot.Window.X, shot.Window.Y, shot.Window.Width, shot.Window.Height))
                .Str("state", read.State.ToString()).Str("text", read.Text).Num("margin", SignalConfidence.MinMargin(read), 3).Str("digits", DigitText(read));
        }

        var sidecar = line.EndArray().Text();
        _good = null;
        _after.Clear();
        Emit!(new LossGroup(LossKind.Mining, unsure, digits, 0, shots, sidecar));
    }

    private static string DigitText(SignalRead read)
    {
        if (read.DigitScores is not { } scores)
            return string.Empty;
        var text = new StringBuilder();
        foreach (var place in scores)
        {
            if (place.Length == 0)
                continue;
            var (best, lead) = Best(place);
            var second = -1;
            for (var d = 0; d < place.Length; d++)
                if (d != best && (second < 0 || place[d] > place[second]))
                    second = d;
            if (text.Length > 0)
                text.Append(' ');
            text.Append(CultureInfo.InvariantCulture, $"{best}:{place[best]:0.00}/{second}:{(second >= 0 ? place[second] : double.NaN):0.00} lead {lead:0.00}");
        }

        return text.ToString();
    }
}
