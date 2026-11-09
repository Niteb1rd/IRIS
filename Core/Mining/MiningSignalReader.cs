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

public sealed partial class MiningSignalReader : IDisposable
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
        return Area(windowWidth, windowHeight, settings, cy - settings.SearchAboveLines * lh, cy + settings.SearchBelowLines * lh);
    }

    public static PixelRect RowSearchArea(int windowWidth, int windowHeight, MiningSettings settings)
    {
        var lh = LineHeight(windowHeight, settings);
        var cy = settings.RowCentreY * windowHeight / 1440.0;
        return Area(windowWidth, windowHeight, settings, cy - settings.RowSearchHalfLines * lh, cy + settings.RowSearchHalfLines * lh);
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

    public SignalRead Search(Mat image, Rect region, int windowWidth, int windowHeight)
    {
        var rows = RowSearchArea(windowWidth, windowHeight, _settings);
        var x0 = Math.Max((int)Math.Round(rows.X), region.X);
        var y0 = Math.Max((int)Math.Round(rows.Y), region.Y);
        var x1 = Math.Min((int)Math.Round(rows.X + rows.Width), Math.Min(region.X + region.Width, region.X + image.Cols));
        var y1 = Math.Min((int)Math.Round(rows.Y + rows.Height), Math.Min(region.Y + region.Height, region.Y + image.Rows));
        if (x1 - x0 >= 4 && y1 - y0 >= 4 && !image.Empty())
        {
            var band = new Rect(x0, y0, x1 - x0, y1 - y0);
            using var part = new Mat(image, new Rect(band.X - region.X, band.Y - region.Y, band.Width, band.Height));
            var read = ReadRows(part, band, windowWidth, windowHeight);
            if (read.State != SignalState.None)
                return read;
        }

        return ReadFromPin(image, region, windowWidth, windowHeight);
    }

    public SignalRead Read(Mat image, Rect region, int windowWidth, int windowHeight)
    {
        var read = ReadRows(image, region, windowWidth, windowHeight);
        return read.State != SignalState.None ? read : ReadFromPin(image, region, windowWidth, windowHeight);
    }

    private bool Prepare(Mat image, int windowHeight)
    {
        if (image.Empty() || image.Rows < 4 || image.Cols < 4 || windowHeight <= 0)
            return false;
        if (image.Channels() == 4)
            Cv2.CvtColor(image, _bgr, ColorConversionCodes.BGRA2BGR);
        else
            image.CopyTo(_bgr);
        Ink(windowHeight / 1440.0);
        return true;
    }

    private SignalRead ReadRows(Mat image, Rect region, int windowWidth, int windowHeight)
    {
        var lh = _settings.LineHeightAt1440 * windowHeight / 1440.0;
        var h = (int)Math.Round(lh);
        if (h < 4 || !Prepare(image, windowHeight))
            return SignalRead.Nothing;
        var width = _ink.Cols;
        var height = _ink.Rows;
        var ink = Bytes(_ink);

        var groups = new List<Group>();
        foreach (var rowTop in RowTops(ink, width, height, h))
            groups.AddRange(ReadRow(ink, width, height, rowTop, h, lh, small: false));
        return Decide(groups, lh, ink, width, region, windowWidth);
    }

    private SignalRead ReadFromPin(Mat image, Rect region, int windowWidth, int windowHeight)
    {
        if (!Prepare(image, windowHeight))
            return SignalRead.Nothing;
        var scale = windowHeight / 1440.0;
        var width = _ink.Cols;
        var height = _ink.Rows;
        double bestScore = double.NegativeInfinity, bestHeight = 0;
        var bestLoc = new Point();
        using (var inkF = new Mat())
        using (var template = new Mat())
        using (var result = new Mat())
        {
            _ink.ConvertTo(inkF, MatType.CV_32FC1);
            for (var pinHeight = _settings.PinHeightMin; pinHeight <= _settings.PinHeightMax; pinHeight++)
            {
                var th = (int)Math.Round(pinHeight * scale);
                var tw = Math.Max(3, (int)Math.Round(th * (double)PinTemplate.Value.Cols / PinTemplate.Value.Rows));
                if (th < 4 || th > height || tw > width)
                    continue;
                Cv2.Resize(PinTemplate.Value, template, new Size(tw, th), 0, 0, InterpolationFlags.Area);
                Cv2.MatchTemplate(inkF, template, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double max, out _, out Point loc);
                if (!double.IsNaN(max) && max > bestScore)
                {
                    bestScore = max;
                    bestHeight = th;
                    bestLoc = loc;
                }
            }
        }

        if (bestScore < _settings.PinMinScore)
            return SignalRead.Nothing;
        var lh = bestHeight * _settings.PinDigitRatio;
        var h = (int)Math.Round(lh);
        if (h < 4)
            return SignalRead.Nothing;
        var rowTop = (int)Math.Round(bestLoc.Y + bestHeight / 2.0 - lh / 2.0);
        var ink = Bytes(_ink);
        var small = h < _settings.SmallRowLines * _settings.LineHeightAt1440 * scale;
        return Decide(ReadRow(ink, width, height, rowTop, h, lh, small), lh, ink, width, region, windowWidth);
    }

    private const string PinTemplateText =
        "DAkLCQYGBAUNEA4OCw4KEAwEBAQBCwgLCAYGBTSSkJmQji0KDAkEBAQCDAkHBwYFRpiblZqRmY5UCgsFBAMACwkGBQYwmZJpLCo2c5ORCAgFBAQCDAYJCQqHmXwACQoMCoKYXgcGBQcDCQQHCgePmE8DDA0NC3WYegQFBQYDBgYDBwePlkcGCg4OC3KYfwAGBQgEBgIEBQiFmXQGCxIODYeaYgAGBQQDAgEEBwgylpBOFxYZXI+TAQIGBgYCAwIBCAgLhJGZjJSMlZBuAAcKBwgEAQQCCAoMI4yVjpiNl4MDBQgHCAYCAAUEBgkMD2eVjJeMlUoACQsGBgcCAgYGCQ4NERSRkJuOgQULDQ0ICQkFBQMHBwoPEhBMjJiMNAQOCgwGBgYDBAQICQ0QEQ8Uf5lsAAkSCwsJCQcEBgYIDAwNExATOJILCRASDAwICAgGCQMIBwoLDg4SD0kFDgwPCwkHCAQEBQQEETBCVG0jCxELFGhJMSIOBAQDBQaGjpOTlpFyChUOfpCUjpGLeQQEBpCXlJaUmZKSNhIrlJGbkpaTlYMAB4+TkJOPlo+VjJeMlY2Vj5KQlI0FCWSNlJSQlpGVjJaMlYuUj5KNik8ACwcMUnB6hYSOiZSJjYeHfHFYCAAJCAoICQYFAgEFBAMFAAMABQIGBgwO";

    private static readonly Lazy<Mat> PinTemplate = new(() =>
    {
        var bytes = Convert.FromBase64String(PinTemplateText);
        var mat = new Mat(24, 21, MatType.CV_32FC1);
        var values = bytes.Select(b => (float)b).ToArray();
        Marshal.Copy(values, 0, mat.Data, values.Length);
        return mat;
    });

    private SignalRead Decide(List<Group> groups, double lh, byte[] ink, int width, Rect region, int windowWidth)
    {
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
