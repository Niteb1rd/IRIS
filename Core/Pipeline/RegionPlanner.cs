using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed record PipelineEnvironment(ScanGeometry Geometry, TrackingBoundary? Boundary)
{
    public int WindowWidth => Geometry.WindowWidth;

    public int WindowHeight => Geometry.WindowHeight;
}

public sealed class ManualMonotonicClock : IMonotonicClock
{
    public double NowMs { get; set; }
}

public sealed class RateClock
{
    private readonly double _periodMs;
    private readonly double _slackMs;
    private double _nextMs = double.NegativeInfinity;

    public RateClock(double hz)
    {
        _periodMs = 1000.0 / hz;
        _slackMs = PipelineSettings.SlackMs(_periodMs);
    }

    public double PeriodMs => _periodMs;

    public double NextDueMs => _nextMs;

    public void Reset() => _nextMs = double.NegativeInfinity;

    public bool TryTick(double nowMs, out int missed)
    {
        missed = 0;
        if (double.IsNegativeInfinity(_nextMs))
        {
            _nextMs = nowMs + _periodMs;
            return true;
        }

        var late = nowMs + _slackMs - _nextMs;
        if (late < 0.0)
            return false;
        missed = (int)Math.Floor(late / _periodMs);
        _nextMs += (missed + 1) * _periodMs;
        return true;
    }
}

public readonly record struct PlannedPatch(Rect Region, double ExpiryMs);

public static class RegionPlanner
{
    public static PixelRect ToPixelRect(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    public static Rect ToWindowRect(PixelRect region, int windowWidth, int windowHeight)
    {
        var x0 = Math.Clamp((int)Math.Round(region.X), 0, windowWidth);
        var y0 = Math.Clamp((int)Math.Round(region.Y), 0, windowHeight);
        var x1 = Math.Clamp((int)Math.Round(region.X + region.Width), 0, windowWidth);
        var y1 = Math.Clamp((int)Math.Round(region.Y + region.Height), 0, windowHeight);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    public static double ExpiryMs(double marginPx, double speedPxPerMs, PipelineSettings settings) =>
        speedPxPerMs > 1e-9 ? Math.Clamp(marginPx / speedPxPerMs, settings.RequestMinMs, settings.RequestMaxMs) : settings.RequestMaxMs;

    public static double ExpiryFloorMs(double frameIntervalMs, PipelineSettings settings) =>
        double.IsFinite(frameIntervalMs) && frameIntervalMs > 0.0 ? Math.Min(settings.ExpiryIntervalFactor * frameIntervalMs, settings.RequestCapMs) : 0.0;

    public static double OneTimeExpiryMs(double baseMs, double frameIntervalMs, PipelineSettings settings) =>
        Math.Max(baseMs, ExpiryFloorMs(frameIntervalMs, settings));

    public static Rect? TrackingPatch(
        MarkerRecord marker, double frameMs, double frameIntervalMs, bool withName, int windowWidth, int windowHeight, PipelineSettings settings, out double expiryMs,
        bool lostGrowth = true, bool nameWhenLost = false)
    {
        var speed = Speed(marker);
        var targetMs = frameMs + frameIntervalMs;
        var lostNow = marker.IsLost(targetMs, settings);
        var lost = lostGrowth && lostNow;
        var name = (lostNow && nameWhenLost) || (withName && !lost);
        var region = CappedPatch(marker, targetMs, speed * frameIntervalMs, name, lost, windowWidth, windowHeight, settings, out var margin);
        var floor = ExpiryFloorMs(frameIntervalMs, settings);
        var baseExpiry = ExpiryMs(margin, speed, settings);
        expiryMs = Math.Min(Math.Max(settings.RequestMaxMs, floor), Math.Max(baseExpiry, floor));
        if (region is null || !(expiryMs > baseExpiry) || speed <= 1e-9 || margin >= speed * expiryMs)
            return region;
        var extra = speed * frameIntervalMs + (speed * expiryMs - margin);
        return CappedPatch(marker, targetMs, extra, name, lost, windowWidth, windowHeight, settings, out _) ?? region;
    }

    public static Dictionary<int, PlannedPatch> PlanTick(
        IEnumerable<MarkerRecord> markers, Func<MarkerRecord, bool> withName, double frameMs, double frameIntervalMs, int windowWidth, int windowHeight, PipelineSettings settings,
        Func<MarkerRecord, bool>? nameWhenLost = null)
    {
        var plans = new Dictionary<int, PlannedPatch>();
        var budget = (long)settings.MaxTickPixels;
        foreach (var marker in markers.OrderByDescending(m => m.LastFoundMs).ThenBy(m => m.Id))
        {
            var name = withName(marker);
            var keepName = nameWhenLost?.Invoke(marker) ?? false;
            var region = TrackingPatch(marker, frameMs, frameIntervalMs, name, windowWidth, windowHeight, settings, out var expiryMs, true, keepName);
            if (region is { } wide && Area(wide) > budget && marker.IsLost(frameMs + frameIntervalMs, settings))
                region = TrackingPatch(marker, frameMs, frameIntervalMs, name, windowWidth, windowHeight, settings, out expiryMs, false, keepName);
            if (region is not { } rect)
                continue;
            budget -= Area(rect);
            plans[marker.Id] = new PlannedPatch(rect, expiryMs);
        }

        return plans;
    }

    public static long Area(Rect region) => (long)region.Width * region.Height;

    private static Rect? CappedPatch(
        MarkerRecord marker, double targetMs, double extraMarginPx, bool withName, bool lostGrowth, int windowWidth, int windowHeight, PipelineSettings settings, out double marginPx)
    {
        var wide = Patch(marker, targetMs, extraMarginPx, null, windowWidth, windowHeight, settings, out marginPx, withName, lostGrowth);
        if (wide is not { } rect || Area(rect) <= settings.MaxPatchPixels)
            return wide;
        var low = Math.Min(marginPx, marker.Height * settings.SearchBaseLines + extraMarginPx);
        var floorPatch = Patch(marker, targetMs, 0.0, low, windowWidth, windowHeight, settings, out _, withName, lostGrowth);
        if (floorPatch is not { } floorRect || Area(floorRect) >= settings.MaxPatchPixels)
        {
            marginPx = low;
            return floorPatch ?? wide;
        }

        var high = marginPx;
        var best = floorRect;
        var bestMargin = low;
        for (var step = 0; step < 12 && high - low > 1.0; step++)
        {
            var mid = (low + high) / 2.0;
            var trial = Patch(marker, targetMs, 0.0, mid, windowWidth, windowHeight, settings, out _, withName, lostGrowth);
            if (trial is { } fit && Area(fit) <= settings.MaxPatchPixels)
            {
                low = mid;
                best = fit;
                bestMargin = mid;
            }
            else
            {
                high = mid;
            }
        }

        marginPx = bestMargin;
        return best;
    }

    public static double Speed(MarkerRecord marker)
    {
        var (vx, vy) = marker.TrackingVelocity();
        return Math.Sqrt(vx * vx + vy * vy);
    }

    public static Rect? Patch(
        MarkerRecord marker, double targetMs, double extraMarginPx, double? fixedMarginPx, int windowWidth, int windowHeight, PipelineSettings settings, out double marginPx,
        bool withName = false, bool lostGrowth = true)
    {
        var scale = windowHeight / 1440.0;
        var (px, py, predicted, _) = marker.Predict(targetMs, settings, lostGrowth);
        marginPx = fixedMarginPx ?? predicted + extraMarginPx;
        var mg = (int)Math.Ceiling(marginPx);
        var pad = Math.Max(1, (int)Math.Round(settings.PatchPadPx * scale));
        var grow = settings.PatchSpeedGrowLines * marker.Height;
        var left = px - mg - pad - grow;
        var top = py - mg - pad;
        var right = px + Math.Max(marker.Width, settings.PatchMinWidthPx * scale) + mg + pad + grow;
        var bottom = py + marker.Height + mg + pad;
        if (withName && marker.NameLine is { } name)
        {
            var side = Math.Max(0.0, (name.Width - Math.Max(marker.Width, settings.PatchMinWidthPx * scale)) / 2.0) + pad;
            top -= name.Gap + name.Height + marginPx;
            left -= side;
            right += side;
        }

        var x0 = Math.Max(0, (int)left);
        var y0 = Math.Max(0, (int)top);
        var x1 = Math.Min(windowWidth, (int)right);
        var y1 = Math.Min(windowHeight, (int)bottom);
        if (x1 <= x0 || y1 <= y0)
            return null;
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    public static Rect? Crop(MarkerRecord marker, double targetMs, double minMarginPx, int windowWidth, int windowHeight, PipelineSettings settings)
    {
        var scale = windowHeight / 1440.0;
        var (px, py, predictedMargin, _) = marker.Predict(targetMs, settings);
        var margin = Math.Max(predictedMargin, minMarginPx);
        var lh = marker.Height;
        var cx = px + Math.Max(marker.Width, settings.PatchMinWidthPx * scale) / 2.0;
        var halfWidth = (int)(settings.CropHalfWidthPx * scale + settings.CropHalfWidthLines * lh + settings.CropSlackPx * scale + margin);
        if (marker.NameRel is { } name)
            halfWidth = Math.Max(halfWidth, (int)(name.Width / 2.0 + settings.CropSlackPx * scale + margin));
        var x0 = Math.Max(0, (int)(cx - halfWidth));
        var x1 = Math.Min(windowWidth, (int)(cx + halfWidth));
        var y0 = Math.Max(0, (int)(py - settings.CropAboveLines * lh - margin));
        var y1 = Math.Min(windowHeight, (int)(py + marker.Height + settings.CropBelowLines * lh + margin));
        if (x1 <= x0 || y1 <= y0)
            return null;
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    public static (double X, double Y) ScanCentre(ScanGeometry geometry, TrackingBoundary? boundary, PipelineSettings settings)
    {
        if (boundary is { } area)
            return ((area.Left + area.Right) / 2.0 * geometry.WindowWidth, (area.Top + area.Bottom) / 2.0 * geometry.WindowHeight);
        return (settings.ScanCenterX * geometry.WindowWidth, settings.ScanCenterY * geometry.WindowHeight);
    }
}
