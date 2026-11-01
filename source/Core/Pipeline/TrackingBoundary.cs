using Iris.Core.Overlay;
using Iris.Core.Config;

namespace Iris.Core.Pipeline;

public readonly record struct TrackingBoundary(double Left, double Top, double Right, double Bottom)
{
    public static TrackingBoundary Default => FromPercents(10, 30, 15, 15);

    public static TrackingBoundary FromPercents(int top, int bottom, int left, int right)
    {
        static int Limit(int value) => Math.Clamp(value, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax);
        return new TrackingBoundary(Limit(left) / 100.0, Limit(top) / 100.0, (100 - Limit(right)) / 100.0, (100 - Limit(bottom)) / 100.0);
    }

    public static TrackingBoundary From(AppSettings settings) =>
        FromPercents(settings.BoundaryTop, settings.BoundaryBottom, settings.BoundaryLeft, settings.BoundaryRight);

    public PixelRect ToPixels(double windowWidth, double windowHeight) =>
        PixelRect.FromEdges(Left * windowWidth, Top * windowHeight, Right * windowWidth, Bottom * windowHeight);

    public static double DropDistancePx(double windowHeight) => LiveSettings.BoundaryDropLines * LiveSettings.LineHeightAt1440 * windowHeight / 1440.0;

    public bool ContainsCenterOf(PixelRect box, double windowWidth, double windowHeight) =>
        DistanceOutside(box, windowWidth, windowHeight) <= 0.0;

    public double DistanceOutside(PixelRect box, double windowWidth, double windowHeight)
    {
        var x = box.X + box.Width / 2.0;
        var y = box.Y + box.Height / 2.0;
        var dx = Math.Max(Math.Max(Left * windowWidth - x, x - Right * windowWidth), 0.0);
        var dy = Math.Max(Math.Max(Top * windowHeight - y, y - Bottom * windowHeight), 0.0);
        return Math.Max(dx, dy);
    }
}
