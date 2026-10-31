using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class CropImage : IDisposable
{
    private readonly bool _owns;

    public CropImage(Mat image, Rect origin, bool owns = true)
    {
        Image = image;
        Origin = origin;
        _owns = owns;
    }

    public Mat Image { get; }

    public Rect Origin { get; }

    public void Dispose()
    {
        if (_owns)
            Image.Dispose();
    }
}

public sealed record CheckItem(int Id, CropImage Crop, PixelRect Expected, double PredictedX, double PredictedY, double Margin, double LineHeight);

public sealed record ReadItem(int Id, CropImage Crop, PixelRect Expected, double LineHeight);
