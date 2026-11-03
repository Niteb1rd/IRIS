using OpenCvSharp;

namespace Iris.Core.Pipeline;

public enum GrabStatus
{
    Ok,

    Unavailable,
}

public readonly record struct GrabRegionsResult(GrabStatus Status, IReadOnlyList<Mat?> Images, string? Message = null, double FrameMs = double.NaN)
{
    public static GrabRegionsResult Unavailable(string message) => new(GrabStatus.Unavailable, Array.Empty<Mat?>(), message);
}
