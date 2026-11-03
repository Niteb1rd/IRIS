using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Iris.Core.Detection;

public readonly record struct CapBand(int Top, int Bottom)
{
    public int Height => Bottom - Top + 1;
}

public static class NameplateFeatures
{
    public const int Scale = 4;

    public const int GlyphWidth = 16, GlyphHeight = 24;

    public const int VectorLength = GlyphWidth * GlyphHeight + 4;

    private static class Kernels
    {
        public static readonly Mat TopHat = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(31, 31));
        public static readonly Mat Open = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3));
    }

    public static LineMask? CreateMask(Mat bgr, Rect box, double? threshold = null, int pad = 2, bool keepColors = false)
    {
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));

        var x0 = Math.Max(0, box.X - pad);
        var y0 = Math.Max(0, box.Y - pad);
        var x1 = Math.Min(bgr.Width, box.X + box.Width + pad);
        var y1 = Math.Min(bgr.Height, box.Y + box.Height + pad);
        if (x1 <= x0 || y1 <= y0)
            return null;

        using var crop = new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
        return CreateMaskFromCrop(crop, threshold, x0, y0, keepColors);
    }

    public static LineMask? CreateMask(byte[] bgr, int width, int height, int stride, Rect box, double? threshold = null, int pad = 2, bool keepColors = false)
    {
        if (width <= 0 || height <= 0 || stride < width * 3 || bgr.Length < (height - 1) * stride + width * 3)
            throw new ArgumentException("The buffer is smaller than width, height and stride say.", nameof(bgr));

        var x0 = Math.Max(0, box.X - pad);
        var y0 = Math.Max(0, box.Y - pad);
        var x1 = Math.Min(width, box.X + box.Width + pad);
        var y1 = Math.Min(height, box.Y + box.Height + pad);
        if (x1 <= x0 || y1 <= y0)
            return null;

        var w = x1 - x0;
        var h = y1 - y0;
        using var crop = new Mat(h, w, MatType.CV_8UC3);
        for (var y = 0; y < h; y++)
            Marshal.Copy(bgr, (y0 + y) * stride + x0 * 3, crop.Data + y * w * 3, w * 3);
        return CreateMaskFromCrop(crop, threshold, x0, y0, keepColors);
    }

    private static LineMask? CreateMaskFromCrop(Mat crop, double? threshold, int originX, int originY, bool keepColors)
    {
        using var big = new Mat();
        Cv2.Resize(crop, big, new Size(0, 0), Scale, Scale, InterpolationFlags.Cubic);

        var channels = Cv2.Split(big);
        using var topHat = new Mat();
        try
        {
            using var one = new Mat();
            for (var i = 0; i < channels.Length; i++)
            {
                if (i == 0)
                {
                    Cv2.MorphologyEx(channels[i], topHat, MorphTypes.TopHat, Kernels.TopHat);
                }
                else
                {
                    Cv2.MorphologyEx(channels[i], one, MorphTypes.TopHat, Kernels.TopHat);
                    Cv2.Max(topHat, one, topHat);
                }
            }
        }
        finally
        {
            foreach (var c in channels)
                c.Dispose();
        }

        double thr;
        if (threshold.HasValue)
        {
            thr = threshold.Value;
        }
        else
        {
            using var scratch = new Mat();
            var otsu = Cv2.Threshold(topHat, scratch, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            thr = Math.Clamp(otsu, 25.0, 90.0);
        }

        using var binary = new Mat();
        Cv2.Threshold(topHat, binary, thr, 1, ThresholdTypes.Binary);

        using var opened = new Mat();
        Cv2.MorphologyEx(binary, opened, MorphTypes.Open, Kernels.Open);

        var w = opened.Width;
        var h = opened.Height;
        var data = new byte[w * h];
        if (opened.IsContinuous())
        {
            Marshal.Copy(opened.Data, data, 0, data.Length);
        }
        else
        {
            using var copy = opened.Clone();
            Marshal.Copy(copy.Data, data, 0, data.Length);
        }

        byte[]? colors = null;
        if (keepColors)
        {
            colors = new byte[checked(big.Rows * big.Cols * 3)];
            if (big.IsContinuous())
            {
                Marshal.Copy(big.Data, colors, 0, colors.Length);
            }
            else
            {
                using var copy = big.Clone();
                Marshal.Copy(copy.Data, colors, 0, colors.Length);
            }
        }

        return TrimToText(data, h, w, originX, originY, colors);
    }

    internal static LineMask? TrimToText(byte[] data, int h, int w, int originX = 0, int originY = 0, byte[]? colors4x = null)
    {
        var rowLimit = 0.02 * w;
        int firstRow = -1, lastRow = -1;
        for (var y = 0; y < h; y++)
        {
            var sum = 0;
            for (var x = 0; x < w; x++)
                sum += data[y * w + x];
            if (sum > rowLimit)
            {
                if (firstRow < 0)
                    firstRow = y;
                lastRow = y;
            }
        }

        if (firstRow < 0)
            return null;

        int firstCol = int.MaxValue, lastCol = -1;
        for (var y = firstRow; y <= lastRow; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (data[y * w + x] == 0)
                    continue;
                if (x < firstCol)
                    firstCol = x;
                if (x > lastCol)
                    lastCol = x;
            }
        }

        if (lastCol < 0)
            return null;

        var outH = lastRow - firstRow + 1;
        var outW = lastCol - firstCol + 1;
        var result = new byte[outH * outW];
        for (var y = 0; y < outH; y++)
            Buffer.BlockCopy(data, (firstRow + y) * w + firstCol, result, y * outW, outW);

        byte[]? ink = null;
        if (colors4x is not null)
        {
            var count = 0;
            foreach (var v in result)
                count += v;
            ink = new byte[count * 3];
            var at = 0;
            for (var y = 0; y < outH; y++)
            {
                for (var x = 0; x < outW; x++)
                {
                    if (result[y * outW + x] == 0)
                        continue;
                    var source = ((firstRow + y) * w + firstCol + x) * 3;
                    ink[at++] = colors4x[source];
                    ink[at++] = colors4x[source + 1];
                    ink[at++] = colors4x[source + 2];
                }
            }
        }

        return new LineMask(outH, outW, result)
        {
            OriginX = originX,
            OriginY = originY,
            ColumnOffset = firstCol,
            RowOffset = firstRow,
            InkColors = ink,
        };
    }

    public static List<(int Start, int End)> GapCuts(LineMask m)
    {
        var cuts = new List<(int, int)>();
        var start = -1;
        for (var x = 0; x < m.Width; x++)
        {
            var hasInk = false;
            for (var y = 0; y < m.Height; y++)
            {
                if (m.Data[y * m.Width + x] != 0)
                {
                    hasInk = true;
                    break;
                }
            }

            if (hasInk && start < 0)
            {
                start = x;
            }
            else if (!hasInk && start >= 0)
            {
                cuts.Add((start, x));
                start = -1;
            }
        }

        if (start >= 0)
            cuts.Add((start, m.Width));
        return cuts;
    }

    public static CapBand FindCapBand(LineMask m)
    {
        var tops = new List<int>();
        var bottoms = new List<int>();
        foreach (var (a, b) in GapCuts(m))
        {
            int min = -1, max = -1;
            for (var y = 0; y < m.Height; y++)
            {
                for (var x = a; x < b; x++)
                {
                    if (m.Data[y * m.Width + x] == 0)
                        continue;
                    if (min < 0)
                        min = y;
                    max = y;
                    break;
                }
            }

            if (min >= 0 && max - min > 0.5 * m.Height)
            {
                tops.Add(min);
                bottoms.Add(max);
            }
        }

        if (tops.Count == 0)
            return new CapBand(0, m.Height - 1);
        return new CapBand(Median(tops), Median(bottoms));
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        var n = values.Count;
        var median = (n & 1) == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
        return (int)median;
    }

    public static double[]? GlyphVector(LineMask m, int x0, int x1, CapBand band)
    {
        using var scratch = new GlyphScratch(m.Height, m.Width);
        var vector = new double[VectorLength];
        return GlyphVector(m, x0, x1, band, vector, scratch) ? vector : null;
    }

    internal static bool GlyphVector(LineMask m, int x0, int x1, CapBand band, Span<double> vector, GlyphScratch scratch)
    {
        if (vector.Length < VectorLength)
            throw new ArgumentException($"The vector needs room for {VectorLength} values.", nameof(vector));
        if (m.Height > scratch.MaxHeight || m.Width > scratch.MaxWidth)
            throw new ArgumentException("The scratch is smaller than the mask.", nameof(scratch));

        x1 = Math.Min(x1, m.Width);
        var cap = Math.Max(1, band.Bottom - band.Top + 1);
        int rowMin = -1, rowMax = -1, colMin = int.MaxValue, colMax = -1;
        for (var y = 0; y < m.Height; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (m.Data[y * m.Width + x] == 0)
                    continue;
                if (rowMin < 0)
                    rowMin = y;
                rowMax = y;
                if (x < colMin)
                    colMin = x;
                if (x > colMax)
                    colMax = x;
            }
        }

        if (rowMin < 0)
            return false;

        var gh = rowMax - rowMin + 1;
        var gw = colMax - colMin + 1;

        var srcStep = (int)scratch.Source.Step();
        var row = scratch.Row;
        for (var y = 0; y < gh; y++)
        {
            for (var x = 0; x < gw; x++)
                row[x] = m.Data[(rowMin + y) * m.Width + colMin + x];
            Marshal.Copy(row, 0, scratch.Source.Data + y * srcStep, gw);
        }

        using (var view = scratch.Source.SubMat(0, gh, 0, gw))
        {
            Cv2.Resize(view, scratch.Target, new Size(GlyphWidth, GlyphHeight), 0, 0, InterpolationFlags.Area);
        }

        var small = scratch.Small;
        if (scratch.Target.IsContinuous())
        {
            Marshal.Copy(scratch.Target.Data, small, 0, small.Length);
        }
        else
        {
            using var copy = scratch.Target.Clone();
            Marshal.Copy(copy.Data, small, 0, small.Length);
        }

        for (var i = 0; i < small.Length; i++)
            vector[i] = small[i];

        var n = GlyphWidth * GlyphHeight;
        vector[n] = (double)gw / cap * 4.0;
        vector[n + 1] = (double)gh / cap * 4.0;
        vector[n + 2] = (double)(rowMin - band.Top) / cap * 4.0;
        vector[n + 3] = (double)(rowMax - band.Bottom) / cap * 4.0;
        return true;
    }
}

internal sealed class GlyphScratch : IDisposable
{
    public GlyphScratch(int maxHeight, int maxWidth)
    {
        MaxHeight = maxHeight;
        MaxWidth = maxWidth;
        Source = new Mat(maxHeight, maxWidth, MatType.CV_32FC1);
        Target = new Mat(NameplateFeatures.GlyphHeight, NameplateFeatures.GlyphWidth, MatType.CV_32FC1);
        Row = new float[maxWidth];
        Small = new float[NameplateFeatures.GlyphWidth * NameplateFeatures.GlyphHeight];
    }

    public int MaxHeight { get; }

    public int MaxWidth { get; }

    public Mat Source { get; }

    public Mat Target { get; }

    public float[] Row { get; }

    public float[] Small { get; }

    public void Dispose()
    {
        Source.Dispose();
        Target.Dispose();
    }
}
