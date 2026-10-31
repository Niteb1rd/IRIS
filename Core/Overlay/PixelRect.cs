namespace Iris.Core.Overlay;

public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);
}
