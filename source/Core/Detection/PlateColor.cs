
namespace Iris.Core.Detection;

public enum PlateColor
{
    White,
    Blue,
    Green,
    Red,
}

public sealed record PlateColorSettings
{
    public double WhiteSaturation { get; init; } = 0.15;

    public double GreenSaturation { get; init; } = 0.45;

    public double GreenHueFrom { get; init; } = 40.0;

    public double GreenHueTo { get; init; } = 165.0;

    public double RedHueFrom { get; init; } = 330.0;

    public double RedHueTo { get; init; } = 40.0;

    public double BlueHueFrom { get; init; } = 195.0;

    public double BlueHueTo { get; init; } = 270.0;

    public double BlueSaturation { get; init; } = 0.25;

    public static PlateColorSettings Default { get; } = new();
}

public readonly record struct PlateColorStats(double Hue, double Saturation, double Value, int Pixels);

public readonly record struct PlateColorReading(PlateColor Color, double Hue, double Saturation);

public static class PlateColors
{
    public static PlateColorStats? Measure(IEnumerable<LineMask> rows)
    {
        var all = new List<byte[]>();
        var total = 0;
        foreach (var row in rows)
        {
            if (row.InkColors is null)
                continue;
            all.Add(row.InkColors);
            total += row.InkColors.Length / 3;
        }

        if (total < NameplateDetectorSettings.PlateColorMinPixels)
            return null;

        var brightness = new int[256];
        foreach (var colors in all)
        {
            for (var i = 0; i < colors.Length; i += 3)
                brightness[Math.Max(colors[i], Math.Max(colors[i + 1], colors[i + 2]))]++;
        }

        var threshold = ValueAtSortedIndex(brightness, total / 2);

        var blue = new int[256];
        var green = new int[256];
        var red = new int[256];
        var core = 0;
        foreach (var colors in all)
        {
            for (var i = 0; i < colors.Length; i += 3)
            {
                var b = colors[i];
                var g = colors[i + 1];
                var r = colors[i + 2];
                if (Math.Max(b, Math.Max(g, r)) < threshold)
                    continue;
                blue[b]++;
                green[g]++;
                red[r]++;
                core++;
            }
        }

        var bm = Median(blue, core);
        var gm = Median(green, core);
        var rm = Median(red, core);
        var mx = Math.Max(rm, Math.Max(gm, bm));
        var mn = Math.Min(rm, Math.Min(gm, bm));
        var saturation = mx == 0 ? 0.0 : (mx - mn) / mx;
        double hue;
        if (mx == mn)
            hue = 0.0;
        else if (mx == rm)
            hue = Mod360(60.0 * ((gm - bm) / (mx - mn)));
        else if (mx == gm)
            hue = 60.0 * ((bm - rm) / (mx - mn)) + 120.0;
        else
            hue = 60.0 * ((rm - gm) / (mx - mn)) + 240.0;
        return new PlateColorStats(hue, saturation, mx / 255.0, total);
    }

    public static PlateColorReading? Classify(IEnumerable<LineMask> rows, PlateColorSettings? settings = null)
    {
        var stats = Measure(rows);
        if (stats is null)
            return null;
        return new PlateColorReading(Classify(stats.Value.Hue, stats.Value.Saturation, settings), stats.Value.Hue, stats.Value.Saturation);
    }

    public static PlateColor Classify(double hue, double saturation, PlateColorSettings? settings = null)
    {
        var p = settings ?? PlateColorSettings.Default;
        if (saturation < p.WhiteSaturation)
            return PlateColor.White;
        if (saturation >= p.GreenSaturation)
        {
            if (p.GreenHueFrom <= hue && hue < p.GreenHueTo)
                return PlateColor.Green;
            if (hue >= p.RedHueFrom || hue < p.RedHueTo)
                return PlateColor.Red;
        }

        if (p.BlueHueFrom <= hue && hue < p.BlueHueTo && saturation >= p.BlueSaturation)
            return PlateColor.Blue;
        return PlateColor.White;
    }

    private static int ValueAtSortedIndex(int[] histogram, int index)
    {
        var seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (index < seen)
                return value;
        }

        return histogram.Length - 1;
    }

    private static double Median(int[] histogram, int count)
    {
        if (count % 2 == 1)
            return ValueAtSortedIndex(histogram, count / 2);
        return (ValueAtSortedIndex(histogram, count / 2 - 1) + ValueAtSortedIndex(histogram, count / 2)) / 2.0;
    }

    private static double Mod360(double x)
    {
        var r = x % 360.0;
        return r < 0 ? r + 360.0 : r + 0.0;
    }
}
