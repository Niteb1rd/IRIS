using Iris.Core.Detection;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public enum PlateColorClass
{
    Any,

    CyanWhite,

    Red,

    Green,
}

public sealed class PlateMasker : IDisposable
{
    private readonly NameplateDetectorSettings _settings;
    private readonly Dictionary<int, Mat> _kernels = new();
    private readonly Mat _blue = new();
    private readonly Mat _green = new();
    private readonly Mat _red = new();
    private readonly Mat _hat = new();
    private readonly Mat _scratch = new();
    private readonly Mat _cyanWhite = new();
    private readonly Mat _redMask = new();
    private readonly Mat _greenMask = new();
    private bool _disposed;

    public PlateMasker(NameplateDetectorSettings? settings = null) => _settings = settings ?? NameplateDetectorSettings.Default;

    public int KernelSize(int scaleHeight)
    {
        var k = _settings.Scale(scaleHeight, _settings.TopHatKernelPx);
        return k % 2 == 1 ? k : k + 1;
    }

    public void Build(Mat bgr, int scaleHeight, PlateColorClass cls, Mat dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Split(bgr);
        var kernel = Kernel(KernelSize(scaleHeight));
        switch (cls)
        {
            case PlateColorClass.CyanWhite:
                BuildCyanWhite(kernel);
                _cyanWhite.CopyTo(dst);
                break;
            case PlateColorClass.Red:
                BuildRed(kernel);
                _redMask.CopyTo(dst);
                break;
            case PlateColorClass.Green:
                BuildGreen(kernel);
                _greenMask.CopyTo(dst);
                break;
            default:
                BuildCyanWhite(kernel);
                BuildRed(kernel);
                BuildGreen(kernel);
                Cv2.BitwiseOr(_cyanWhite, _redMask, dst);
                Cv2.BitwiseOr(dst, _greenMask, dst);
                break;
        }
    }

    public PlateColorClass Classify(Mat bgr, int scaleHeight, Rect box, int minPixels = NameplateDetectorSettings.PlateMaskMinPixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Split(bgr);
        var kernel = Kernel(KernelSize(scaleHeight));
        BuildCyanWhite(kernel);
        BuildRed(kernel);
        BuildGreen(kernel);

        var bounds = new Rect(0, 0, bgr.Width, bgr.Height);
        var inside = Intersect(box, bounds);
        if (inside.Width <= 0 || inside.Height <= 0)
            return PlateColorClass.Any;

        int Count(Mat mask)
        {
            using var part = new Mat(mask, inside);
            return Cv2.CountNonZero(part);
        }

        var cyan = Count(_cyanWhite);
        var red = Count(_redMask);
        var green = Count(_greenMask);
        var best = Math.Max(cyan, Math.Max(red, green));
        if (best < minPixels)
            return PlateColorClass.Any;
        var winners = (cyan == best ? 1 : 0) + (red == best ? 1 : 0) + (green == best ? 1 : 0);
        if (winners > 1)
            return PlateColorClass.Any;
        return cyan == best ? PlateColorClass.CyanWhite : red == best ? PlateColorClass.Red : PlateColorClass.Green;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mat in new[] { _blue, _green, _red, _hat, _scratch, _cyanWhite, _redMask, _greenMask })
            mat.Dispose();
        foreach (var kernel in _kernels.Values)
            kernel.Dispose();
        _kernels.Clear();
    }

    internal static Rect Intersect(Rect a, Rect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right <= left || bottom <= top ? new Rect() : new Rect(left, top, right - left, bottom - top);
    }

    private void Split(Mat bgr)
    {
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));
        Cv2.ExtractChannel(bgr, _blue, 0);
        Cv2.ExtractChannel(bgr, _green, 1);
        Cv2.ExtractChannel(bgr, _red, 2);
    }

    private Mat Kernel(int size)
    {
        if (!_kernels.TryGetValue(size, out var kernel))
        {
            kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(size, size));
            _kernels[size] = kernel;
        }

        return kernel;
    }


    private void BuildCyanWhite(Mat kernel)
    {
        Cv2.MorphologyEx(_blue, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _cyanWhite, _settings.TopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_blue, _scratch, _settings.CyanBlueMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
        Cv2.Threshold(_green, _scratch, _settings.CyanGreenMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
        Cv2.Subtract(_red, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, 0, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
    }

    private void BuildRed(Mat kernel)
    {
        Cv2.MorphologyEx(_red, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _redMask, _settings.TopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_red, _scratch, _settings.RedMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
        Cv2.Subtract(_red, _green, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.RedOverGreenMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
        Cv2.Subtract(_red, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.RedOverBlueMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
    }

    private void BuildGreen(Mat kernel)
    {
        Cv2.MorphologyEx(_green, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _greenMask, _settings.GreenTopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_green, _scratch, _settings.GreenMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
        Cv2.Subtract(_green, _red, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.GreenOverRedMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
        Cv2.Subtract(_green, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.GreenOverBlueMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
    }
}
