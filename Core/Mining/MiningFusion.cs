using System.Globalization;
using OpenCvSharp;

namespace Iris.Core.Mining;

public static class SignalConfidence
{
    public static double MinMargin(SignalRead read)
    {
        if (read.DigitScores is not { Count: > 0 } scores)
            return 0.0;
        var min = double.PositiveInfinity;
        foreach (var digit in scores)
        {
            double best = double.NegativeInfinity, second = double.NegativeInfinity;
            foreach (var s in digit)
            {
                if (s > best)
                {
                    second = best;
                    best = s;
                }
                else if (s > second)
                {
                    second = s;
                }
            }

            min = Math.Min(min, best - second);
        }

        return min;
    }

    public static bool IsConfident(SignalRead read, double margin) => read.State == SignalState.Signal && MinMargin(read) >= margin;
}

public sealed class DigitEvidence
{
    private readonly int _maxReads;
    private readonly List<IReadOnlyList<double[]>> _reads = new();
    private string? _layout;

    public DigitEvidence(int maxReads) => _maxReads = Math.Max(1, maxReads);

    public int Count => _reads.Count;

    public void Reset()
    {
        _reads.Clear();
        _layout = null;
    }

    public void Add(SignalRead read)
    {
        if (read.State is not (SignalState.Signal or SignalState.Unreadable) || read.DigitScores is not { Count: > 0 } scores)
            return;
        var layout = Layout(read.Text);
        if (layout.Count(c => c == '#') != scores.Count)
            return;
        if (layout != _layout)
        {
            _reads.Clear();
            _layout = layout;
        }

        _reads.Add(scores);
        if (_reads.Count > _maxReads)
            _reads.RemoveAt(0);
    }

    public SignalRead? Fused(SignalRead current, double minDigitScore)
    {
        if (_reads.Count < 2 || _layout is not { } layout)
            return null;
        var places = _reads[0].Count;
        var averaged = new double[places][];
        var text = new System.Text.StringBuilder();
        var place = 0;
        var minScore = double.PositiveInfinity;
        foreach (var c in layout)
        {
            if (c != '#')
            {
                text.Append(c);
                continue;
            }

            var sum = new double[10];
            foreach (var read in _reads)
                for (var d = 0; d < 10; d++)
                    sum[d] += read[place][d] / _reads.Count;
            var best = 0;
            for (var d = 1; d < 10; d++)
                if (sum[d] > sum[best])
                    best = d;
            averaged[place] = sum;
            minScore = Math.Min(minScore, sum[best]);
            text.Append((char)('0' + best));
            place++;
        }

        var fused = text.ToString();
        if (!MiningSignalReader.IsNumber(fused) || minScore < minDigitScore
            || !int.TryParse(fused.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return null;
        return current with { State = SignalState.Signal, Text = fused, Value = value, MinScore = minScore, DigitScores = averaged };
    }

    private static string Layout(string text) => new(text.Select(c => c is >= '0' and <= '9' ? '#' : c).ToArray());
}

public sealed class FrameAverage : IDisposable
{
    private readonly int _maxFrames;
    private readonly Queue<Mat> _frames = new();
    private Rect _region;

    public FrameAverage(int maxFrames) => _maxFrames = Math.Max(2, maxFrames);

    public int Count => _frames.Count;

    public void Add(Mat image, Rect region)
    {
        if (image.Empty())
            return;
        if (region != _region)
        {
            Clear();
            _region = region;
        }

        var copy = new Mat();
        if (image.Channels() == 4)
            Cv2.CvtColor(image, copy, ColorConversionCodes.BGRA2BGR);
        else
            image.CopyTo(copy);
        _frames.Enqueue(copy);
        while (_frames.Count > _maxFrames)
            _frames.Dequeue().Dispose();
    }

    public Mat? Average()
    {
        if (_frames.Count < 2)
            return null;
        Mat? sum = null;
        using var part = new Mat();
        foreach (var frame in _frames)
        {
            frame.ConvertTo(part, MatType.CV_32FC3);
            if (sum is null)
                sum = part.Clone();
            else
                Cv2.Add(sum, part, sum);
        }

        var mean = new Mat();
        sum!.ConvertTo(mean, MatType.CV_8UC3, 1.0 / _frames.Count);
        sum.Dispose();
        return mean;
    }

    public void Clear()
    {
        while (_frames.Count > 0)
            _frames.Dequeue().Dispose();
    }

    public void Dispose() => Clear();
}

public sealed class LabelColours
{
    private readonly MiningSettings _settings;
    private double _tb, _tg, _tr, _bb, _bg, _br;
    private double _publishedMs = double.NegativeInfinity;

    public LabelColours(MiningSettings settings, Bgr text, Bgr back)
    {
        _settings = settings;
        (_tb, _tg, _tr) = (text.B, text.G, text.R);
        (_bb, _bg, _br) = (back.B, back.G, back.R);
        Text = text;
        Back = back;
    }

    public Bgr Text { get; private set; }

    public Bgr Back { get; private set; }

    public bool Add(Bgr text, Bgr back, double nowMs)
    {
        var a = _settings.ColourBlend;
        _tb += a * (text.B - _tb);
        _tg += a * (text.G - _tg);
        _tr += a * (text.R - _tr);
        _bb += a * (back.B - _bb);
        _bg += a * (back.G - _bg);
        _br += a * (back.R - _br);
        if (nowMs - _publishedMs < _settings.ColourUpdateMs)
            return false;
        var newText = new Bgr(Byte(_tb), Byte(_tg), Byte(_tr));
        var newBack = Readable(newText, new Bgr(Byte(_bb), Byte(_bg), Byte(_br)), _settings.MinContrast);
        if (Distance(newText, Text) < _settings.ColourStep && Distance(newBack, Back) < _settings.ColourStep)
            return false;
        Text = newText;
        Back = newBack;
        _publishedMs = nowMs;
        return true;
    }

    public static Bgr Readable(Bgr text, Bgr back, double minRatio)
    {
        var lt = Luminance(text);
        for (var i = 0; i < 20; i++)
        {
            var lb = Luminance(back);
            if ((Math.Max(lt, lb) + 0.05) / (Math.Min(lt, lb) + 0.05) >= minRatio)
                return back;
            back = new Bgr((byte)(back.B * 0.8), (byte)(back.G * 0.8), (byte)(back.R * 0.8));
        }

        return back;
    }

    private static double Luminance(Bgr c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static int Distance(Bgr a, Bgr b) => Math.Max(Math.Abs(a.B - b.B), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.R - b.R)));

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
