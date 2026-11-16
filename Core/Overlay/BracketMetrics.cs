namespace Iris.Core.Overlay;

public readonly record struct BracketMetrics(double Top, double Height, double Width, double Gap, double Stroke, double Outline, double Bump, double GlowSigma)
{
    public const double ReferenceScreenHeight = 1440.0;

    public const double MinClearPx = 20.0;

    public const double RailHeightPx = 33.6;

    public const double EndPx = 6.0;

    public const double StrokePx = 3.0;

    public const double OutlinePx = 1.0;

    public const double SmallBumpPx = 4.0;
    public const double SmallBumpHalfPx = 5.0;

    public const double BigBumpPx = 8.0;
    public const double BigBumpHalfPx = 7.5;
    public const double AngleRailShare = 0.5;

    public const double GlowSigmaPx = 3.1;
    public const double GlowSigmas = 3.0;

    public static double Scale(double screenHeight) => screenHeight > 0 ? screenHeight / ReferenceScreenHeight : 1.0;

    public static BracketMetrics For(PixelRect plateBox, double screenHeight)
    {
        var scale = Scale(screenHeight);
        var height = RailHeightPx * scale;
        var stroke = StrokePx * scale;
        var outline = OutlinePx * scale;

        return new BracketMetrics(
            Top: plateBox.Y + plateBox.Height / 2 - height / 2,
            Height: height,
            Width: EndPx * scale,
            Gap: MinClearPx * scale + stroke / 2 + outline,
            Stroke: stroke,
            Outline: outline,
            Bump: BigBumpPx * scale,
            GlowSigma: GlowSigmaPx * scale);
    }

    public double LeftX(PixelRect box) => box.X - Gap - Width;

    public double RightX(PixelRect box) => box.Right + Gap;

    public double Bleed => Stroke / 2 + Outline;

    public double Halo => Math.Max(Bleed, GlowSigma * GlowSigmas);

    public PixelRect Extent(PixelRect box)
    {
        var half = Halo;
        var left = LeftX(box) - Bump - half;
        var right = RightX(box) + Width + Bump + half;
        return new PixelRect(left, Top - half, right - left, Height + 2 * half);
    }

    public static PixelRect ExtentOf(PixelRect plateBox, double screenHeight) => For(plateBox, screenHeight).Extent(plateBox);
}
