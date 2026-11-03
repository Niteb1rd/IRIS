using Iris.Core.Detection;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class PlateSeedBuilder : IDisposable
{
    private readonly PlateMasker _masker;
    private readonly Mat _mask = new();
    private bool _disposed;

    public PlateSeedBuilder(NameplateDetectorSettings? settings = null) => _masker = new PlateMasker(settings);

    public Mat? MakeTemplate(Mat image, Rect box, int scaleHeight, out PlateColorClass cls)
    {
        cls = PlateColorClass.Any;
        var bounds = new Rect(0, 0, image.Width, image.Height);
        var inside = PlateMasker.Intersect(box, bounds);
        if (inside.Width <= 0 || inside.Height <= 0)
            return null;

        var margin = _masker.KernelSize(scaleHeight) / 2 + 2;
        var context = PlateMasker.Intersect(new Rect(inside.X - margin, inside.Y - margin, inside.Width + 2 * margin, inside.Height + 2 * margin), bounds);
        using var crop = new Mat(image, context);
        var local = new Rect(inside.X - context.X, inside.Y - context.Y, inside.Width, inside.Height);

        cls = _masker.Classify(crop, scaleHeight, local);
        _masker.Build(crop, scaleHeight, cls, _mask);
        using var part = new Mat(_mask, local);
        if (Cv2.CountNonZero(part) == 0)
            return null;
        return part.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mask.Dispose();
        _masker.Dispose();
    }
}
