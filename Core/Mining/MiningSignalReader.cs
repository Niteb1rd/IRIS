using System.Runtime.InteropServices;
using System.Text;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Mining;

public enum SignalState
{
    None,
    Disqualified,
    Unreadable,
    Signal,
}

public readonly record struct Bgr(byte B, byte G, byte R);

public sealed record SignalRead(
    SignalState State, string Text, int? Value, PixelRect Box, double RowTop, double DigitHeight, Bgr TextColour, Bgr BackColour, double OffsetPx, double MinScore,
    IReadOnlyList<double[]>? DigitScores = null)
{
    public static readonly SignalRead Nothing = new(SignalState.None, string.Empty, null, default, double.NaN, 0.0, default, default, double.NaN, double.NaN);

    public bool Found => State is SignalState.Signal or SignalState.Unreadable;
}

public sealed class MiningSignalReader : IDisposable
{
    private const int MaxRows = 4;

    private readonly MiningSettings _settings;
    private readonly Mat _bgr = new();
    private readonly Mat _ink = new();
    private readonly Mat _work = new();
    private readonly Mat _open = new();
    private readonly Mat _lines = new();
    private readonly Mat _low = new();
    private readonly Mat _high = new();
    private readonly Mat _labels = new();
    private readonly Mat _stats = new();
    private readonly Mat _centroids = new();

    public MiningSignalReader(MiningSettings? settings = null) => _settings = settings ?? MiningSettings.Default;

    public MiningSettings Settings => _settings;

    public static PixelRect SearchArea(int windowWidth, int windowHeight, MiningSettings settings)
    {
        var lh = LineHeight(windowHeight, settings);
        var cy = settings.RowCentreY * windowHeight / 1440.0;
        return Area(windowWidth, windowHeight, settings, cy - settings.SearchHalfHeightLines * lh, cy + settings.SearchHalfHeightLines * lh);
    }

    public static PixelRect LockArea(int windowWidth, int windowHeight, double rowTop, MiningSettings settings)
    {
        var lh = LineHeight(windowHeight, settings);
        return Area(windowWidth, windowHeight, settings, rowTop - settings.LockAboveLines * lh, rowTop + settings.LockBelowLines * lh);
    }

    private static PixelRect Area(int windowWidth, int windowHeight, MiningSettings settings, double top, double bottom)
    {
        var lh = LineHeight(windowHeight, settings);
        var cx = windowWidth / 2.0;
        var x0 = Math.Clamp(Math.Round(cx - settings.LeftLines * lh), 0, windowWidth);
        var x1 = Math.Clamp(Math.Round(cx + settings.RightLines * lh), 0, windowWidth);
        var y0 = Math.Clamp(Math.Round(top), 0, windowHeight);
        var y1 = Math.Clamp(Math.Round(bottom), 0, windowHeight);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static double LineHeight(int windowHeight, MiningSettings settings) => settings.LineHeightAt1440 * windowHeight / 1440.0;

    public SignalRead Read(Mat image, Rect region, int windowWidth, int windowHeight)
    {
        if (image.Empty() || image.Rows < 4 || image.Cols < 4 || windowHeight <= 0)
            return SignalRead.Nothing;
        if (image.Channels() == 4)
            Cv2.CvtColor(image, _bgr, ColorConversionCodes.BGRA2BGR);
        else
            image.CopyTo(_bgr);

        var scale = windowHeight / 1440.0;
        var lh = _settings.LineHeightAt1440 * scale;
        var h = (int)Math.Round(lh);
        if (h < 4)
            return SignalRead.Nothing;
        Ink(scale);
        var width = _ink.Cols;
        var height = _ink.Rows;
        var ink = Bytes(_ink);

        var groups = new List<Group>();
        foreach (var rowTop in RowTops(ink, width, height, h))
            groups.AddRange(ReadRow(ink, width, height, rowTop, h, lh));

        var unique = new List<Group>();
        foreach (var g in groups)
            if (!unique.Any(u => Math.Abs(g.Left - u.Left) <= 0.5 * lh && Math.Abs(g.Top - u.Top) <= 0.5 * lh))
                unique.Add(g);

        var expected = windowWidth / 2.0 + _settings.CentreOffsetLines * lh - region.X;
        Group? main = null;
        foreach (var g in unique)
            if (g.Symbol is not null && (main is null || Math.Abs(g.Centre - expected) < Math.Abs(main.Centre - expected)))
                main = g;
        if (main is null)
            return SignalRead.Nothing;

        var chosen = main;
        var offset = main.Centre - expected;
        var others = unique.Count(g => !ReferenceEquals(g, chosen) && Math.Abs(g.Top - chosen.Top) <= 0.5 * lh);
        var text = Classify(main, width);
        if (Math.Abs(offset) > _settings.CentreToleranceLines * lh || others > 0)
            return SignalRead.Nothing with { State = SignalState.Disqualified, Text = text, OffsetPx = offset };

        var sym = main.Symbol!.Value;
        var top = Math.Min(sym.Y, main.Top);
        var bottom = Math.Max(sym.Y + sym.Height, main.Top + main.Height);
        var box = new PixelRect(region.X + sym.X, region.Y + top, main.Right - sym.X, bottom - top);
        var (textColour, backColour) = Colours(main, ink, width);
        var minScore = main.Scores.Count == 0 ? 0.0 : main.Scores.Min();
        var readable = IsNumber(text) && main.Scores.All(s => s >= _settings.MinDigitScore);
        int? value = readable && int.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), out var parsed) ? parsed : null;
        return new SignalRead(
            value is null ? SignalState.Unreadable : SignalState.Signal, text, value, box, region.Y + main.Top, main.Height, textColour, backColour, offset, minScore,
            main.AllScores.ToArray());
    }

    public static bool IsNumber(string text)
    {
        if (text.Length == 0)
            return false;
        if (text.Length is >= 4 and <= 6 && text.All(c => c is >= '0' and <= '9'))
            return true;
        var parts = text.Split(',');
        if (parts[0].Length is < 1 or > 3)
            return false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0 && parts[i].Length != 3)
                return false;
            foreach (var c in parts[i])
                if (c is < '0' or > '9')
                    return false;
        }

        return true;
    }

    private void Ink(double scale)
    {
        var channels = Cv2.Split(_bgr);
        try
        {
            Cv2.Min(channels[0], channels[1], _low);
            Cv2.Min(_low, channels[2], _low);
            Cv2.Max(channels[0], channels[1], _high);
            Cv2.Max(_high, channels[2], _high);
        }
        finally
        {
            foreach (var c in channels)
                c.Dispose();
        }

        var k = (int)Math.Round(15 * scale) | 1;
        var length = Math.Max(3, (int)Math.Round(2.5 * _settings.LineHeightAt1440 * scale));
        using var square = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
        using var horizontal = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(length, 1));
        using var vertical = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, length));
        TopHatWithoutLines(_low, square, horizontal, vertical, _ink);
        TopHatWithoutLines(_high, square, horizontal, vertical, _work);
        Cv2.Max(_ink, _work, _ink);
    }

    private void TopHatWithoutLines(Mat channel, Mat square, Mat horizontal, Mat vertical, Mat result)
    {
        Cv2.MorphologyEx(channel, result, MorphTypes.TopHat, square);
        Cv2.MorphologyEx(result, _open, MorphTypes.Open, horizontal);
        Cv2.MorphologyEx(result, _lines, MorphTypes.Open, vertical);
        Cv2.Max(_open, _lines, _lines);
        Cv2.Subtract(result, _lines, result);
    }

    private static List<int> RowTops(byte[] ink, int width, int height, int h)
    {
        var tops = new List<int>();
        if (height < h)
            return tops;
        var profile = new double[height];
        for (var y = 0; y < height; y++)
        {
            long sum = 0;
            var row = y * width;
            for (var x = 0; x < width; x++)
                sum += ink[row + x];
            profile[y] = (double)sum / width;
        }

        var sums = new double[height - h + 1];
        for (var y = 0; y < sums.Length; y++)
        {
            var s = 0.0;
            for (var i = 0; i < h; i++)
                s += profile[y + i];
            sums[y] = s;
        }

        foreach (var y in Enumerable.Range(0, sums.Length).OrderByDescending(y => sums[y]))
        {
            if (tops.All(t => Math.Abs(y - t) >= 0.8 * h))
                tops.Add(y);
            if (tops.Count == MaxRows)
                break;
        }

        return tops;
    }

    private sealed record Cell(bool Digit, int X, int Y, int W, int H, int Shape);

    private readonly record struct SymbolBox(int X, int Y, int Width, int Height);

    private sealed class Group
    {
        public required List<Cell> Cells { get; init; }

        public required byte[] Strip { get; init; }

        public required int StripHeight { get; init; }

        public required double Threshold { get; init; }

        public required int RowHeight { get; init; }

        public int Left { get; init; }

        public int Right { get; init; }

        public int Top { get; init; }

        public int Height { get; init; }

        public SymbolBox? Symbol { get; init; }

        public List<double> Scores { get; } = new();

        public List<double[]> AllScores { get; } = new();

        public double Centre => Symbol is { } s ? (s.X + Right) / 2.0 : (Left + Right) / 2.0;
    }

    private List<Group> ReadRow(byte[] ink, int width, int height, int rowTop, int h, double lh)
    {
        var groups = new List<Group>();
        var ya = Math.Max(0, (int)(rowTop - 0.3 * h));
        var yb = Math.Min(height, (int)(rowTop + h + 0.3 * h) + 1);
        if (yb - ya < 2)
            return groups;
        var stripHeight = yb - ya;
        var strip = new byte[stripHeight * width];
        Array.Copy(ink, ya * width, strip, 0, strip.Length);

        double threshold;
        using (var stripMat = new Mat(stripHeight, width, MatType.CV_8UC1))
        using (var binary = new Mat())
        {
            Marshal.Copy(strip, 0, stripMat.Data, strip.Length);
            threshold = Math.Max(Cv2.Threshold(stripMat, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu), _settings.MinInkThreshold);
            Cv2.Threshold(stripMat, binary, threshold, 1, ThresholdTypes.Binary);
            var count = Cv2.ConnectedComponentsWithStats(binary, _labels, _stats, _centroids, PixelConnectivity.Connectivity8);
            var stats = Ints(_stats);
            int[]? labels = null;
            var cells = new List<Cell>();
            for (var i = 1; i < count; i++)
            {
                int x = stats[i * 5], y = stats[i * 5 + 1], w = stats[i * 5 + 2], hh = stats[i * 5 + 3], area = stats[i * 5 + 4];
                if (area < 2)
                    continue;
                var wide = w > _settings.SplitWidthLines * h && hh <= 1.15 * h;
                if (hh >= 0.7 * h && w <= 1.05 * h && hh <= 1.4 * h && !wide)
                    cells.Add(new Cell(true, x, y, w, hh, i));
                else if (hh >= 0.7 * h && wide && w <= 1.6 * h)
                    cells.AddRange(Split(labels ??= Ints(_labels), width, i, x, y, w, hh));
                else if (hh >= 0.25 * h && hh <= 0.6 * h && w <= 0.45 * h && y >= 0.55 * h)
                    cells.Add(new Cell(false, x, y, w, hh, i));
            }

            cells = cells.OrderBy(c => c.X).ToList();
            var runs = new List<List<Cell>>();
            var current = new List<Cell>();
            foreach (var c in cells)
            {
                if (current.Count > 0 && c.X - (current[^1].X + current[^1].W) > 0.75 * h)
                {
                    runs.Add(current);
                    current = new List<Cell>();
                }

                current.Add(c);
            }

            if (current.Count > 0)
                runs.Add(current);

            foreach (var run in runs)
            {
                var g = run.SkipWhile(c => !c.Digit).ToList();
                while (g.Count > 0 && !g[^1].Digit)
                    g.RemoveAt(g.Count - 1);
                if (g.Where(c => c.Digit).Select(c => c.Shape).Distinct().Count() < 2)
                    continue;
                var left = g[0].X;
                var right = g[^1].X + g[^1].W;
                var digits = g.Where(c => c.Digit).ToList();
                var gTop = digits.Min(c => c.Y);
                var gBottom = digits.Max(c => c.Y + c.H);
                var gh = gBottom - gTop;
                groups.Add(new Group
                {
                    Cells = g,
                    Strip = strip,
                    StripHeight = stripHeight,
                    Threshold = threshold,
                    RowHeight = h,
                    Left = left,
                    Right = right,
                    Top = ya + gTop,
                    Height = gh,
                    Symbol = FindSymbol(ink, width, height, left, ya + gTop, ya + gBottom, gh, lh, threshold),
                });
            }
        }

        return groups;
    }

    private static IEnumerable<Cell> Split(int[] labels, int width, int shape, int x, int y, int w, int hh)
    {
        var columns = new int[w];
        for (var r = 0; r < hh; r++)
            for (var c = 0; c < w; c++)
                if (labels[(y + r) * width + x + c] == shape)
                    columns[c]++;
        var lo = (int)(0.3 * w);
        var hi = Math.Max(lo + 1, (int)Math.Round(0.7 * w));
        var cut = lo;
        for (var c = lo; c < hi; c++)
            if (columns[c] < columns[cut])
                cut = c;
        foreach (var (from, to) in new[] { (0, cut), (cut + 1, w) })
        {
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (var r = 0; r < hh; r++)
                for (var c = from; c < to; c++)
                {
                    if (labels[(y + r) * width + x + c] != shape)
                        continue;
                    minX = Math.Min(minX, c);
                    maxX = Math.Max(maxX, c);
                    minY = Math.Min(minY, r);
                    maxY = Math.Max(maxY, r);
                }

            if (maxX >= 0)
                yield return new Cell(true, x + minX, y + minY, maxX - minX + 1, maxY - minY + 1, shape);
        }
    }

    private static SymbolBox? FindSymbol(byte[] ink, int width, int height, int left, int top, int bottom, int gh, double lh, double threshold)
    {
        var sx0 = (int)Math.Max(0, left - 2.6 * lh);
        var sx1 = (int)(left - 0.3 * lh);
        var sy0 = (int)Math.Max(0, top - 0.7 * gh);
        var sy1 = (int)Math.Min(height, bottom + 0.7 * gh);
        if (sx1 <= sx0 || sy1 <= sy0)
            return null;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, count = 0;
        for (var y = sy0; y < sy1; y++)
        {
            for (var x = sx0; x < Math.Min(sx1, width); x++)
            {
                if (ink[y * width + x] <= threshold)
                    continue;
                count++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (count < 0.4 * gh * gh)
            return null;
        var sw = maxX - minX + 1;
        var sh = maxY - minY + 1;
        if (sh >= gh && sw >= 0.5 * gh && sw <= 2.4 * gh)
            return new SymbolBox(minX, minY, sw, sh);
        return null;
    }

    private string Classify(Group group, int width)
    {
        var text = new StringBuilder();
        group.Scores.Clear();
        group.AllScores.Clear();
        foreach (var cell in group.Cells)
        {
            if (!cell.Digit)
            {
                text.Append(',');
                continue;
            }

            var patch = Patch(group, cell, width);
            var (digit, score) = DigitTemplates.Match(patch, _settings.TieMargin, out var all);
            group.AllScores.Add(all);
            text.Append(digit);
            group.Scores.Add(score);
        }

        return text.ToString();
    }

    private static float[] Patch(Group group, Cell cell, int width)
    {
        var h = group.RowHeight;
        var cw = (int)Math.Round(0.62 * h) + 2;
        var x0 = (int)Math.Round(cell.X + cell.W / 2.0 - cw / 2.0);
        var y0 = cell.Y - 1;
        var rows = cell.H + 2;
        var sub = new float[rows * cw];
        for (var r = 0; r < rows; r++)
        {
            var y = y0 + r;
            if (y < 0 || y >= group.StripHeight)
                continue;
            for (var c = 0; c < cw; c++)
            {
                var x = x0 + c;
                if (x >= 0 && x < width)
                    sub[r * cw + c] = group.Strip[y * width + x];
            }
        }

        var v = Percentile(sub, 0.95) + 1e-3;
        for (var i = 0; i < sub.Length; i++)
            sub[i] = (float)Math.Clamp(sub[i] / v, 0.0, 1.0);
        using var source = new Mat(rows, cw, MatType.CV_32FC1);
        Marshal.Copy(sub, 0, source.Data, sub.Length);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(DigitTemplates.Width, DigitTemplates.Height), 0, 0, InterpolationFlags.Linear);
        var result = new float[DigitTemplates.Width * DigitTemplates.Height];
        using var continuous = resized.Clone();
        Marshal.Copy(continuous.Data, result, 0, result.Length);
        return result;
    }

    private static double Percentile(float[] values, double q)
    {
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private (Bgr Text, Bgr Back) Colours(Group group, byte[] ink, int width)
    {
        var pixels = Bytes(_bgr);
        long tb = 0, tg = 0, tr = 0, tn = 0;
        var back = new List<(byte B, byte G, byte R)>();
        var top = Math.Max(0, group.Top - group.Height / 3);
        var bottom = Math.Min(_bgr.Rows, group.Top + group.Height + group.Height / 3);
        for (var y = top; y < bottom; y++)
        {
            for (var x = Math.Max(0, group.Left); x < Math.Min(width, group.Right); x++)
            {
                var i = (y * width + x) * 3;
                if (ink[y * width + x] > group.Threshold)
                {
                    tb += pixels[i];
                    tg += pixels[i + 1];
                    tr += pixels[i + 2];
                    tn++;
                }
                else if (ink[y * width + x] < group.Threshold / 2)
                {
                    back.Add((pixels[i], pixels[i + 1], pixels[i + 2]));
                }
            }
        }

        var text = tn == 0 ? new Bgr(255, 255, 255) : new Bgr((byte)(tb / tn), (byte)(tg / tn), (byte)(tr / tn));
        if (back.Count == 0)
            return (text, new Bgr(0, 0, 0));
        static byte Median(List<byte> v)
        {
            v.Sort();
            return v[v.Count / 2];
        }

        return (text, new Bgr(Median(back.Select(p => p.B).ToList()), Median(back.Select(p => p.G).ToList()), Median(back.Select(p => p.R).ToList())));
    }

    private static byte[] Bytes(Mat mat)
    {
        using var continuous = mat.IsContinuous() ? null : mat.Clone();
        var source = continuous ?? mat;
        var data = new byte[source.Rows * source.Cols * source.Channels()];
        Marshal.Copy(source.Data, data, 0, data.Length);
        return data;
    }

    private static int[] Ints(Mat mat)
    {
        using var continuous = mat.IsContinuous() ? null : mat.Clone();
        var source = continuous ?? mat;
        var data = new int[source.Rows * source.Cols];
        Marshal.Copy(source.Data, data, 0, data.Length);
        return data;
    }

    public void Dispose()
    {
        _bgr.Dispose();
        _ink.Dispose();
        _work.Dispose();
        _open.Dispose();
        _lines.Dispose();
        _low.Dispose();
        _high.Dispose();
        _labels.Dispose();
        _stats.Dispose();
        _centroids.Dispose();
    }
}
