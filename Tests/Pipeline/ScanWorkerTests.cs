using Iris.Core.Detection;
using Iris.Core.Mining;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Iris.Tests.Support;
using OpenCvSharp;
using Xunit;

namespace Iris.Tests.Pipeline;

public class ScanWorkerTests : IDisposable
{
    private static readonly PixelRect NewLine = new(500, 500, 100, 14);

    private readonly FakeImageOps _ops = new();
    private readonly BatchSink _sink = new();
    private readonly ManualMonotonicClock _clock = new() { NowMs = 1000.0 };
    private readonly FrameHub _hub = new(3);
    private readonly Mat _image = new(64, 64, MatType.CV_8UC4, Scalar.All(0));
    private readonly MarkerRecord _marker = MarkerRecord.Born(7, new PixelRect(1000, 700, 100, 14), 900.0);
    private readonly PipelineEnvironment _env;
    private readonly ScanWorker _worker;

    public ScanWorkerTests()
    {
        var boundary = TrackingBoundary.FromPercents(15, 15, 8, 8);
        Assert.True(ScanGeometry.TryCreate(2560, 1440, NameplateDetectorSettings.Default, boundary, out var geometry));
        _env = new PipelineEnvironment(geometry!, boundary);
        _worker = new ScanWorker(PipelineSettings.Default, _ops, null, _sink, _clock);
    }

    public void Dispose()
    {
        _worker.Dispose();
        _hub.Dispose();
        _image.Dispose();
    }

    private IndexSnapshot Snapshot() => new() { Epoch = 1, Markers = new[] { _marker } };

    private void PrepareTicks(int ticks)
    {
        for (var i = 0; i < ticks; i++)
            _worker.Prepare(Snapshot(), _env, _clock.NowMs);
    }

    private int Requests(RequestKind kind) => _sink.All.OfType<RequestUpdate>().Count(u => u.Spec.Kind == kind);

    private CaptureRequest Request(RequestKind kind, int markerId, long tag) =>
        new(1, PipelineGroup.Scan, kind, markerId, new PixelRect(0, 0, 64, 64), tag, 0.0, 0.0, double.MaxValue, true, false);

    private CapturedFrame Frame(long tag)
    {
        var frame = _hub.Rent()!;
        frame.Begin(1000.0, 1001.0, 2560, 1440);
        frame.Add(Request(RequestKind.ScanArea, 0, tag), new Rect(0, 0, 64, 64), _image);
        frame.Add(Request(RequestKind.CheckCrop, _marker.Id, tag), new Rect(0, 0, 64, 64), _image);
        return frame;
    }

    [Fact]
    public void CheckCropsUseThePredictionTheCheckItselfUses()
    {
        const double interval = 16.67;
        var moving = MarkerRecord.Born(7, new PixelRect(1000, 700, 100, 14), 0.0).AfterHit(20.0, 1060.0, 700.0, 100.0).AfterHit(40.0, 1070.0, 700.0, 100.0);
        var snapshot = new IndexSnapshot { Epoch = 1, Markers = new[] { moving }, NewFrameMs = 400.0, MedianFrameIntervalMs = interval };
        var settings = PipelineSettings.Default;
        var target = 400.0 + interval;
        var expected = RegionPlanner.Crop(moving, target, moving.Height * settings.CheckMarginLines, 2560, 1440, settings.Adapt(interval));

        _worker.Prepare(snapshot, _env, 400.0);
        _worker.Prepare(snapshot, _env, 400.0);

        Assert.Equal(RegionPlanner.ToPixelRect(expected!.Value), _sink.All.OfType<RequestUpdate>().Single(u => u.Spec.Kind == RequestKind.CheckCrop).Spec.Region);
    }

    [Fact]
    public void EveryTickAsksForTheNameScan()
    {
        PrepareTicks(4);

        Assert.Equal(4, Requests(RequestKind.ScanArea));
    }

    [Fact]
    public void PlateChecksRunOnEverySecondTickAtEightPerSecond()
    {
        PrepareTicks(4);

        Assert.Equal(2, Requests(RequestKind.CheckCrop));
    }

    [Theory]
    [InlineData(6.0, 12, 8)]
    [InlineData(8.0, 16, 8)]
    [InlineData(4.0, 8, 8)]
    [InlineData(2.0, 4, 4)]
    public void PlateChecksHoldFourPerSecondAtAnyFasterScanRate(double scanHz, int ticks, int expectedChecks)
    {
        using var worker = new ScanWorker(PipelineSettings.Default with { ScanHz = scanHz }, _ops, null, _sink, _clock);

        for (var i = 0; i < ticks; i++)
            worker.Prepare(Snapshot(), _env, _clock.NowMs);

        Assert.Equal(expectedChecks, Requests(RequestKind.CheckCrop));
    }

    [Fact]
    public void TheMiningSearchKeepsThePlateCheckPace()
    {
        _worker.Mining = new MiningSignalReader();
        _worker.MiningEnabled = true;

        PrepareTicks(4);

        Assert.Equal(2, Requests(RequestKind.MiningSearch));
    }

    [Fact]
    public void NewPlatesArePublishedBeforeTheChecksRun()
    {
        _ops.Lines = new[] { NewLine };
        var publishedBeforeCheck = false;
        _ops.OnCheck = () => publishedBeforeCheck = _sink.All.OfType<NewMarkerUpdate>().Any();
        PrepareTicks(2);

        _worker.Process(Snapshot(), Frame(2), _env);

        Assert.True(publishedBeforeCheck);
    }

    [Fact]
    public void EachNewPlateIsPublishedExactlyOnce()
    {
        _ops.Lines = new[] { NewLine };
        PrepareTicks(2);

        _worker.Process(Snapshot(), Frame(2), _env);

        Assert.Single(_sink.All.OfType<NewMarkerUpdate>());
    }

    [Fact]
    public void ANewPlateCarriesTheDistanceReadFromIt()
    {
        _ops.Lines = new[] { NewLine };
        _ops.Distances[NewLine] = 6300.0;
        PrepareTicks(2);

        _worker.Process(Snapshot(), Frame(2), _env);

        Assert.Equal(6300.0, _sink.All.OfType<NewMarkerUpdate>().Single().DistanceMetres);
    }

    [Fact]
    public void APlateWithoutAParsedDistanceHasNone()
    {
        _ops.Lines = new[] { NewLine };
        PrepareTicks(2);

        _worker.Process(Snapshot(), Frame(2), _env);

        Assert.True(double.IsNaN(_sink.All.OfType<NewMarkerUpdate>().Single().DistanceMetres));
    }
}
