
namespace Iris.Core.Overlay;

public sealed class BracketRasterizer
{
    private const int CurveSteps = 8;
    private const float DarkAlpha = 0xE6 / 255f;
    private static readonly (float R, float G, float B) DarkColor = (0x0A, 0x0A, 0x0F);

    private readonly Segments _left = new();
    private readonly Segments _right = new();
    private float[] _coverage = Array.Empty<float>();

    public void Draw(byte[] pixels, int stride, int width, int height, IReadOnlyList<BracketItem> brackets, double originX, double originY, double screenHeight)
    {
        if (width <= 0 || height <= 0 || brackets.Count == 0 || stride < width * 4 || pixels.Length < stride * (height - 1) + width * 4)
            return;
        if (_coverage.Length < width * height)
            _coverage = new float[width * height];

        foreach (var item in brackets)
        {
            var box = item.Box;
            var m = BracketMetrics.For(box, screenHeight);
            BuildShape(_left, item.Kind, m.LeftX(box) - originX, m.Top - originY, m.Width, m.Height, mirrored: false);
            BuildShape(_right, item.Kind, m.RightX(box) - originX, m.Top - originY, m.Width, m.Height, mirrored: true);

            var (r, g, b) = OverlayPalette.Rgb(item.Color);
            var darkRadius = (float)((m.Stroke + 2 * m.Outline) / 2);
            var colorRadius = (float)(m.Stroke / 2);

            Stroke(pixels, stride, width, height, _left, darkRadius, DarkColor.R, DarkColor.G, DarkColor.B, DarkAlpha);
            Stroke(pixels, stride, width, height, _right, darkRadius, DarkColor.R, DarkColor.G, DarkColor.B, DarkAlpha);
            Stroke(pixels, stride, width, height, _left, colorRadius, r, g, b, 1f);
            Stroke(pixels, stride, width, height, _right, colorRadius, r, g, b, 1f);
        }
    }

    private void Stroke(byte[] pixels, int stride, int width, int height, Segments shape, float radius, float r, float g, float b, float alpha)
    {
        if (shape.Count == 0)
            return;
        var coverage = _coverage;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (var s = 0; s < shape.Count; s++)
        {
            var x0 = shape.Values[s * 4];
            var y0 = shape.Values[s * 4 + 1];
            var x1 = shape.Values[s * 4 + 2];
            var y1 = shape.Values[s * 4 + 3];

            var left = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) - radius - 1));
            var right = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) + radius + 1));
            var top = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1) - radius - 1));
            var bottom = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(y0, y1) + radius + 1));
            if (left > right || top > bottom)
                continue;

            minX = Math.Min(minX, left);
            maxX = Math.Max(maxX, right);
            minY = Math.Min(minY, top);
            maxY = Math.Max(maxY, bottom);

            var dx = x1 - x0;
            var dy = y1 - y0;
            var lengthSquared = dx * dx + dy * dy;
            for (var y = top; y <= bottom; y++)
            {
                var py = y + 0.5f;
                var row = y * width;
                for (var x = left; x <= right; x++)
                {
                    var px = x + 0.5f;
                    var t = lengthSquared > 0f ? ((px - x0) * dx + (py - y0) * dy) / lengthSquared : 0f;
                    t = t < 0f ? 0f : t > 1f ? 1f : t;
                    var ex = px - (x0 + t * dx);
                    var ey = py - (y0 + t * dy);
                    var c = radius + 0.5f - MathF.Sqrt(ex * ex + ey * ey);
                    if (c <= 0f)
                        continue;
                    if (c > 1f)
                        c = 1f;
                    if (c > coverage[row + x])
                        coverage[row + x] = c;
                }
            }
        }

        if (minX > maxX)
            return;
        for (var y = minY; y <= maxY; y++)
        {
            var row = y * width;
            for (var x = minX; x <= maxX; x++)
            {
                var c = coverage[row + x];
                if (c <= 0f)
                    continue;
                coverage[row + x] = 0f;
                var a = c * alpha;
                var inverse = 1f - a;
                var i = y * stride + x * 4;
                pixels[i] = ToByte(b * a + pixels[i] * inverse);
                pixels[i + 1] = ToByte(g * a + pixels[i + 1] * inverse);
                pixels[i + 2] = ToByte(r * a + pixels[i + 2] * inverse);
                pixels[i + 3] = ToByte(255f * a + pixels[i + 3] * inverse);
            }
        }
    }

    private static byte ToByte(float value) => value >= 255f ? (byte)255 : value <= 0f ? (byte)0 : (byte)(value + 0.5f);

    private static void BuildShape(Segments shape, MatchKind kind, double x, double y, double width, double height, bool mirrored)
    {
        shape.Count = 0;
        (float X, float Y) P(double u, double v) => ((float)(mirrored ? x + (1 - u) * width : x + u * width), (float)(y + v * height));

        switch (kind)
        {
            case MatchKind.PlayerList:
            {
                var current = P(1, 0);
                current = Curve(shape, current, P(0.5, 0), P(0.5, 0.12));
                current = Line(shape, current, P(0.5, 0.38));
                current = Curve(shape, current, P(0.5, 0.5), P(0, 0.5));
                current = Curve(shape, current, P(0.5, 0.5), P(0.5, 0.62));
                current = Line(shape, current, P(0.5, 0.88));
                Curve(shape, current, P(0.5, 1), P(1, 1));
                break;
            }

            case MatchKind.OrgList:
            {
                var current = P(1, 0);
                current = Line(shape, current, P(0, 0));
                current = Line(shape, current, P(0, 1));
                Line(shape, current, P(1, 1));
                break;
            }

            default:
            {
                var current = P(1, 0);
                current = Line(shape, current, P(0, 0.5));
                Line(shape, current, P(1, 1));
                break;
            }
        }
    }

    private static (float X, float Y) Line(Segments shape, (float X, float Y) from, (float X, float Y) to)
    {
        shape.Add(from.X, from.Y, to.X, to.Y);
        return to;
    }

    private static (float X, float Y) Curve(Segments shape, (float X, float Y) from, (float X, float Y) control, (float X, float Y) to)
    {
        var previous = from;
        for (var i = 1; i <= CurveSteps; i++)
        {
            var t = i / (float)CurveSteps;
            var mt = 1f - t;
            var point = (X: mt * mt * from.X + 2f * mt * t * control.X + t * t * to.X, Y: mt * mt * from.Y + 2f * mt * t * control.Y + t * t * to.Y);
            shape.Add(previous.X, previous.Y, point.X, point.Y);
            previous = point;
        }

        return to;
    }

    private sealed class Segments
    {
        public float[] Values = new float[4 * 48];
        public int Count;

        public void Add(float x0, float y0, float x1, float y1)
        {
            if ((Count + 1) * 4 > Values.Length)
                Array.Resize(ref Values, Values.Length * 2);
            var i = Count * 4;
            Values[i] = x0;
            Values[i + 1] = y0;
            Values[i + 2] = x1;
            Values[i + 3] = y1;
            Count++;
        }
    }
}
