using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public readonly record struct LineFix(double CentreX, double Top, bool Refined);

public static class LineSubPixel
{
    private const int PadX = 2;
    private const int PadY = 1;

    [ThreadStatic]
    private static byte[]? _window;

    public static LineFix Measure(Mat bgr, Rect box)
    {
        var fallback = new LineFix(box.X + box.Width / 2.0, box.Y, false);
        if (bgr.Empty() || bgr.Type() != MatType.CV_8UC3 || box.Width < 2 || box.Height < 2)
            return fallback;
        if (box.X < 0 || box.Y < 0 || box.X + box.Width > bgr.Width || box.Y + box.Height > bgr.Height)
            return fallback;

        var x0 = Math.Max(0, box.X - PadX);
        var y0 = Math.Max(0, box.Y - PadY);
        var x1 = Math.Min(bgr.Width, box.X + box.Width + PadX);
        var y1 = Math.Min(bgr.Height, box.Y + box.Height + PadY);
        var width = x1 - x0;
        var height = y1 - y0;
        var stride = width * 3;
        var buffer = _window;
        if (buffer is null || buffer.Length < stride * height)
            _window = buffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(bgr.Ptr(y0 + y), x0 * 3), buffer, y * stride, stride);

        var fix = Measure(buffer, stride, width, height, box.X - x0, box.Y - y0, box.Width, box.Height);
        return fix.Refined ? new LineFix(fix.CentreX + x0, fix.Top + y0, true) : fallback;
    }

    public static LineFix Measure(ReadOnlySpan<byte> bgr, int stride, int width, int height, int boxX, int boxY, int boxWidth, int boxHeight)
    {
        var fallback = new LineFix(boxX + boxWidth / 2.0, boxY, false);
        if (boxX < 0 || boxY < 0 || boxX + boxWidth > width || boxY + boxHeight > height || boxWidth < 2 || boxHeight < 2)
            return fallback;

        Span<int> columns = width <= 1024 ? stackalloc int[width] : new int[width];
        Span<int> rows = height <= 256 ? stackalloc int[height] : new int[height];
        for (var x = 0; x < width; x++)
        {
            var max = 0;
            for (var y = boxY; y < boxY + boxHeight; y++)
                max = Math.Max(max, Brightness(bgr, y * stride + x * 3));
            columns[x] = max;
        }

        for (var y = 0; y < height; y++)
        {
            var max = 0;
            for (var x = boxX; x < boxX + boxWidth; x++)
                max = Math.Max(max, Brightness(bgr, y * stride + x * 3));
            rows[y] = max;
        }

        if (!Leading(columns, reverse: false, out var left) || !Leading(columns, reverse: true, out var rightGap) || !Leading(rows, reverse: false, out var top))
            return fallback;

        var right = width - rightGap;
        var centre = (left + right) / 2.0;
        if (right - left < PipelineSettings.SubPixelMinWidthFraction * boxWidth)
            return fallback;
        if (Math.Abs(centre - fallback.CentreX) > Math.Max(PipelineSettings.SubPixelMaxShiftPx, PipelineSettings.SubPixelMaxShiftLines * boxHeight) || Math.Abs(top - boxY) > PipelineSettings.SubPixelMaxShiftPx)
            return fallback;
        return new LineFix(centre, top, true);
    }

    private static int Brightness(ReadOnlySpan<byte> bgr, int index) => Math.Max(bgr[index], Math.Max(bgr[index + 1], bgr[index + 2]));

    private static bool Leading(ReadOnlySpan<int> profile, bool reverse, out double edge)
    {
        edge = 0.0;
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var value in profile)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        var range = (double)(max - min);
        if (range < PipelineSettings.SubPixelMinContrast)
            return false;
        var missing = 0.0;
        for (var n = 0; n < profile.Length; n++)
        {
            var value = profile[reverse ? profile.Length - 1 - n : n];
            var coverage = Math.Clamp((value - min) / range, 0.0, 1.0);
            if (coverage >= PipelineSettings.SubPixelFullCoverage)
            {
                edge = missing;
                return n > 0;
            }

            missing += 1.0 - coverage;
        }

        return false;
    }
}
