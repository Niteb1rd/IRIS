
namespace Iris.Core.Pipeline;

public sealed class IndexSnapshot
{
    public static readonly IndexSnapshot Empty = new()
    {
        Markers = Array.Empty<MarkerRecord>(),
        Requests = Array.Empty<CaptureRequest>(),
        SessionMemory = Array.Empty<SessionMemoryEntry>(),
    };

    public long Version { get; init; }

    public int Epoch { get; init; }

    public IReadOnlyList<MarkerRecord> Markers { get; init; } = Array.Empty<MarkerRecord>();

    public IReadOnlyList<CaptureRequest> Requests { get; init; } = Array.Empty<CaptureRequest>();

    public IReadOnlyList<SessionMemoryEntry> SessionMemory { get; init; } = Array.Empty<SessionMemoryEntry>();

    public double NewFrameMs { get; init; } = double.NegativeInfinity;

    public double NameConfirmedMs { get; init; } = double.NegativeInfinity;

    public double SettingsChangedMs { get; init; } = double.NegativeInfinity;

    public double TrackedFrameMs { get; init; } = double.NegativeInfinity;

    public double PrevTrackedFrameMs { get; init; } = double.NaN;

    public double MedianFrameIntervalMs { get; init; } = double.NaN;

    public double TrackingP95Ms { get; init; }

    public double FramePublishedP95Ms { get; init; }

    public double PublishedMs { get; internal set; } = double.NaN;

    public bool CarriesTrackedFrame { get; init; }

    public TrackingBoundary? Boundary { get; init; }

    public int WindowWidth { get; init; }

    public int WindowHeight { get; init; }

    public bool LateFrameNote { get; init; }

    public double? LoopIntervalMs => double.IsNaN(PrevTrackedFrameMs) || double.IsNegativeInfinity(TrackedFrameMs) ? null : TrackedFrameMs - PrevTrackedFrameMs;

    public double NominalFrameIntervalMs
    {
        get
        {
            if (MedianFrameIntervalMs > 0.0)
                return MedianFrameIntervalMs;
            var loop = LoopIntervalMs;
            return loop.HasValue && loop.Value > 0.0 ? loop.Value : LiveSettings.DefaultFrameIntervalMs;
        }
    }

    public MarkerRecord? Find(int id)
    {
        foreach (var marker in Markers)
            if (marker.Id == id)
                return marker;
        return null;
    }
}
