namespace Iris.Core.Overlay;

public static class ScreenCurve
{
    public const double DepthAt100 = 0.02;

    public static double DepthFor(int percent) => Math.Max(0, percent) / 100.0 * DepthAt100;

    public static double Factor(double y, double height, double depth)
    {
        if (depth <= 0 || height <= 0)
            return 1;
        var v = (2 * y - height) / height;
        return (1 - depth) / (1 - depth * v * v);
    }

    public static (double X, double Y) Map(double x, double y, double width, double height, double depth)
    {
        var s = Factor(y, height, depth);
        return (width / 2 + (x - width / 2) * s, height / 2 + (y - height / 2) * s);
    }

    public static (double X, double Y) Unmap(double x, double y, double width, double height, double depth)
    {
        if (depth <= 0 || height <= 0)
            return (x, y);
        var half = height / 2;
        var drawn = (y - half) / half;
        var v = drawn == 0 ? 0 : (-(1 - depth) + Math.Sqrt((1 - depth) * (1 - depth) + 4 * depth * drawn * drawn)) / (2 * depth * drawn);
        var s = (1 - depth) / (1 - depth * v * v);
        return (width / 2 + (x - width / 2) / s, half + v * half);
    }

    public static double MaxShiftPx(double width, double depth) => Math.Max(0, depth) * width / 2;

    public static void SourceMaps(double left, double top, int mapWidth, int mapHeight, double windowWidth, double windowHeight, double depth, float[] mapX, float[] mapY)
    {
        if (mapX.Length < mapWidth * mapHeight || mapY.Length < mapWidth * mapHeight)
            throw new ArgumentException("The maps are smaller than the picture.");
        for (var row = 0; row < mapHeight; row++)
        {
            var (_, flatY) = Unmap(windowWidth / 2, top + row, windowWidth, windowHeight, depth);
            var s = Factor(flatY, windowHeight, depth);
            var sourceY = (float)(flatY - top);
            var start = row * mapWidth;
            for (var col = 0; col < mapWidth; col++)
            {
                mapX[start + col] = (float)(windowWidth / 2 + (left + col - windowWidth / 2) / s - left);
                mapY[start + col] = sourceY;
            }
        }
    }
}
