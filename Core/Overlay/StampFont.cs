namespace Iris.Core.Overlay;

public static class StampFont
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;
    public const int Padding = 2;

    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E },
        ['1'] = new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E },
        ['2'] = new byte[] { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F },
        ['3'] = new byte[] { 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E },
        ['4'] = new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 },
        ['5'] = new byte[] { 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E },
        ['6'] = new byte[] { 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E },
        ['7'] = new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 },
        ['8'] = new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E },
        ['9'] = new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C },
        ['R'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11 },
        ['F'] = new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 },
        ['T'] = new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 },
        ['.'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C },
        [':'] = new byte[] { 0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00 },
        ['-'] = new byte[] { 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00 },
    };

    public static (int Width, int Height) Measure(string text, int scale) =>
        (((text.Length * (GlyphWidth + 1)) - 1 + 2 * Padding) * scale, (GlyphHeight + 2 * Padding) * scale);

    public static void Draw(byte[] pixels, int stride, int width, int height, string text, int scale)
    {
        var (w, h) = Measure(text, scale);
        w = Math.Min(w, width);
        h = Math.Min(h, height);
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
                Set(pixels, row + x * 4, 0, 200);
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!Glyphs.TryGetValue(text[i], out var glyph))
                continue;
            var left = (Padding + i * (GlyphWidth + 1)) * scale;
            for (var gy = 0; gy < GlyphHeight; gy++)
            {
                for (var gx = 0; gx < GlyphWidth; gx++)
                {
                    if ((glyph[gy] & (0x10 >> gx)) == 0)
                        continue;
                    for (var sy = 0; sy < scale; sy++)
                    {
                        var y = (Padding + gy) * scale + sy;
                        if (y >= h)
                            continue;
                        for (var sx = 0; sx < scale; sx++)
                        {
                            var x = left + gx * scale + sx;
                            if (x < w)
                                Set(pixels, y * stride + x * 4, 255, 255);
                        }
                    }
                }
            }
        }
    }

    private static void Set(byte[] pixels, int at, byte value, byte alpha)
    {
        var premultiplied = (byte)(value * alpha / 255);
        pixels[at] = premultiplied;
        pixels[at + 1] = premultiplied;
        pixels[at + 2] = premultiplied;
        pixels[at + 3] = alpha;
    }
}
