using Iris.Core.Detection;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Iris.Tests.Support;
using OpenCvSharp;
using Xunit;

namespace Iris.Tests.Pipeline;

public class TrackingWorkerTests : IDisposable
{
    private static readonly PixelRect OwnLine = new(1000, 700, 100, 14);
    private static readonly PixelRect StoppedLine = new(1010, 712, 100, 14);

    private sealed class FakeTracker : ILineTracker
    {
        public IReadOnlyList<FoundLine> Lines { get; set; } = Array.Empty<FoundLine>();

        public IReadOnlyList<FoundLine> Find(MarkerRecord marker, PatchPlan plan, Mat patch) => Lines;

        public LinePosition Refine(PatchPlan plan, Mat patch, PixelRect box) => new(box.X + box.Width / 2.0, box.Y, box.Width);

        public PixelRect? LocateLine(Mat patch, PatchPlan plan, double lineHeight, IReadOnlyList<PixelRect> claimed, double lineWidth = double.NaN) => null;
    }

    private readonly FakeTracker _tracker = new();
    private readonly BatchSink _sink = new();
    private readonly ManualMonotonicClock _clock = new() { NowMs = 1000.0 };
    private readonly FrameHub _hub = new(3);
    private readonly Mat _image = new(200, 300, MatType.CV_8UC4, Scalar.All(0));
    private readonly PipelineEnvironment _env;

    public TrackingWorkerTests()
    {
        var boundary = TrackingBoundary.FromPercents(15, 15, 8, 8);
        Assert.True(ScanGeometry.TryCreate(2560, 1440, NameplateDetectorSettings.Default, boundary, out var geometry));
        _env = new PipelineEnvironment(geometry!, boundary);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _image.Dispose();
    }

    private void Track(params MarkerRecord[] markers)
    {
        var worker = new TrackingWorker(PipelineSettings.Default, _tracker, _sink, _clock);
        var frame = _hub.Rent()!;
        frame.Begin(1000.0, 1001.0, 2560, 1440);
        var region = new PixelRect(950, 650, 300, 200);
        frame.Add(new CaptureRequest(1, PipelineGroup.Tracking, RequestKind.TrackingRegion, 7, region, 1, 0.0, 0.0, double.MaxValue, false, false), new Rect(950, 650, 300, 200), _image);
        worker.Process(new IndexSnapshot { Epoch = 1, Markers = markers }, frame, _env);
    }

    [Fact]
    public void AMarkerStillTakesItsOwnLine()
    {
        _tracker.Lines = new[] { new FoundLine(OwnLine) };

        Track(MarkerRecord.Born(7, OwnLine, 980.0));

        Assert.Single(_sink.All.OfType<TrackingHitUpdate>(), hit => hit.Id == 7);
    }

    [Fact]
    public void AMarkerKeepsItsLineWhenAStoppedPlateSitsOnIt()
    {
        _tracker.Lines = new[] { new FoundLine(OwnLine) };
        var stopped = MarkerRecord.Born(8, OwnLine, 900.0) with { Status = PlateStatus.Stopped };

        Track(MarkerRecord.Born(7, OwnLine, 980.0), stopped);

        Assert.Single(_sink.All.OfType<TrackingHitUpdate>(), hit => hit.Id == 7);
    }

    [Fact]
    public void AMarkerCannotTakeTheLineOfAStoppedPlate()
    {
        _tracker.Lines = new[] { new FoundLine(StoppedLine) };
        var stopped = MarkerRecord.Born(8, StoppedLine, 900.0) with { Status = PlateStatus.Stopped };

        Track(MarkerRecord.Born(7, OwnLine, 980.0), stopped);

        Assert.DoesNotContain(_sink.All.OfType<TrackingHitUpdate>(), hit => hit.Id == 7);
    }
}
