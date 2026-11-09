
using System.Runtime.InteropServices;
using System.Text;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Mining;

public sealed partial class MiningSignalReader
{
    private sealed record Cell(bool Digit, int X, int Y, int W, int H, int Shape);

    private readonly record struct SymbolBox(int X, int Y, int Width, int Height);

    private sealed class Group
    {
        public required List<Cell> Cells { get; init; }

        public required byte[] Strip { get; init; }

        public required int StripHeight { get; init; }

        public required double Threshold { get; init; }

        public required int RowHeight { get; init; }

        public bool Small { get; init; }

        public bool Condensed { get; init; }

        public int Left { get; init; }

        public int Right { get; init; }

        public int Top { get; init; }

        public int Height { get; init; }

        public SymbolBox? Symbol { get; init; }

        public List<double> Scores { get; } = new();

        public List<double[]> AllScores { get; } = new();

        public double Centre => Symbol is { } s ? (s.X + Right) / 2.0 : (Left + Right) / 2.0;
    }

    private List<Group> ReadRow(byte[] ink, int width, int height, int rowTop, int h, double lh, bool small)
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
            byte[]? bgr = null;
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
                    cells.AddRange(SplitOrWhole(strip, stripHeight, width, h, small, labels ??= Ints(_labels), new Cell(true, x, y, w, hh, i)));
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
                var symbol = FindSymbol(ink, width, height, left, ya + gTop, ya + gBottom, gh, lh, threshold);
                var condensed = symbol is { } sb && IsCondensed(bgr ??= Bytes(_bgr), ink, width, sb, threshold, g);
                if (condensed)
                    g = Recut(strip, stripHeight, width, h, small, labels ??= Ints(_labels), stats, count, left, right);
                groups.Add(new Group
                {
                    Cells = g,
                    Strip = strip,
                    StripHeight = stripHeight,
                    Threshold = threshold,
                    RowHeight = h,
                    Small = small,
                    Condensed = condensed,
                    Left = left,
                    Right = right,
                    Top = ya + gTop,
                    Height = gh,
                    Symbol = symbol,
                });
            }
        }

        return groups;
    }

    private IEnumerable<Cell> SplitOrWhole(byte[] strip, int stripHeight, int width, int h, bool small, int[] labels, Cell whole)
    {
        var parts = Split(labels, width, whole.Shape, whole.X, whole.Y, whole.W, whole.H).ToList();
        if (whole.W > _settings.SplitWholeMaxLines * h)
            return parts;
        if (parts.Count != 2)
            return new List<Cell> { whole };
        var one = DigitTemplates.BestScore(Patch(strip, stripHeight, h, whole, width), small);
        var two = parts.Min(p => DigitTemplates.BestScore(Patch(strip, stripHeight, h, p, width), small));
        return two > one ? parts : new List<Cell> { whole };
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

    private bool IsCondensed(byte[] bgr, byte[] ink, int width, SymbolBox symbol, double threshold, List<Cell> cells)
    {
        long b = 0, g = 0, r = 0, n = 0;
        for (var y = symbol.Y; y < symbol.Y + symbol.Height; y++)
            for (var x = symbol.X; x < symbol.X + symbol.Width; x++)
            {
                if (ink[y * width + x] <= threshold)
                    continue;
                var i = (y * width + x) * 3;
                b += bgr[i];
                g += bgr[i + 1];
                r += bgr[i + 2];
                n++;
            }

        if (n < _settings.CondensedMinInkPixels)
            return false;
        double mb = (double)b / n, mg = (double)g / n, mr = (double)r / n;
        if (mr < _settings.CondensedMinRed || mg >= _settings.CondensedGreenShare * mr || mb >= _settings.CondensedBlueShare * mr)
            return false;
        var shares = cells.Where(c => c.Digit).Select(c => (double)c.W / c.H).Where(s => s > _settings.CondensedShapeMinShare && s < _settings.CondensedShapeMaxShare).OrderBy(s => s).ToList();
        return shares.Count > 0 && shares[(shares.Count - 1) / 4] < _settings.CondensedNarrowShare;
    }

    private List<Cell> Recut(byte[] strip, int stripHeight, int width, int h, bool small, int[] labels, int[] stats, int count, int left, int right)
    {
        var cells = new List<Cell>();
        for (var i = 1; i < count; i++)
        {
            int x = stats[i * 5], y = stats[i * 5 + 1], w = stats[i * 5 + 2], hh = stats[i * 5 + 3], area = stats[i * 5 + 4];
            if (area < 2 || x < left - 1 || x + w > right + 1)
                continue;
            if (hh >= 0.7 * h && hh <= 1.4 * h && w <= _settings.CondensedMaxShapeLines * h)
                cells.AddRange(CutCondensed(strip, stripHeight, width, h, small, labels, new Cell(true, x, y, w, hh, i)));
            else if (hh >= 0.25 * h && hh <= 0.6 * h && w <= 0.45 * h && y >= 0.55 * h)
                cells.Add(new Cell(false, x, y, w, hh, i));
        }

        return cells.OrderBy(c => c.X).ToList();
    }

    private IEnumerable<Cell> CutCondensed(byte[] strip, int stripHeight, int width, int h, bool small, int[] labels, Cell whole)
    {
        var share = (double)whole.W / whole.H;
        if (share <= _settings.CondensedOneShare)
            return new[] { whole };
        if (share <= _settings.CondensedPairShare)
        {
            var parts = CutInto(labels, width, whole, 2, _settings.CondensedCutReach);
            if (parts.Count != 2)
                return new[] { whole };
            var one = DigitTemplates.BestScore(Patch(strip, stripHeight, h, whole, width, condensed: true), small);
            var two = parts.Min(p => DigitTemplates.BestScore(Patch(strip, stripHeight, h, p, width, condensed: true), small));
            return two > one ? parts : new List<Cell> { whole };
        }

        var n = Math.Clamp((int)Math.Round((whole.W + 1) / (_settings.CondensedPitchShare * whole.H)), 2, 5);
        return CutInto(labels, width, whole, n, _settings.CondensedCutReach);
    }

    private static List<Cell> CutInto(int[] labels, int width, Cell whole, int n, double reach)
    {
        int x = whole.X, y = whole.Y, w = whole.W, hh = whole.H;
        var columns = new int[w];
        for (var r = 0; r < hh; r++)
            for (var c = 0; c < w; c++)
                if (labels[(y + r) * width + x + c] == whole.Shape)
                    columns[c]++;
        var cuts = new List<int>();
        for (var i = 1; i < n; i++)
        {
            var at = i * (double)w / n;
            var lo = Math.Max(1, (int)(at - reach * w / n));
            var hi = Math.Min(w - 1, (int)Math.Round(at + reach * w / n) + 1);
            lo = Math.Max(lo, cuts.Count > 0 ? cuts[^1] + 2 : 1);
            if (lo >= w)
                break;
            if (hi <= lo)
                hi = lo + 1;
            var cut = lo;
            for (var c = lo; c < Math.Min(hi, w); c++)
                if (columns[c] < columns[cut])
                    cut = c;
            cuts.Add(cut);
        }

        var parts = new List<Cell>();
        var edges = new List<int> { -1 };
        edges.AddRange(cuts);
        edges.Add(w);
        for (var e = 0; e + 1 < edges.Count; e++)
        {
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (var r = 0; r < hh; r++)
                for (var c = edges[e] + 1; c < edges[e + 1]; c++)
                {
                    if (labels[(y + r) * width + x + c] != whole.Shape)
                        continue;
                    minX = Math.Min(minX, c);
                    maxX = Math.Max(maxX, c);
                    minY = Math.Min(minY, r);
                    maxY = Math.Max(maxY, r);
                }

            if (maxX >= 0)
                parts.Add(new Cell(true, x + minX, y + minY, maxX - minX + 1, maxY - minY + 1, whole.Shape));
        }

        return parts;
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

            var patch = Patch(group.Strip, group.StripHeight, group.RowHeight, cell, width, group.Condensed);
            var (digit, score) = DigitTemplates.Match(patch, _settings.TieMargin, out var all, group.Small);
            group.AllScores.Add(all);
            text.Append(digit);
            group.Scores.Add(score);
        }

        return text.ToString();
    }

    private float[] Patch(byte[] strip, int stripHeight, int h, Cell cell, int width, bool condensed = false)
    {
        var cw = condensed ? (int)Math.Round(_settings.CondensedCellLines * h) + 2
            : cell.W > _settings.SplitWidthLines * h && cell.H <= 1.15 * h ? cell.W : (int)Math.Round(0.62 * h) + 2;
        var x0 = (int)Math.Round(cell.X + cell.W / 2.0 - cw / 2.0);
        var y0 = cell.Y - 1;
        var rows = cell.H + 2;
        var sub = new float[rows * cw];
        for (var r = 0; r < rows; r++)
        {
            var y = y0 + r;
            if (y < 0 || y >= stripHeight)
                continue;
            for (var c = 0; c < cw; c++)
            {
                var x = x0 + c;
                if (x >= 0 && x < width)
                    sub[r * cw + c] = strip[y * width + x];
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
}
