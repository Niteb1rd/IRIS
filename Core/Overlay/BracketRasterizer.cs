
namespace Iris.Core.Overlay;

public sealed class BracketRasterizer
{
    public const float Opacity = 0.7f;

    public const float DarkAlpha = 0.55f;

    public const float GlowAlpha = 0.45f;
    public const float GlowPeak = 0.64f;

    private const int CurveSteps = 4;
    private static readonly (float R, float G, float B) DarkColor = (0x0A, 0x0A, 0x0F);

    private readonly Segments _left = new();
    private readonly Segments _right = new();
    private float[] _coverage = Array.Empty<float>();
    private const int GlowSteps = 256;
    private readonly float[] _glow = new float[GlowSteps + 1];
    private (float Radius, float Sigma, float Peak) _glowFor;

    public void Draw(byte[] pixels, int stride, int width, int height, IReadOnlyList<BracketItem> brackets, double originX, double originY, double screenHeight)
    {
        if (width <= 0 || height <= 0 || brackets.Count == 0 || stride < width * 4 || pixels.Length < stride * (height - 1) + width * 4)
            return;
        if (_coverage.Length < width * height)
            _coverage = new float[width * height];

        var scale = BracketMetrics.Scale(screenHeight);
        foreach (var item in brackets)
        {
            var box = item.Box;
            var m = BracketMetrics.For(box, screenHeight);
            BuildShape(_left, item.Kind, m.LeftX(box) - originX, m.Top - originY, m.Width, m.Height, scale, inward: 1);
            BuildShape(_right, item.Kind, m.RightX(box) + m.Width - originX, m.Top - originY, m.Width, m.Height, scale, inward: -1);

            var (r, g, b) = OverlayPalette.Rgb(item.Color);
            var darkRadius = (float)m.Bleed;
            var colorRadius = (float)(m.Stroke / 2);
            var sigma = (float)m.GlowSigma;
            var reach = (float)(m.GlowSigma * BracketMetrics.GlowSigmas);

            Stroke(pixels, stride, width, height, _left, darkRadius, 0f, 0f, DarkColor.R, DarkColor.G, DarkColor.B, DarkAlpha);
            Stroke(pixels, stride, width, height, _right, darkRadius, 0f, 0f, DarkColor.R, DarkColor.G, DarkColor.B, DarkAlpha);
            Stroke(pixels, stride, width, height, _left, reach, sigma, GlowPeak, r, g, b, GlowAlpha);
            Stroke(pixels, stride, width, height, _right, reach, sigma, GlowPeak, r, g, b, GlowAlpha);
            Stroke(pixels, stride, width, height, _left, colorRadius, 0f, 0f, r, g, b, Opacity);
            Stroke(pixels, stride, width, height, _right, colorRadius, 0f, 0f, r, g, b, Opacity);
        }
    }

    private void Stroke(byte[] pixels, int stride, int width, int height, Segments shape, float radius, float sigma, float peak, float r, float g, float b, float alpha)
    {
        var glow = sigma > 0f;
        var radiusSquared = radius * radius;
        var glowStep = glow ? radiusSquared / GlowSteps : 0f;
        if (glow && _glowFor != (radius, sigma, peak))
        {
            for (var i = 0; i <= GlowSteps; i++)
                _glow[i] = peak * MathF.Exp(-(i * glowStep) / (2f * sigma * sigma));
            _glowFor = (radius, sigma, peak);
        }
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
                    var dSquared = ex * ex + ey * ey;
                    float c;
                    if (glow)
                    {
                        if (dSquared >= radiusSquared)
                            continue;
                        c = _glow[(int)(dSquared / glowStep)];
                    }
                    else
                    {
                        c = radius + 0.5f - MathF.Sqrt(dSquared);
                        if (c <= 0f)
                            continue;
                        if (c > 1f)
                            c = 1f;
                    }
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

    private static void BuildShape(Segments shape, MatchKind kind, double railX, double top, double ends, double height, double scale, int inward)
    {
        shape.Count = 0;
        (float X, float Y) P(double x, double y) => ((float)x, (float)y);
        var x = railX;
        var mid = top + height / 2;
        var bottom = top + height;

        switch (kind)
        {
            case MatchKind.PlayerList:
            {
                var bump = BracketMetrics.SmallBumpPx * scale;
                var half = BracketMetrics.SmallBumpHalfPx * scale;
                var current = Curve(shape, P(x + inward * ends, top), P(x, top), P(x, top + ends));
                current = Line(shape, current, P(x, mid - half));
                current = Line(shape, current, P(x - inward * bump, mid));
                current = Line(shape, current, P(x, mid + half));
                current = Line(shape, current, P(x, bottom - ends));
                Curve(shape, current, P(x, bottom), P(x + inward * ends, bottom));
                break;
            }

            case MatchKind.OrgList:
            {
                var current = Line(shape, P(x + inward * ends, top), P(x, top));
                current = Line(shape, current, P(x, bottom));
                Line(shape, current, P(x + inward * ends, bottom));
                break;
            }

            default:
            {
                var bump = BracketMetrics.BigBumpPx * scale;
                var half = BracketMetrics.BigBumpHalfPx * scale;
                var rail = Math.Max(0.0, height / 2 - half) * BracketMetrics.AngleRailShare;
                var current = Line(shape, P(x, mid - half - rail), P(x, mid - half));
                current = Line(shape, current, P(x - inward * bump, mid));
                current = Line(shape, current, P(x, mid + half));
                Line(shape, current, P(x, mid + half + rail));
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
