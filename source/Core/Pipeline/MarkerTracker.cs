using Iris.Core.Detection;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public readonly record struct PatchPlan(int Id, Rect Region, double PredictedX, double PredictedY, double Margin);

public readonly record struct LinePosition(double CentreX, double Top, double Width);

public interface ILineTracker
{
    IReadOnlyList<FoundLine> Find(MarkerRecord marker, PatchPlan plan, Mat patch);

    LinePosition Refine(PatchPlan plan, Mat patch, PixelRect box);

    PixelRect? LocateLine(Mat patch, PatchPlan plan, double lineHeight, IReadOnlyList<PixelRect> claimed, double lineWidth = double.NaN);
}

public sealed class MarkerTracker : ILineTracker
{
    private readonly PipelineSettings _settings;
    private readonly NameplateDetector _lines;
    private readonly int _windowHeight;

    public MarkerTracker(PipelineSettings settings, NameplateDetector lineFinder, int windowHeight)
    {
        _settings = settings;
        _lines = lineFinder;
        _windowHeight = windowHeight;
    }

    private double Scale => _windowHeight / 1440.0;

    private static readonly int[] NoInk = Array.Empty<int>();

    public IReadOnlyList<FoundLine> Find(MarkerRecord marker, PatchPlan plan, Mat patch)
    {
        if (patch.Height < _settings.MinPatchHeightPx * Scale || patch.Width < _settings.MinPatchWidthPx * Scale)
            return Array.Empty<FoundLine>();
        var origin = plan.Region;
        var withNarrow = marker.NameLine is { } stored && _lines.IsNarrowLine(stored.Width, stored.Height, _windowHeight);
        var rects = _lines.FindLines(patch, true, out var real, out var narrowCount, withNarrow);
        var narrowStart = rects.Count - narrowCount;
        var nameLine = marker.Status == PlateStatus.Confirmed ? marker.NameLine : null;
        var gate = _settings.TrackWidthGateLines * marker.Height;
        var distanceWidth = PartialLine.StoredWidth(marker);
        var centre = plan.PredictedX + marker.Width / 2.0;
        var found = new List<FoundLine>(rects.Count);
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            var box = new PixelRect(rect.X + origin.X, rect.Y + origin.Y, rect.Width, rect.Height);
            var narrow = i >= narrowStart;
            PixelRect? part = null;
            if (i >= real && !narrow)
            {
                var bestOffset = double.MaxValue;
                for (var r = 0; r < real; r++)
                {
                    var left = rects[r];
                    if (left.X != rect.X || left.Width >= rect.Width || Math.Abs(left.Y - rect.Y) >= bestOffset)
                        continue;
                    bestOffset = Math.Abs(left.Y - rect.Y);
                    part = new PixelRect(left.X + origin.X, left.Y + origin.Y, left.Width, left.Height);
                }
            }

            var plain = part is null;
            var splittable = plain && !narrow && box.Width > distanceWidth + gate && centre >= box.X && centre <= box.Right;
            var nameCandidate = plain && nameLine is not null && TrackAssigner.NameSizeFits(box.Width, box.Height, nameLine, gate, _settings);
            var ink = splittable || nameCandidate ? _lines.ColumnInk(rect) : NoInk;
            var colour = nameCandidate && (i < real || narrow) ? _lines.InkColour(patch, rect) : null;
            found.Add(new FoundLine(box, ink, part, colour, narrow));
        }

        return found;
    }

    public LinePosition Refine(PatchPlan plan, Mat patch, PixelRect box)
    {
        var origin = plan.Region;
        var local = new Rect((int)Math.Round(box.X - origin.X), (int)Math.Round(box.Y - origin.Y), (int)Math.Round(box.Width), (int)Math.Round(box.Height));
        var fix = LineSubPixel.Measure(patch, local);
        return new LinePosition(origin.X + fix.CentreX, origin.Y + fix.Top, box.Width);
    }

    public PixelRect? LocateLine(Mat patch, PatchPlan plan, double lineHeight, IReadOnlyList<PixelRect> claimed, double lineWidth = double.NaN)
    {
        if (patch.Height < _settings.MinPatchHeightPx * Scale || patch.Width < _settings.MinPatchWidthPx * Scale)
            return null;
        var origin = plan.Region;
        var lines = _lines.FindLines(patch, !double.IsNaN(lineWidth));
        var window = new List<PixelRect>(lines.Count);
        foreach (var line in lines)
            window.Add(new PixelRect(line.X + origin.X, line.Y + origin.Y, line.Width, line.Height));
        return LineChooser.Choose(window, plan.PredictedX, plan.PredictedY, lineHeight, Math.Ceiling(plan.Margin), claimed, _settings, lineWidth);
    }
}
