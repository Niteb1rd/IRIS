using OpenCvSharp;

namespace Iris.Core.Detection;

public readonly record struct O0Measurement(double OuterWidth, double OuterHeight, double HoleWidth, double HoleHeight);

public readonly record struct O0Glyph(int Index, double ProbabilityO);

public static class O0Rule
{
    public const double WeightA = 0.5667556378738614;

    public const double WeightB = 0.8238859429181884;

    public const double MeanO = 0.7533676849758579;

    public const double SdO = 0.025796386202400184;

    public const double Mean0 = 0.6808192767983506;

    public const double Sd0 = 0.01720362317305907;

    public const int Upscale = 8;

    public const int Pad = 3;

    public static O0Measurement? Measure(Mat crop)
    {
        if (crop.Empty() || crop.Depth() != MatType.CV_8U)
            throw new ArgumentException("Expected a non-empty 8 bit image.", nameof(crop));

        using var gray = new Mat();
        if (crop.Channels() == 1)
        {
            crop.CopyTo(gray);
        }
        else
        {
            var channels = Cv2.Split(crop);
            try
            {
                channels[0].CopyTo(gray);
                for (var i = 1; i < channels.Length; i++)
                    Cv2.Max(gray, channels[i], gray);
            }
            finally
            {
                foreach (var c in channels)
                    c.Dispose();
            }
        }

        using var asFloat = new Mat();
        gray.ConvertTo(asFloat, MatType.CV_32FC1);
        using var big = new Mat();
        Cv2.Resize(asFloat, big, new Size(0, 0), Upscale, Upscale, InterpolationFlags.Cubic);
        var width = big.Cols;
        var height = big.Rows;
        var values = new float[width * height];
        if (big.IsContinuous())
        {
            System.Runtime.InteropServices.Marshal.Copy(big.Data, values, 0, values.Length);
        }
        else
        {
            using var copy = big.Clone();
            System.Runtime.InteropServices.Marshal.Copy(copy.Data, values, 0, values.Length);
        }

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        var threshold = (float)((Percentile(sorted, 10) + Percentile(sorted, 99)) / 2.0);
        var mask = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
            mask[i] = values[i] > threshold ? (byte)1 : (byte)0;
        return MeasureMask(mask, width, height);
    }

    public static O0Measurement? MeasureMask(byte[] mask, int width, int height)
    {
        if (mask.Length != width * height)
            throw new ArgumentException("Mask data does not match its size.", nameof(mask));

        var columns = new int[width];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                columns[x] += mask[y * width + x];
        int bestStart = -1, bestEnd = -1;
        long bestInk = -1;
        var at = 0;
        while (at < width)
        {
            if (columns[at] > 0)
            {
                var start = at;
                long ink = 0;
                while (at < width && columns[at] > 0)
                    ink += columns[at++];
                if (ink > bestInk)
                {
                    bestInk = ink;
                    bestStart = start;
                    bestEnd = at;
                }
            }

            at++;
        }

        if (bestStart < 0)
            return null;

        var glyphWidth = bestEnd - bestStart;
        int firstRow = -1, lastRow = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = bestStart; x < bestEnd; x++)
            {
                if (mask[y * width + x] == 0)
                    continue;
                if (firstRow < 0)
                    firstRow = y;
                lastRow = y;
                break;
            }
        }

        var glyphHeight = lastRow - firstRow + 1;
        if (glyphWidth > 1.6 * glyphHeight)
            return null;

        var label = new int[glyphWidth * glyphHeight];
        var queue = new int[glyphWidth * glyphHeight];
        var bestArea = 0;
        var holeWidth = 0;
        var holeHeight = 0;
        var next = 0;
        for (var start = 0; start < label.Length; start++)
        {
            if (label[start] != 0 || mask[(firstRow + start / glyphWidth) * width + bestStart + start % glyphWidth] != 0)
                continue;
            next++;
            int head = 0, tail = 0, area = 0;
            int minX = glyphWidth, minY = glyphHeight, maxX = -1, maxY = -1;
            queue[tail++] = start;
            label[start] = next;
            while (head < tail)
            {
                var cell = queue[head++];
                var cx = cell % glyphWidth;
                var cy = cell / glyphWidth;
                area++;
                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
                for (var d = 0; d < 4; d++)
                {
                    var nx = cx + (d == 0 ? -1 : d == 1 ? 1 : 0);
                    var ny = cy + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || ny < 0 || nx >= glyphWidth || ny >= glyphHeight)
                        continue;
                    var neighbour = ny * glyphWidth + nx;
                    if (label[neighbour] != 0 || mask[(firstRow + ny) * width + bestStart + nx] != 0)
                        continue;
                    label[neighbour] = next;
                    queue[tail++] = neighbour;
                }
            }

            var interior = minX > 0 && minY > 0 && maxX + 1 < glyphWidth && maxY + 1 < glyphHeight;
            if (interior && area > bestArea)
            {
                bestArea = area;
                holeWidth = maxX - minX + 1;
                holeHeight = maxY - minY + 1;
            }
        }

        if (bestArea == 0)
            return null;
        return new O0Measurement(glyphWidth / (double)Upscale, glyphHeight / (double)Upscale, holeWidth / (double)Upscale, holeHeight / (double)Upscale);
    }

    public static double ProbabilityO(O0Measurement m)
    {
        var s = (m.OuterWidth / m.OuterHeight) * WeightA + (m.HoleWidth / m.OuterHeight) * WeightB;
        s = Math.Clamp(s, Math.Min(Mean0, MeanO), Math.Max(Mean0, MeanO));
        var lO = -0.5 * Math.Pow((s - MeanO) / SdO, 2) - Math.Log(SdO);
        var l0 = -0.5 * Math.Pow((s - Mean0) / Sd0, 2) - Math.Log(Sd0);
        var d = l0 - lO;
        if (d > 700)
            return 0.0;
        return 1.0 / (1.0 + Math.Exp(d));
    }

    public static char? Verdict(double probabilityO) =>
        probabilityO >= NameplateDetectorSettings.O0Decide ? 'O' : probabilityO <= 1.0 - NameplateDetectorSettings.O0Decide ? '0' : null;

    public static (int X0, int Y0, int X1, int Y1)? GlyphBox(LineMask mask, int start, int end, int pad = Pad)
    {
        int firstRow = -1, lastRow = -1;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = start; x < end && x < mask.Width; x++)
            {
                if (mask.Data[y * mask.Width + x] == 0)
                    continue;
                if (firstRow < 0)
                    firstRow = y;
                lastRow = y;
                break;
            }
        }

        if (firstRow < 0)
            return null;
        var scale = NameplateFeatures.Scale;
        var x0 = mask.OriginX + (int)Math.Floor((mask.ColumnOffset + start) / (double)scale) - pad;
        var x1 = mask.OriginX + (int)Math.Ceiling((mask.ColumnOffset + end) / (double)scale) + pad;
        var y0 = mask.OriginY + (int)Math.Floor((mask.RowOffset + firstRow) / (double)scale) - pad;
        var y1 = mask.OriginY + (int)Math.Ceiling((mask.RowOffset + lastRow + 1) / (double)scale) + pad;
        return (x0, y0, x1, y1);
    }

    public static string Correct(Mat bgr, PieceRead read, string text, List<O0Glyph>? evidence = null)
    {
        if (read.Mask is null || read.CharacterSpans is null || read.CharacterSpans.Count != text.Length)
            return text;
        if (text.IndexOf('O') < 0 && text.IndexOf('0') < 0)
            return text;
        var chars = text.ToCharArray();
        for (var k = 0; k < chars.Length; k++)
        {
            if (chars[k] != 'O' && chars[k] != '0')
                continue;
            var (start, end) = read.CharacterSpans[k];
            var box = GlyphBox(read.Mask, start, end);
            if (box is null)
                continue;
            var x0 = Math.Max(0, box.Value.X0);
            var y0 = Math.Max(0, box.Value.Y0);
            var x1 = Math.Min(bgr.Width, box.Value.X1);
            var y1 = Math.Min(bgr.Height, box.Value.Y1);
            if (x1 <= x0 || y1 <= y0)
                continue;
            using var crop = new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
            var measurement = Measure(crop);
            if (measurement is null)
                continue;
            var probability = ProbabilityO(measurement.Value);
            evidence?.Add(new O0Glyph(k, probability));
            if (Verdict(probability) is { } verdict)
                chars[k] = verdict;
        }

        return new string(chars);
    }

    private static double Percentile(float[] sorted, double q)
    {
        var position = (sorted.Length - 1) * q / 100.0;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
