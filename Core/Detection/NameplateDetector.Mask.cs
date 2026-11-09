
using System.Runtime.InteropServices;
using Iris.Core.Pipeline;
using OpenCvSharp;

namespace Iris.Core.Detection;

public sealed partial class NameplateDetector
{
    private void BuildColorMask(Mat region, int scaleHeight)
    {
        if (Workers is { Threads: > 1 } workers)
        {
            var tophatSize = ScaledOdd(scaleHeight, _settings.TopHatKernelPx);
            var shrink = Scaled(scaleHeight, _settings.BackgroundShrinkPx);
            var overlap = Math.Max((_settings.BackgroundBlur / 2 + 2) * shrink, 2 * tophatSize + 2);
            var plan = MaskBands.Plan(region.Rows, workers.Threads, overlap, shrink);
            if (plan.Count > 1)
            {
                BuildMaskInBands(region, scaleHeight, plan, workers);
                return;
            }
        }

        BuildWholeMask(region, scaleHeight);
    }

    private void BuildMaskInBands(Mat region, int scaleHeight, IReadOnlyList<MaskBand> plan, LowPriorityWorkers workers)
    {
        if (_bands is null || _bands.Length != plan.Count)
        {
            DisposeBands();
            _bands = new NameplateDetector[plan.Count];
            for (var i = 0; i < _bands.Length; i++)
                _bands[i] = new NameplateDetector(_reader, _settings);
        }

        var countInk = ContrastSink is not null;
        var bands = _bands;
        workers.Run(plan.Count, i =>
        {
            using var part = new Mat(region, new Rect(0, plan[i].From, region.Cols, plan[i].Rows));
            bands[i]._countContrastInk = countInk;
            bands[i]._inkKeepFrom = plan[i].KeepFrom - plan[i].From;
            bands[i]._inkKeepRows = plan[i].KeepRows;
            bands[i].BuildWholeMask(part, scaleHeight);
        });

        _mask.Create(region.Rows, region.Cols, MatType.CV_8UC1);
        var contrastInk = 0;
        var sawContrast = false;
        for (var i = 0; i < plan.Count; i++)
        {
            var band = plan[i];
            using var kept = new Mat(bands[i]._mask, new Rect(0, band.KeepFrom - band.From, region.Cols, band.KeepRows));
            using var target = new Mat(_mask, new Rect(0, band.KeepFrom, region.Cols, band.KeepRows));
            kept.CopyTo(target);
            if (bands[i]._lastContrastInk is { } ink)
            {
                contrastInk += ink;
                sawContrast = true;
            }
        }

        if (sawContrast)
            ContrastSink?.AddContrastMask(Cv2.CountNonZero(_mask), contrastInk);
    }

    private void DisposeBands()
    {
        if (_bands is null)
            return;
        foreach (var band in _bands)
            band.Dispose();
        _bands = null;
    }

    private void BuildWholeMask(Mat region, int scaleHeight)
    {
        _lastContrastInk = null;
        var tophat = ScaledOdd(scaleHeight, _settings.TopHatKernelPx);
        var kernel = Kernel(tophat, tophat);
        Cv2.ExtractChannel(region, _blue, 0);
        Cv2.ExtractChannel(region, _green, 1);
        Cv2.ExtractChannel(region, _red, 2);
        if (_settings.WhiteAnyTint || _settings.ContrastMask)
        {
            Cv2.Max(_blue, _green, _value);
            Cv2.Max(_value, _red, _value);
        }

        _valueHatArea = default;
        var bright = _settings.ContrastMask ? BrightArea(tophat, scaleHeight) : null;
        BuildColourTerms(kernel, tophat, bright);
        if (bright is { } area)
            ApplyContrastRule(area, kernel);
    }

    private void BuildColourTerms(Mat kernel, int tophat, Rect? bright)
    {
        if (_settings.WhiteAnyTint)
        {
            Cv2.Threshold(_value, _cyanWhite, _settings.WhiteValueMin, 255, ThresholdTypes.Binary);
            ApplyValueTopHat(kernel, tophat, bright);
        }
        else
        {
            Cv2.Threshold(_blue, _cyanWhite, _settings.CyanBlueMin, 255, ThresholdTypes.Binary);
            Cv2.Threshold(_green, _scratch, _settings.CyanGreenMin, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
            Cv2.Subtract(_red, _blue, _scratch);
            Cv2.Threshold(_scratch, _scratch, 0, 255, ThresholdTypes.BinaryInv);
            Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
            ApplyTopHat(_blue, _cyanWhite, kernel, tophat, _settings.TopHatMin);
        }

        Cv2.Threshold(_red, _redMask, _settings.RedMin, 255, ThresholdTypes.Binary);
        Cv2.Subtract(_red, _green, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.RedOverGreenMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
        Cv2.Subtract(_red, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.RedOverBlueMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
        ApplyTopHat(_red, _redMask, kernel, tophat, _settings.TopHatMin);

        Cv2.Threshold(_green, _greenMask, _settings.GreenMin, 255, ThresholdTypes.Binary);
        Cv2.Subtract(_green, _red, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.GreenOverRedMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
        Cv2.Subtract(_green, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, _settings.GreenOverBlueMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
        ApplyTopHat(_green, _greenMask, kernel, tophat, _settings.GreenTopHatMin);

        Cv2.BitwiseOr(_cyanWhite, _redMask, _mask);
        Cv2.BitwiseOr(_mask, _greenMask, _mask);
    }

    private void ApplyValueTopHat(Mat kernel, int tophat, Rect? bright)
    {
        var bounds = Cv2.BoundingRect(_cyanWhite);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var area = Padded(bounds, tophat, _cyanWhite.Cols, _cyanWhite.Rows);
        var hatArea = area;
        if (bright is { } other)
        {
            var union = Union(area, other);
            if ((long)union.Width * union.Height <= (long)area.Width * area.Height + (long)other.Width * other.Height)
                hatArea = union;
        }

        using (var source = new Mat(_value, hatArea))
            Cv2.MorphologyEx(source, _valueHat, MorphTypes.TopHat, kernel);
        _valueHatArea = hatArea;
        using var hat = new Mat(_valueHat, Within(area, hatArea));
        using var target = new Mat(_cyanWhite, area);
        Cv2.Threshold(hat, _hatBits, _settings.TopHatMin, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(target, _hatBits, target);
    }

    private Rect? BrightArea(int tophat, int scaleHeight)
    {
        var shrink = Scaled(scaleHeight, _settings.BackgroundShrinkPx);
        var smallWidth = Math.Max(1, (_value.Cols + shrink - 1) / shrink);
        var smallHeight = Math.Max(1, (_value.Rows + shrink - 1) / shrink);
        Cv2.Resize(_value, _bgSmall, new Size(smallWidth, smallHeight), 0, 0, InterpolationFlags.Area);
        Cv2.Blur(_bgSmall, _bgBlur, new Size(_settings.BackgroundBlur, _settings.BackgroundBlur));
        Cv2.MinMaxLoc(_bgBlur, out _, out double brightest, out _, out _);
        if (brightest <= _settings.BgDarkMax)
            return null;

        Cv2.Resize(_bgBlur, _background, new Size(_value.Cols, _value.Rows), 0, 0, InterpolationFlags.Linear);
        Cv2.Threshold(_background, _bright, _settings.BgDarkMax, 255, ThresholdTypes.Binary);
        var bounds = Cv2.BoundingRect(_bright);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;
        return Padded(bounds, tophat, _value.Cols, _value.Rows);
    }

    private void ApplyContrastRule(Rect area, Mat kernel)
    {
        using var value = new Mat(_value, area);
        using var background = new Mat(_background, area);
        using var bright = new Mat(_bright, area);
        using var mask = new Mat(_mask, area);
        using var shared = Contains(_valueHatArea, area) ? new Mat(_valueHat, Within(area, _valueHatArea)) : null;
        if (shared is null)
            Cv2.MorphologyEx(value, _hat, MorphTypes.TopHat, kernel);
        Cv2.LUT(background, _hatLimits, _limit);
        Cv2.Compare(shared ?? _hat, _limit, _ink, CmpType.GT);
        Cv2.BitwiseAnd(_ink, bright, _ink);
        mask.SetTo(Scalar.All(0), bright);
        Cv2.BitwiseOr(mask, _ink, mask);
        if (_countContrastInk)
            _lastContrastInk = InkInKeptRows(area);
        if (ContrastSink is { } sink)
            sink.AddContrastMask(Cv2.CountNonZero(_mask), Cv2.CountNonZero(_ink));
    }

    private int InkInKeptRows(Rect area)
    {
        var from = Math.Max(area.Y, _inkKeepFrom);
        var to = Math.Min(area.Bottom, _inkKeepFrom + _inkKeepRows);
        if (to <= from)
            return 0;
        using var kept = new Mat(_ink, new Rect(0, from - area.Y, _ink.Cols, to - from));
        return Cv2.CountNonZero(kept);
    }

    private static Rect Padded(Rect bounds, int size, int cols, int rows)
    {
        var pad = 2 * size + 2;
        var left = Math.Max(0, bounds.X - pad);
        var top = Math.Max(0, bounds.Y - pad);
        var right = Math.Min(cols, bounds.X + bounds.Width + pad);
        var bottom = Math.Min(rows, bounds.Y + bounds.Height + pad);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect Union(Rect a, Rect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return new Rect(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
    }

    private static bool Contains(Rect outer, Rect inner) =>
        outer.Width > 0 && outer.Height > 0 && inner.X >= outer.X && inner.Y >= outer.Y
        && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    private static Rect Within(Rect inner, Rect outer) => new(inner.X - outer.X, inner.Y - outer.Y, inner.Width, inner.Height);

    private static Mat HatLimits(NameplateDetectorSettings settings)
    {
        var limits = new byte[256];
        for (var bg = 0; bg < 256; bg++)
        {
            var t = settings.TopHatMin + (bg - settings.BgDarkMax) / (settings.BgFullContrast - settings.BgDarkMax) * (settings.ContrastHatMax - settings.TopHatMin);
            t = Math.Clamp(t, settings.TopHatMin, settings.ContrastHatMax);
            limits[bg] = (byte)Math.Clamp(Math.Floor(t), 0, 255);
        }

        var lut = new Mat(1, 256, MatType.CV_8UC1);
        Marshal.Copy(limits, 0, lut.Data, limits.Length);
        return lut;
    }

    private void ApplyTopHat(Mat channel, Mat plain, Mat kernel, int size, double minimum)
    {
        var bounds = Cv2.BoundingRect(plain);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var area = Padded(bounds, size, plain.Cols, plain.Rows);
        using var source = new Mat(channel, area);
        using var target = new Mat(plain, area);
        Cv2.MorphologyEx(source, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _hatBits, minimum, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(target, _hatBits, target);
    }
}
