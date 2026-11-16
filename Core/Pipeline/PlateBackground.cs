using OpenCvSharp;

namespace Iris.Core.Pipeline;

public enum BackgroundBand
{
    Dark,
    Mid,
    Bright,
    VeryBright,
    Glare,
}

public static class PlateBackground
{
    public const double SideLines = 1.0;
    public const double AboveLines = 2.5;
    public const double BelowLines = 1.0;
    public const int MidMin = 100;
    public const int BrightMin = 160;
    public const int VeryBrightMin = 190;
    public const int GlareMin = 220;

    public static int? Measure(Mat image, double x, double y, double width, double height)
    {
        if (image.Empty() || image.Channels() < 3 || !(height > 0.0) || !(width > 0.0))
            return null;
        var left = (int)Math.Max(0.0, Math.Floor(x - SideLines * height));
        var top = (int)Math.Max(0.0, Math.Floor(y - AboveLines * height));
        var right = (int)Math.Min(image.Width, Math.Ceiling(x + width + SideLines * height));
        var bottom = (int)Math.Min(image.Height, Math.Ceiling(y + height + BelowLines * height));
        if (right <= left || bottom <= top)
            return null;

        using var area = new Mat(image, new Rect(left, top, right - left, bottom - top));
        using var value = new Mat();
        using var other = new Mat();
        Cv2.ExtractChannel(area, value, 0);
        Cv2.ExtractChannel(area, other, 1);
        Cv2.Max(value, other, value);
        Cv2.ExtractChannel(area, other, 2);
        Cv2.Max(value, other, value);
        using var histogram = new Mat();
        Cv2.CalcHist(new[] { value }, new[] { 0 }, null, histogram, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        var half = value.Rows * (double)value.Cols / 2.0;
        var sum = 0.0;
        for (var level = 0; level < 256; level++)
        {
            sum += histogram.At<float>(level);
            if (sum >= half)
                return level;
        }

        return 255;
    }

    public static BackgroundBand BandOf(int value) => value switch
    {
        >= GlareMin => BackgroundBand.Glare,
        >= VeryBrightMin => BackgroundBand.VeryBright,
        >= BrightMin => BackgroundBand.Bright,
        >= MidMin => BackgroundBand.Mid,
        _ => BackgroundBand.Dark,
    };

    public static string Label(BackgroundBand band) => band switch
    {
        BackgroundBand.Dark => $"dark <{MidMin}",
        BackgroundBand.Mid => $"mid {MidMin}-{BrightMin - 1}",
        BackgroundBand.Bright => $"bright {BrightMin}-{VeryBrightMin - 1}",
        BackgroundBand.VeryBright => $"very bright {VeryBrightMin}-{GlareMin - 1}",
        _ => $"glare {GlareMin}+",
    };
}
