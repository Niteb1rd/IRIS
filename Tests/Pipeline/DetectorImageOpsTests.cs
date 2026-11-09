using Iris.Core.Detection;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using OpenCvSharp;
using Xunit;

namespace Iris.Tests.Pipeline;

public class DetectorImageOpsTests : IDisposable
{
    private static readonly NameplateReader Reader = new(LetterModel.LoadEmbedded());
    private static readonly NameplateDetectorSettings Settings = NameplateDetectorSettings.Default;

    private readonly ScanGeometry _geometry;
    private readonly Mat _frame;

    public DetectorImageOpsTests()
    {
        Assert.True(ScanGeometry.TryCreate(2560, 1440, Settings, TrackingBoundary.FromPercents(15, 15, 8, 8), out var geometry));
        _geometry = geometry!;
        _frame = new Mat(_geometry.Capture.Height, _geometry.Capture.Width, MatType.CV_8UC4, new Scalar(30, 30, 30, 255));
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 4; column++)
                Plate(_frame, 200 + column * 420, 150 + row * 240);
    }

    public void Dispose() => _frame.Dispose();

    private static void Plate(Mat image, int x, int y)
    {
        const HersheyFonts font = HersheyFonts.HersheySimplex;
        const double scale = 0.62;
        var name = Cv2.GetTextSize("SOMEPILOT", font, scale, 1, out _);
        var distance = Cv2.GetTextSize("4.3km", font, scale, 1, out _);
        Cv2.PutText(image, "SOMEPILOT", new Point(x, y), font, scale, Scalar.White, 1, LineTypes.AntiAlias);
        Cv2.PutText(image, "4.3km", new Point(x + (name.Width - distance.Width) / 2, y + 26), font, scale, Scalar.White, 1, LineTypes.AntiAlias);
    }

    private (IReadOnlyList<PixelRect> Lines, IReadOnlyDictionary<PixelRect, double> Distances) ScanWith(int helpers, LayerRule rule)
    {
        using var ops = new DetectorImageOps(Reader, _geometry, Settings, null, helpers);
        ops.Layers.Rule = rule;
        using var crop = new CropImage(_frame, _geometry.Capture, false);
        var lines = ops.Scan(crop);
        return (lines.ToArray(), new Dictionary<PixelRect, double>(ops.LastScanDistances));
    }

    [Fact]
    public void TheTestFrameGivesTheReadStepSeveralCandidates()
    {
        using var bgr = new Mat();
        Cv2.CvtColor(_frame, bgr, ColorConversionCodes.BGRA2BGR);
        using var detector = new NameplateDetector(Reader, _geometry.DetectorSettings);

        var found = detector.FindCandidates(bgr);

        Assert.True(found.Pairs.Count >= 2, $"boxes={found.Boxes.Count} pairs={found.Pairs.Count} loose={found.LoosePairs.Count} crowd={found.CrowdPairs.Count}");
    }

    [Theory]
    [InlineData(LayerRule.Brightest)]
    [InlineData(LayerRule.Agree)]
    [InlineData(LayerRule.Measure)]
    public void ParallelReadsMatchSerialReads(LayerRule rule)
    {
        var serial = ScanWith(0, rule);

        for (var round = 0; round < 20; round++)
        {
            var parallel = ScanWith(1, rule);

            Assert.Equal(serial.Lines, parallel.Lines);
            Assert.Equal(serial.Distances.OrderBy(d => d.Key.X).ThenBy(d => d.Key.Y), parallel.Distances.OrderBy(d => d.Key.X).ThenBy(d => d.Key.Y));
        }
    }

    private ReadResult ReadOffCentrePlate(byte background)
    {
        const HersheyFonts font = HersheyFonts.HersheySimplex;
        const double scale = 0.62;
        using var image = new Mat(220, 640, MatType.CV_8UC3, Scalar.All(background));
        Cv2.PutText(image, "SOMEPILOT", new Point(300, 90), font, scale, Scalar.White, 1, LineTypes.AntiAlias);
        Cv2.PutText(image, "4.3km", new Point(240, 116), font, scale, Scalar.White, 1, LineTypes.AntiAlias);
        var cropSettings = Settings with { RegionLeft = 0.0, RegionRight = 1.0, RegionTop = 0.0, RegionBottom = 1.0, ScaleHeight = _geometry.WindowHeight };
        using var detector = new NameplateDetector(Reader, cropSettings);
        var line = detector.FindCandidates(image).Boxes.OrderBy(b => Math.Abs(b.X - 240) + Math.Abs(b.Y - 104)).First();
        using var ops = new DetectorImageOps(Reader, _geometry, Settings, null, 0);
        using var crop = new CropImage(image, new Rect(0, 0, image.Width, image.Height), false);

        return ops.Read(new ReadItem(1, crop, new PixelRect(line.X, line.Y, line.Width, line.Height), line.Height));
    }

    [Fact]
    public void AnOffCentreLineOnADarkBackgroundStillStopsReading()
    {
        var result = ReadOffCentrePlate(30);

        Assert.Equal(ObservationClass.Stop, result.Class);
        Assert.Equal(ReadResult.OffCentre, result.Reason);
    }

    [Theory]
    [InlineData(NameplateDetectorSettings.BrightBackgroundValue)]
    [InlineData(180)]
    public void AnOffCentreLineOnABrightBackgroundDoesNotRetireThePlate(byte background)
    {
        var result = ReadOffCentrePlate(background);

        Assert.Equal(ObservationClass.Unclear, result.Class);
        Assert.Equal(ReadResult.OffCentre, result.Reason);
    }

    [Fact]
    public void AnOffCentreLineJustBelowTheBrightLevelStillStopsReading()
    {
        var result = ReadOffCentrePlate(NameplateDetectorSettings.BrightBackgroundValue - 1);

        Assert.Equal(ObservationClass.Stop, result.Class);
    }

    [Fact]
    public void AFixedBoxReadsADistanceWhereItIsAndNothingOnEmptyGround()
    {
        const HersheyFonts font = HersheyFonts.HersheySimplex;
        using var image = new Mat(160, 400, MatType.CV_8UC3, Scalar.All(30));
        Cv2.PutText(image, "4.3km", new Point(160, 100), font, 0.62, Scalar.White, 1, LineTypes.AntiAlias);
        var size = Cv2.GetTextSize("4.3km", font, 0.62, 1, out _);
        using var ops = new DetectorImageOps(Reader, _geometry, Settings, null, 0);
        using var crop = new CropImage(image, new Rect(1000, 500, image.Width, image.Height), false);

        Assert.True(ops.ReadDistance(crop, new PixelRect(1160, 600 - size.Height, size.Width, size.Height)));
        Assert.False(ops.ReadDistance(crop, new PixelRect(1160, 520, size.Width, size.Height)));
    }

    private static Mat BusyFrame(int width, int height)
    {
        var frame = new Mat(height, width, MatType.CV_8UC4, new Scalar(40, 40, 40, 255));
        using (var noise = new Mat(height, width, MatType.CV_8UC4))
        {
            Cv2.Randu(noise, Scalar.All(0), new Scalar(35, 35, 35, 0));
            Cv2.Add(frame, noise, frame);
        }

        var middle = height / 2;
        Cv2.Rectangle(frame, new Rect(0, middle - 120, width, 240), new Scalar(175, 205, 225, 255), -1);
        Cv2.Rectangle(frame, new Rect(width / 2, 0, width / 4, height), new Scalar(235, 240, 245, 255), -1);
        for (var row = 0; row < 7; row++)
            for (var column = 0; column < 6; column++)
                Plate(frame, 60 + column * (width / 6), 50 + row * (height / 7));
        return frame;
    }

    private static (Rect[] Boxes, int[] Columns) MaskResult(Mat frame, NameplateDetectorSettings settings, LowPriorityWorkers? workers)
    {
        using var detector = new NameplateDetector(Reader, settings) { Workers = workers };
        var boxes = detector.FindCandidates(frame).Boxes.ToArray();
        return (boxes, detector.ColumnInk(new Rect(0, 0, frame.Cols, frame.Rows)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AMaskBuiltInBandsMatchesTheWholeFrameMask(int helpers)
    {
        using var frame = BusyFrame(_geometry.Capture.Width, _geometry.Capture.Height);
        using var workers = new LowPriorityWorkers(helpers);

        var whole = MaskResult(frame, _geometry.DetectorSettings, null);
        var banded = MaskResult(frame, _geometry.DetectorSettings, workers);

        Assert.NotEmpty(whole.Boxes);
        Assert.Equal(whole.Columns, banded.Columns);
        Assert.Equal(whole.Boxes, banded.Boxes);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1366, 768)]
    [InlineData(2560, 1080)]
    [InlineData(1913, 1037)]
    public void BandedMasksMatchAtCommonAndOddWindowSizes(int width, int height)
    {
        Assert.True(ScanGeometry.TryCreate(width, height, Settings, TrackingBoundary.FromPercents(15, 15, 8, 8), out var geometry));
        using var frame = BusyFrame(geometry!.Capture.Width, geometry.Capture.Height);
        using var workers = new LowPriorityWorkers(1);

        var whole = MaskResult(frame, geometry.DetectorSettings, null);
        var banded = MaskResult(frame, geometry.DetectorSettings, workers);

        Assert.Equal(whole.Columns, banded.Columns);
        Assert.Equal(whole.Boxes, banded.Boxes);
    }

    private sealed class ContrastTotals : IContrastMaskSink
    {
        public int Mask { get; private set; }

        public int Contrast { get; private set; }

        public void AddContrastMask(int maskPixels, int contrastPixels)
        {
            Mask += maskPixels;
            Contrast += contrastPixels;
        }
    }

    [Fact]
    public void BandedMasksReportTheSameContrastShareAsAWholeFrame()
    {
        using var frame = new Mat(_geometry.Capture.Height, _geometry.Capture.Width, MatType.CV_8UC4, new Scalar(195, 195, 195, 255));
        for (var row = 0; row < 40; row++)
            for (var column = 0; column < 6; column++)
                Plate(frame, 60 + column * (frame.Width / 6), 30 + row * (frame.Height / 40));
        using var workers = new LowPriorityWorkers(1);
        var whole = new ContrastTotals();
        var banded = new ContrastTotals();
        using (var detector = new NameplateDetector(Reader, _geometry.DetectorSettings) { ContrastSink = whole })
            detector.FindCandidates(frame);
        using (var detector = new NameplateDetector(Reader, _geometry.DetectorSettings) { ContrastSink = banded, Workers = workers })
            detector.FindCandidates(frame);

        Assert.True(whole.Contrast > 0);
        Assert.Equal(whole.Mask, banded.Mask);
        Assert.Equal(whole.Contrast, banded.Contrast);
    }

    [Fact]
    public void BandedMasksStayCorrectAcrossRepeatedScans()
    {
        using var frame = BusyFrame(_geometry.Capture.Width, _geometry.Capture.Height);
        using var workers = new LowPriorityWorkers(1);
        using var detector = new NameplateDetector(Reader, _geometry.DetectorSettings) { Workers = workers };
        var whole = MaskResult(frame, _geometry.DetectorSettings, null);

        for (var round = 0; round < 30; round++)
        {
            Assert.Equal(whole.Boxes, detector.FindCandidates(frame).Boxes);
            Assert.Equal(whole.Columns, detector.ColumnInk(new Rect(0, 0, frame.Cols, frame.Rows)));
        }
    }

    [Fact]
    public void AReusedScannerStaysConsistentAcrossManyScans()
    {
        using var ops = new DetectorImageOps(Reader, _geometry, Settings, null, 1);
        ops.Layers.Rule = LayerRule.Agree;
        using var crop = new CropImage(_frame, _geometry.Capture, false);
        var first = ops.Scan(crop).ToArray();

        for (var round = 0; round < 50; round++)
            Assert.Equal(first, ops.Scan(crop));
    }
}
