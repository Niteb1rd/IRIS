
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.App.Views;

internal sealed class PanelCurve : IDisposable
{
    private Mat? _source;
    private Mat? _mapX;
    private Mat? _mapY;
    private float[]? _x;
    private float[]? _y;
    private (int Width, int Height, double Left, double Top, double WindowWidth, double WindowHeight, double Depth) _key;

    public void Draw(BitmapSource picture, IntPtr bits, int stride, int width, int height, PixelRect fit, double windowWidth, double windowHeight, double depth)
    {
        EnsureMaps(width, height, fit, windowWidth, windowHeight, depth);
        var source = _source!;
        picture.CopyPixels(new Int32Rect(0, 0, width, height), source.Data, (int)(source.Step() * height), (int)source.Step());
        using var target = Mat.FromPixelData(height, width, MatType.CV_8UC4, bits, stride);
        Cv2.Remap(source, target, _mapX!, _mapY!, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
    }

    public void Dispose()
    {
        _source?.Dispose();
        _mapX?.Dispose();
        _mapY?.Dispose();
        _source = _mapX = _mapY = null;
    }

    private void EnsureMaps(int width, int height, PixelRect fit, double windowWidth, double windowHeight, double depth)
    {
        var key = (width, height, fit.X, fit.Y, windowWidth, windowHeight, depth);
        if (_source is not null && key == _key)
            return;
        if (_source is null || _source.Width != width || _source.Height != height)
        {
            Dispose();
            _source = new Mat(height, width, MatType.CV_8UC4);
            _mapX = new Mat(height, width, MatType.CV_32FC1);
            _mapY = new Mat(height, width, MatType.CV_32FC1);
            _x = new float[width * height];
            _y = new float[width * height];
        }
        ScreenCurve.SourceMaps(fit.X, fit.Y, width, height, windowWidth, windowHeight, depth, _x!, _y!);
        Marshal.Copy(_x!, 0, _mapX!.Data, _x!.Length);
        Marshal.Copy(_y!, 0, _mapY!.Data, _y!.Length);
        _key = key;
    }
}
