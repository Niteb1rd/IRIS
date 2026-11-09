using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Iris.Tests.Support;
using OpenCvSharp;
using Xunit;

namespace Iris.Tests.Pipeline;

public class ReadsWorkerTests : IDisposable
{
    private readonly BatchSink _sink = new();
    private readonly ManualMonotonicClock _clock = new() { NowMs = 1000.0 };
    private readonly FrameHub _hub = new(3);
    private readonly Mat _image = new(64, 64, MatType.CV_8UC4, Scalar.All(0));
    private readonly SocialConfig _config = new();
    private readonly PipelineEnvironment _env;
    private readonly ReadsWorker _worker;
    private readonly PlateNameJudge _judge;

    public ReadsWorkerTests()
    {
        var boundary = TrackingBoundary.FromPercents(15, 15, 8, 8);
        Assert.True(ScanGeometry.TryCreate(2560, 1440, NameplateDetectorSettings.Default, boundary, out var geometry));
        _env = new PipelineEnvironment(geometry!, boundary);
        _judge = new PlateNameJudge(ExclusionList.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new FakeClock(), Iris.Core.NullLog.Instance), new SessionExclusions(), _ => null, null, LiveSettings.Default);
        _worker = new ReadsWorker(PipelineSettings.Default, new FakeImageOps(), _judge, new SessionExclusions(), _sink, _clock);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _image.Dispose();
    }

    private static MarkerRecord Marker(int id, PlateStatus status = PlateStatus.Unconfirmed) =>
        MarkerRecord.Born(id, new PixelRect(1000 + id * 200, 700, 100, 14), 900.0) with { Status = status };

    [Fact]
    public void ReadCropsUseThePredictionAdaptedToTheFrameRate()
    {
        const double interval = 16.67;
        var moving = MarkerRecord.Born(7, new PixelRect(1000, 700, 100, 14), 0.0).AfterHit(20.0, 1060.0, 700.0, 100.0).AfterHit(40.0, 1070.0, 700.0, 100.0);
        var snapshot = new IndexSnapshot { Epoch = 1, Markers = new[] { moving }, NewFrameMs = 400.0, MedianFrameIntervalMs = interval };
        var settings = PipelineSettings.Default;
        var expected = RegionPlanner.Crop(moving, 400.0 + interval, settings.ReadMarginPx, 2560, 1440, settings.Adapt(interval));

        _worker.Prepare(snapshot, _env, 400.0, _config);

        Assert.Equal(RegionPlanner.ToPixelRect(expected!.Value), _sink.All.OfType<RequestUpdate>().Single(u => u.Spec.Kind == RequestKind.ReadCrop).Spec.Region);
    }

    private static IndexSnapshot Snapshot(params MarkerRecord[] markers) => new() { Epoch = 1, Markers = markers };

    [Fact]
    public void ANewPlateEarnsAnEarlyRead()
    {
        Assert.True(_worker.WantsEarlyRead(Snapshot(Marker(1))));
    }

    [Fact]
    public void APlateEarnsOnlyOneEarlyRead()
    {
        var snapshot = Snapshot(Marker(1));
        _worker.WantsEarlyRead(snapshot);

        Assert.False(_worker.WantsEarlyRead(snapshot));
    }

    [Fact]
    public void AnotherNewPlateEarnsItsOwnEarlyRead()
    {
        _worker.WantsEarlyRead(Snapshot(Marker(1)));

        Assert.True(_worker.WantsEarlyRead(Snapshot(Marker(1), Marker(2))));
    }

    [Theory]
    [InlineData(PlateStatus.Confirmed)]
    [InlineData(PlateStatus.Stopped)]
    [InlineData(PlateStatus.Unclear)]
    public void OnlyFreshUnreadPlatesEarnOne(PlateStatus status)
    {
        Assert.False(_worker.WantsEarlyRead(Snapshot(Marker(1, status))));
    }

    [Fact]
    public void NoEarlyReadIsTakenWhileARequestIsOutstanding()
    {
        var older = Marker(1);
        _worker.Prepare(Snapshot(older), _env, _clock.NowMs, _config);

        Assert.False(_worker.WantsEarlyRead(Snapshot(older, Marker(2))));
    }

    [Fact]
    public void AnEarlyReadBlockedByAnOutstandingRequestIsNotLost()
    {
        var older = Marker(1);
        _worker.Prepare(Snapshot(older), _env, _clock.NowMs, _config);
        _worker.WantsEarlyRead(Snapshot(older, Marker(2)));
        Process(1, older);

        Assert.True(_worker.WantsEarlyRead(Snapshot(older, Marker(2))));
    }

    [Fact]
    public void APlateAlreadyReadByANormalTickEarnsNone()
    {
        var plate = Marker(1);
        _worker.Prepare(Snapshot(plate), _env, _clock.NowMs, _config);

        Process(1, plate);

        Assert.False(_worker.WantsEarlyRead(Snapshot(plate)));
    }

    [Fact]
    public void AFailedReadDoesNotLoseTheUpdatesFromEarlierPlates()
    {
        var ops = new FakeImageOps();
        var worker = new ReadsWorker(PipelineSettings.Default, ops, _judge, new SessionExclusions(), _sink, _clock);
        var first = Marker(1);
        var second = Marker(2);
        var snapshot = Snapshot(first, second);
        worker.Prepare(snapshot, _env, 1000.0, _config);
        ops.OnRead = item => item.Id == second.Id ? throw new InvalidOperationException("broken read") : new ReadResult(ObservationClass.Unclear, "nothing");
        var frame = _hub.Rent()!;
        frame.Begin(1000.0, 1001.0, 2560, 1440);
        foreach (var marker in new[] { first, second })
            frame.Add(new CaptureRequest(1, PipelineGroup.Reads, RequestKind.ReadCrop, marker.Id, new PixelRect(0, 0, 64, 64), 1, 0.0, 0.0, double.MaxValue, true, false), new Rect(0, 0, 64, 64), _image);
        _sink.Batches.Clear();

        Assert.Throws<InvalidOperationException>(() => worker.Process(snapshot, frame, _env, _config));

        Assert.Contains(_sink.All.OfType<StatusUpdate>(), update => update.Id == first.Id);
    }

    private void Process(long tag, MarkerRecord marker)
    {
        var frame = _hub.Rent()!;
        frame.Begin(1000.0, 1001.0, 2560, 1440);
        frame.Add(new CaptureRequest(1, PipelineGroup.Reads, RequestKind.ReadCrop, marker.Id, new PixelRect(0, 0, 64, 64), tag, 0.0, 0.0, double.MaxValue, true, false), new Rect(0, 0, 64, 64), _image);
        _worker.Process(Snapshot(marker), frame, _env, _config);
    }
}
