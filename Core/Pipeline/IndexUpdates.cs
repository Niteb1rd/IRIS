using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public enum RequestKind
{
    TrackingRegion,
    FirstRegion,
    StoppedRegion,
    ReadCrop,
    CheckCrop,
    ScanArea,
}

public enum EndReason
{
    Boundary,
    MarkerCheck,
}

[Flags]
public enum IndexTrigger
{
    None = 0,
    NewFrame = 1,
    NameConfirmed = 2,
    SettingsChanged = 4,
    All = NewFrame | NameConfirmed | SettingsChanged,
}

public sealed record RequestSpec(
    PipelineGroup Requester, RequestKind Kind, PixelRect Region, long Tag, double MadeFromFrameMs, double MadeAtMs, double ExpiresAtMs, bool OneTime);

public sealed record CaptureRequest(
    long Id, PipelineGroup Requester, RequestKind Kind, int MarkerId, PixelRect Region, long Tag, double MadeFromFrameMs, double MadeAtMs, double ExpiresAtMs, bool OneTime, bool Fulfilled)
{
    public bool IsIncludedIn(double frameMs, double lastFramePublishedAtMs) =>
        frameMs <= ExpiresAtMs && (!OneTime || lastFramePublishedAtMs < MadeAtMs);
}

public static class CaptureRule
{
    public static List<CaptureRequest> Select(IReadOnlyList<CaptureRequest> requests, double frameMs, double lastFramePublishedAtMs)
    {
        var chosen = new List<CaptureRequest>(requests.Count);
        foreach (var request in requests)
            if (request.IsIncludedIn(frameMs, lastFramePublishedAtMs))
                chosen.Add(request);
        return chosen;
    }

    public static List<CaptureRequest> SelectUnseen(IReadOnlyList<CaptureRequest> requests, double frameMs, long newestSeenRequestId)
    {
        var chosen = new List<CaptureRequest>(requests.Count);
        foreach (var request in requests)
            if (frameMs <= request.ExpiresAtMs && (!request.OneTime || request.Id > newestSeenRequestId))
                chosen.Add(request);
        return chosen;
    }

    public static long NewestId(IReadOnlyList<CaptureRequest> requests)
    {
        var newest = 0L;
        foreach (var request in requests)
            newest = Math.Max(newest, request.Id);
        return newest;
    }
}

public sealed record SessionMemoryEntry(string Name, double Width, double Height, double EndedMs, EndReason Reason, double LineHeight = 0.0);

public abstract record IndexUpdate(PipelineGroup Group, double FrameMs, int Epoch)
{
    private double _workStartMs = double.NaN;
    private double _sentMs = double.NaN;
    private double _newestFrameAtStartMs = double.NaN;

    public double WorkStartMs { get => _workStartMs; init => _workStartMs = value; }

    public double SentMs { get => _sentMs; init => _sentMs = value; }

    public double NewestFrameAtStartMs { get => _newestFrameAtStartMs; init => _newestFrameAtStartMs = value; }

    internal void SetTiming(double workStartMs, double sentMs, double newestFrameAtStartMs)
    {
        _workStartMs = workStartMs;
        _sentMs = sentMs;
        _newestFrameAtStartMs = newestFrameAtStartMs;
    }
}

public static class UpdateTiming
{
    public static void Stamp(IReadOnlyList<IndexUpdate> updates, double workStartMs, double sentMs, double newestFrameAtStartMs)
    {
        for (var i = 0; i < updates.Count; i++)
            updates[i].SetTiming(workStartMs, sentMs, newestFrameAtStartMs);
    }

    public static void Send(IUpdateSink sink, List<IndexUpdate> updates, IMonotonicClock clock, double workStartMs, double newestFrameAtStartMs)
    {
        Stamp(updates, workStartMs, clock.NowMs, newestFrameAtStartMs);
        sink.Submit(updates);
    }
}

public sealed record NewMarkerUpdate(PipelineGroup Group, double FrameMs, int Epoch, PixelRect Line, RequestSpec? FirstRequest) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record TrackingHitUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, double CentreX, double Top, double Width, double DistanceWidth = double.NaN)
    : IndexUpdate(Group, FrameMs, Epoch);

public sealed record PositionUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, PixelRect Line, double DistanceWidth = double.NaN) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record StatusUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, PlateStatus Status) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record ConfirmUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, string Name, PixelRect NameRel, PixelRect PlateRel, NameLineInfo? NameLine = null) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record UnconfirmUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record IdentityUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, MarkerIdentity? Identity) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record PlateColourUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, PlateColor? Colour) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record EndUpdate(PipelineGroup Group, double FrameMs, int Epoch, int Id, EndReason Reason) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record RequestUpdate(PipelineGroup Group, double FrameMs, int Epoch, int MarkerId, RequestSpec Spec) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record NewFrameUpdate(PipelineGroup Group, double FrameMs, int Epoch, double ArrivalMs, double MedianIntervalMs, IReadOnlyList<long> RequestIds) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record FrameTrackedUpdate(PipelineGroup Group, double FrameMs, int Epoch, double ArrivalMs, double TrackingP95Ms, double PublishedMs = double.NaN) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record SettingsUpdate(PipelineGroup Group, double FrameMs, int Epoch, TrackingBoundary? Boundary, int WindowWidth, int WindowHeight) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record LateNoteUpdate(PipelineGroup Group, double FrameMs, int Epoch, bool Active) : IndexUpdate(Group, FrameMs, Epoch);

public sealed record ResetUpdate(PipelineGroup Group, double FrameMs, int Epoch) : IndexUpdate(Group, FrameMs, Epoch);

public interface IUpdateSink
{
    void Submit(IReadOnlyList<IndexUpdate> updates);
}
