using Iris.Core.Detection;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Iris.Tests.Support;
using OpenCvSharp;
using Xunit;

namespace Iris.Tests.Pipeline;

public class KnownPlateRecoveryTests : IDisposable
{
    private static readonly PixelRect Line = new(1000, 700, 100, 14);
    private static readonly PixelRect NameRel = new(-40, -20, 80, 14);

    private sealed class FakeMatcher : ITemplateMatcher
    {
        public TemplateHit? Hit { get; set; }

        public TemplateHit? Match(Mat grey, Mat template, Rect window) => Hit;
    }

    private readonly FakeImageOps _ops = new() { NearOk = false };
    private readonly BatchSink _sink = new();
    private readonly ManualMonotonicClock _clock = new() { NowMs = 1000.0 };
    private readonly FrameHub _hub = new(32);
    private readonly Mat _image = new(200, 300, MatType.CV_8UC3);
    private readonly PipelineStats _stats = new(0.0);
    private readonly PipelineEnvironment _env;
    private readonly ScanWorker _worker;
    private MarkerRecord _marker = MarkerRecord.Born(7, Line, 800.0) with { Status = PlateStatus.Confirmed, MarkedConfirmed = true };
    private long _tag;
    private double _frameMs = 1000.0;

    public KnownPlateRecoveryTests()
    {
        Cv2.Randu(_image, Scalar.All(0), Scalar.All(255));
        var boundary = TrackingBoundary.FromPercents(15, 15, 8, 8);
        Assert.True(ScanGeometry.TryCreate(2560, 1440, NameplateDetectorSettings.Default, boundary, out var geometry));
        _env = new PipelineEnvironment(geometry!, boundary);
        _worker = new ScanWorker(PipelineSettings.Default, _ops, null, _sink, _clock, _stats);
    }

    public void Dispose()
    {
        _worker.Dispose();
        _hub.Dispose();
        _image.Dispose();
    }

    private void RunChecks(int count, params MarkerRecord[] others)
    {
        for (var i = 0; i < count; i++)
        {
            var snapshot = new IndexSnapshot { Epoch = 1, Markers = new[] { _marker }.Concat(others).ToArray() };
            _worker.Prepare(snapshot, _env, _clock.NowMs);
            _tag++;
            var frame = _hub.Rent()!;
            frame.Begin(_frameMs, _frameMs + 1.0, 2560, 1440);
            var request = new CaptureRequest(1, PipelineGroup.Scan, RequestKind.CheckCrop, _marker.Id, new PixelRect(950, 650, 300, 200), _tag, 0.0, 0.0, double.MaxValue, true, false);
            frame.Add(request, new Rect(950, 650, 300, 200), _image);
            _worker.Process(snapshot, frame, _env);
        }
    }

    private int Ended => _sink.All.OfType<EndUpdate>().Count(u => u.Id == _marker.Id);

    [Fact]
    public void APlateTheSearchMissesStaysAliveWhileItsDistanceReadsInPlace()
    {
        _ops.InPlaceOk = true;

        RunChecks(12);

        Assert.Equal(0, Ended);
        Assert.Equal(0, _worker.FailedChecks(_marker.Id));
        Assert.Equal(0, _worker.LostChecks(_marker.Id));
    }

    [Fact]
    public void APlateThatReadsNowhereEndsAfterTheUsualFailures()
    {
        _ops.InPlaceOk = false;

        RunChecks(PipelineSettings.Default.ConfirmedCheckFails - 1);
        Assert.Equal(0, Ended);

        RunChecks(1);
        Assert.Equal(1, Ended);
    }

    [Fact]
    public void AnOccasionalMissedInPlaceReadDoesNotEndThePlate()
    {
        var limit = PipelineSettings.Default.ConfirmedCheckFails;
        for (var round = 0; round < 5; round++)
        {
            _ops.InPlaceOk = false;
            RunChecks(limit - 1);
            _ops.InPlaceOk = true;
            RunChecks(1);
        }

        Assert.Equal(0, Ended);
    }

    [Fact]
    public void TheInPlaceReadIsOnlyTriedWhenTheSearchFailed()
    {
        _ops.NearOk = true;

        RunChecks(3);

        Assert.Equal(0, _ops.InPlaceReads);
    }

    [Fact]
    public void TheInPlaceReadIsNotUsedForPlatesNotYetIdentified()
    {
        _marker = _marker with { Status = PlateStatus.Unconfirmed, MarkedConfirmed = false };
        _ops.InPlaceOk = true;

        RunChecks(1);

        Assert.Equal(0, _ops.InPlaceReads);
    }

    [Fact]
    public void TheInPlaceReadLooksWhereThePlateShouldBe()
    {
        _ops.InPlaceOk = true;

        RunChecks(1);

        Assert.Equal(_marker.Line, _ops.LastInPlaceBox);
    }

    [Fact]
    public void AnInPlaceReadNeverMovesThePlate()
    {
        _ops.InPlaceOk = true;

        RunChecks(6);

        Assert.Empty(_sink.All.OfType<PositionUpdate>());
        Assert.Empty(_sink.All.OfType<TemplateHitUpdate>());
    }

    [Fact]
    public void AnotherPlatesLineAtTheExpectedPlaceDoesNotCount()
    {
        _ops.InPlaceOk = true;
        var other = MarkerRecord.Born(8, Line, 700.0);

        RunChecks(PipelineSettings.Default.ConfirmedCheckFails, other);

        Assert.Equal(0, _ops.InPlaceReads);
        Assert.Equal(1, Ended);
    }

    [Fact]
    public void ANameTemplateHitIsEnoughWithoutTheInPlaceRead()
    {
        var matcher = new FakeMatcher();
        _worker.Matcher = matcher;
        _marker = _marker with { NameRel = NameRel };
        _ops.NearOk = true;
        RunChecks(1);
        Assert.True(_worker.HasTemplate(_marker.Id));

        _ops.NearOk = false;
        matcher.Hit = new TemplateHit(0.9, 60, 30);
        RunChecks(3);

        Assert.Equal(0, _ops.InPlaceReads);
        Assert.Equal(0, Ended);
    }

    [Fact]
    public void AHoldEndsAfterItsTimeLimit()
    {
        _ops.InPlaceOk = true;
        RunChecks(1);
        _frameMs += PipelineSettings.Default.ConfirmedHoldMaxMs + 1.0;

        RunChecks(PipelineSettings.Default.ConfirmedCheckFails);

        Assert.Equal(1, Ended);
    }

    [Fact]
    public void TrackingFindingThePlateRestartsTheHoldLimit()
    {
        _ops.InPlaceOk = true;
        RunChecks(1);
        _frameMs += PipelineSettings.Default.ConfirmedHoldMaxMs - 1000.0;
        _marker = _marker with { LastFoundMs = _frameMs };
        RunChecks(1);
        _frameMs += PipelineSettings.Default.ConfirmedHoldMaxMs - 1000.0;

        RunChecks(PipelineSettings.Default.ConfirmedCheckFails);

        Assert.Equal(0, Ended);
    }

    [Fact]
    public void AReadThatLandedOnANeighbourDoesNotStopTheHold()
    {
        var neighbour = new PixelRect(1200, 700, 100, 14);
        _ops.NearOk = true;
        _ops.NearLine = neighbour;
        _ops.InPlaceOk = true;

        RunChecks(PipelineSettings.Default.ConfirmedNoTrackChecks + 2, MarkerRecord.Born(8, neighbour, 700.0));

        Assert.True(_ops.InPlaceReads > 0);
        Assert.Equal(0, Ended);
    }

    [Fact]
    public void InPlaceHoldsAreCountedForTheSessionStats()
    {
        _ops.InPlaceOk = true;
        RunChecks(2);
        _ops.InPlaceOk = false;
        RunChecks(1);

        Assert.Equal(2, _stats.InPlaceHolds);
    }
}
