using System.Globalization;

namespace Iris.Core.Overlay;

public readonly record struct ResourceReading(double? CpuPercent, long MemoryBytes, double? GpuPercent);

public static class ResourceUsage
{
    private const string ProcessPrefix = "pid_";
    private const string EngineTypeMarker = "_engtype_";
    private const long BytesPerMegabyte = 1024L * 1024L;

    public static double CpuPercent(TimeSpan cpuTime, TimeSpan elapsed, int processors)
    {
        if (elapsed <= TimeSpan.Zero || processors <= 0)
            return 0.0;
        return Math.Clamp(100.0 * cpuTime.TotalMilliseconds / (elapsed.TotalMilliseconds * processors), 0.0, 100.0);
    }

    public static double GpuPercent(IEnumerable<(string Instance, double Percent)> engines, int processId)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{ProcessPrefix}{processId}_");
        var byType = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (instance, percent) in engines)
        {
            if (!instance.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var at = instance.IndexOf(EngineTypeMarker, StringComparison.Ordinal);
            if (at < 0)
                continue;
            var type = instance[(at + EngineTypeMarker.Length)..];
            byType[type] = byType.GetValueOrDefault(type) + percent;
        }

        return byType.Count == 0 ? 0.0 : Math.Clamp(byType.Values.Max(), 0.0, 100.0);
    }

    public static string Text(ResourceReading reading)
    {
        var cpu = "CPU " + Percent(reading.CpuPercent) + "%";
        var memory = string.Create(CultureInfo.InvariantCulture, $"MEM {reading.MemoryBytes / BytesPerMegabyte}MB");
        var gpu = "GPU " + Percent(reading.GpuPercent) + "%";
        return string.Create(CultureInfo.InvariantCulture, $"{cpu,-8} {memory,-10} {gpu,-8}");
    }

    private static string Percent(double? value) =>
        value is { } known ? Math.Round(known, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "--";
}

public static class ResourceOverlayPlacement
{
    public const int Unset = -1;
    public const int DefaultRightPx = 12;
    public const int DefaultTopPx = 8;

    private const double WindowHeightPerScale = 1440.0;

    public static int GlyphScale(double windowHeight) =>
        Math.Max(1, (int)Math.Round(windowHeight / WindowHeightPerScale, MidpointRounding.AwayFromZero));

    public static (int X, int Y) Place(PixelRect screen, int width, int height, int rightPx, int topPx)
    {
        var (left, top, maxX, maxY) = Limits(screen, width, height);
        var x = maxX - (rightPx == Unset ? DefaultRightPx : rightPx);
        var y = top + (topPx == Unset ? DefaultTopPx : topPx);
        return (Math.Clamp(x, left, maxX), Math.Clamp(y, top, maxY));
    }

    public static (int RightPx, int TopPx) Offsets(PixelRect screen, int width, int height, double x, double y)
    {
        var (left, top, maxX, maxY) = Limits(screen, width, height);
        return (maxX - Math.Clamp((int)Math.Round(x), left, maxX), Math.Clamp((int)Math.Round(y), top, maxY) - top);
    }

    private static (int Left, int Top, int MaxX, int MaxY) Limits(PixelRect screen, int width, int height)
    {
        var left = (int)Math.Round(screen.X);
        var top = (int)Math.Round(screen.Y);
        return (left, top, Math.Max(left, (int)Math.Round(screen.Right) - width), Math.Max(top, (int)Math.Round(screen.Bottom) - height));
    }
}
