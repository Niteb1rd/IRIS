namespace Iris.Core.Overlay;

public readonly record struct BracketMetrics(double Top, double Height, double Width, double Gap, double Stroke, double Outline)
{
    public const double ReferenceScreenHeight = 1440.0;

    public const double ReferenceBoxHeight = 41.0;

    public const double MinClearPx = 10.0;

    public static double Scale(double screenHeight) => screenHeight > 0 ? screenHeight / ReferenceScreenHeight : 1.0;

    public static BracketMetrics For(PixelRect plateBox, double screenHeight)
    {
        var scale = Scale(screenHeight);

        var height = ReferenceBoxHeight * 1.24;
        var stroke = Math.Max(2.0, height * 0.06);
        var outline = Math.Max(1.0, stroke * 0.5);
        var width = height * 0.24;

        height *= scale;
        width *= scale;
        stroke *= scale;
        outline *= scale;

        return new BracketMetrics(
            Top: plateBox.Y + plateBox.Height / 2 - height / 2,
            Height: height,
            Width: width,
            Gap: MinClearPx * scale + stroke / 2 + outline,
            Stroke: stroke,
            Outline: outline);
    }

    public double LeftX(PixelRect box) => box.X - Gap - Width;

    public double RightX(PixelRect box) => box.Right + Gap;

    public double Bleed => Stroke / 2 + Outline;

    public PixelRect Extent(PixelRect box)
    {
        var half = Bleed;
        var left = LeftX(box) - half;
        var right = RightX(box) + Width + half;
        return new PixelRect(left, Top - half, right - left, Height + 2 * half);
    }

    public static PixelRect ExtentOf(PixelRect plateBox, double screenHeight) => For(plateBox, screenHeight).Extent(plateBox);
}
