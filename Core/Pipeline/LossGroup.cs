using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public enum LossKind
{
    Name,
    Mining,
}

public sealed record LossShot(string Role, Mat Image, Rect Window, long Sequence, double FrameMs);

public sealed class LossGroup : IDisposable
{
    public LossGroup(LossKind kind, bool unsure, string label, int markerId, IReadOnlyList<LossShot> shots, string sidecar)
    {
        Kind = kind;
        Unsure = unsure;
        Label = label;
        MarkerId = markerId;
        Shots = shots;
        Sidecar = sidecar;
    }

    public LossKind Kind { get; }

    public bool Unsure { get; }

    public string Label { get; }

    public int MarkerId { get; }

    public IReadOnlyList<LossShot> Shots { get; }

    public string Sidecar { get; }

    public void Dispose()
    {
        foreach (var shot in Shots)
            shot.Image.Dispose();
    }

    public static (Mat Image, Rect Window)? Cut(Mat image, Rect region, PixelRect wanted, double minShare = 0.5)
    {
        if (image.Empty() || wanted.IsEmpty)
            return null;
        var left = (int)Math.Floor(wanted.X);
        var top = (int)Math.Floor(wanted.Y);
        var right = (int)Math.Ceiling(wanted.Right);
        var bottom = (int)Math.Ceiling(wanted.Bottom);
        var x0 = Math.Max(left, region.X);
        var y0 = Math.Max(top, region.Y);
        var x1 = Math.Min(right, region.X + Math.Min(region.Width, image.Width));
        var y1 = Math.Min(bottom, region.Y + Math.Min(region.Height, image.Height));
        if (x1 <= x0 || y1 <= y0)
            return null;
        var area = (double)(right - left) * (bottom - top);
        if (area <= 0 || (x1 - x0) * (double)(y1 - y0) < minShare * area)
            return null;
        using var view = new Mat(image, new Rect(x0 - region.X, y0 - region.Y, x1 - x0, y1 - y0));
        return (view.Clone(), new Rect(x0, y0, x1 - x0, y1 - y0));
    }

    public static string FileSafe(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
}
