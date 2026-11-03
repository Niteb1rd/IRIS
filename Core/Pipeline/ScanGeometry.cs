using System.Diagnostics.CodeAnalysis;
using Iris.Core.Detection;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class ScanGeometry
{
    private ScanGeometry(int windowWidth, int windowHeight, Rect capture, NameplateDetectorSettings detectorSettings, TrackingBoundary? boundary)
    {
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        Capture = capture;
        DetectorSettings = detectorSettings;
        Boundary = boundary;
    }

    public TrackingBoundary? Boundary { get; }

    public int WindowWidth { get; }

    public int WindowHeight { get; }

    public Rect Capture { get; }

    public NameplateDetectorSettings DetectorSettings { get; }

    public bool Matches(int windowWidth, int windowHeight) => windowWidth == WindowWidth && windowHeight == WindowHeight;

    public bool Matches(int windowWidth, int windowHeight, TrackingBoundary? boundary) =>
        Matches(windowWidth, windowHeight) && Boundary == boundary;

    public static bool TryCreate(int windowWidth, int windowHeight, NameplateDetectorSettings baseSettings, [NotNullWhen(true)] out ScanGeometry? geometry) =>
        TryCreate(windowWidth, windowHeight, baseSettings, null, out geometry);

    public static bool TryCreate(int windowWidth, int windowHeight, NameplateDetectorSettings baseSettings, TrackingBoundary? boundary, [NotNullWhen(true)] out ScanGeometry? geometry)
    {
        geometry = null;
        if (boundary is { } applied)
            baseSettings = WithBoundary(baseSettings, applied);
        if (windowWidth <= 0 || windowHeight <= 0 || baseSettings.Validate() is not null)
            return false;

        var x0 = (int)(baseSettings.RegionLeft * windowWidth);
        var x1 = Math.Min(windowWidth, (int)(baseSettings.RegionRight * windowWidth));
        var y0 = (int)(baseSettings.RegionTop * windowHeight);
        var y1 = Math.Min(windowHeight, (int)(baseSettings.RegionBottom * windowHeight));
        if (x1 <= x0 || y1 <= y0)
            return false;

        var pad = baseSettings.ContextPadding(windowHeight);
        var vpad = baseSettings.VerticalContextPadding(windowHeight);
        var cx0 = Math.Max(0, x0 - pad);
        var cx1 = Math.Min(windowWidth, x1 + pad);
        var cy0 = Math.Max(0, y0 - vpad);
        var cy1 = Math.Min(windowHeight, y1 + vpad);
        var capture = new Rect(cx0, cy0, cx1 - cx0, cy1 - cy0);

        var w = (double)capture.Width;
        var h = (double)capture.Height;
        var settings = baseSettings with
        {
            RegionLeft = (x0 - cx0 + 0.25) / w,
            RegionRight = Math.Min(1.0, (x1 - cx0 + 0.25) / w),
            RegionTop = (y0 - cy0 + 0.25) / h,
            RegionBottom = Math.Min(1.0, (y1 - cy0 + 0.25) / h),
            ScaleHeight = windowHeight,
        };
        if (settings.Validate() is not null)
            return false;
        geometry = new ScanGeometry(windowWidth, windowHeight, capture, settings, boundary);
        return true;
    }

    public static NameplateDetectorSettings WithBoundary(NameplateDetectorSettings settings, TrackingBoundary boundary) =>
        settings with { RegionLeft = boundary.Left, RegionRight = boundary.Right, RegionTop = boundary.Top, RegionBottom = boundary.Bottom };
}
