using Iris.Core.Detection;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed record ReadResult(
    ObservationClass Class,
    string Reason,
    string? Name = null,
    IReadOnlyList<O0Glyph>? O0 = null,
    PixelRect? Anchor = null,
    IReadOnlyList<PixelRect>? Rows = null,
    PlateColor? Color = null,
    NameLineInfo? NameLine = null);

public readonly record struct NearRead(bool Ok, PixelRect? Line);

public interface IPlateImageOps
{
    IReadOnlyList<PixelRect> Scan(CropImage scan);

    IReadOnlyList<PixelRect> Scan(CropImage scan, Func<PixelRect, bool>? isTracked) => Scan(scan);

    PixelRect? Check(CheckItem item);

    ReadResult Read(ReadItem item);

    Mat? MakeTemplate(CropImage crop, PixelRect nameBox, out PlateColorClass templateClass);

    bool ReadDistance(CropImage crop, PixelRect line) => true;

    bool ReadDistanceNear(CropImage crop, PixelRect expected, double margin) => ReadDistance(crop, expected);

    NearRead ReadNear(CropImage crop, PixelRect expected, double margin) => new(ReadDistanceNear(crop, expected, margin), null);
}

public sealed class DetectorImageOps : IPlateImageOps, IDisposable
{
    private readonly NameplateDetector _scanDetector;
    private readonly NameplateDetector _cropDetector;
    private readonly PlateSeedBuilder _seeds;
    private readonly NameplateDetectorSettings _cropSettings;
    private readonly int _windowHeight;
    private readonly Mat _bgrBox = new();

    public DetectorImageOps(NameplateReader reader, ScanGeometry geometry, NameplateDetectorSettings baseSettings, IContrastMaskSink? contrastSink = null)
    {
        Geometry = geometry;
        _windowHeight = geometry.WindowHeight;
        _cropSettings = baseSettings with { RegionLeft = 0.0, RegionRight = 1.0, RegionTop = 0.0, RegionBottom = 1.0, ScaleHeight = geometry.WindowHeight };
        _scanDetector = new NameplateDetector(reader, geometry.DetectorSettings) { ContrastSink = contrastSink };
        _cropDetector = new NameplateDetector(reader, _cropSettings) { ContrastSink = contrastSink };
        _seeds = new PlateSeedBuilder(baseSettings);
    }

    public ScanGeometry Geometry { get; }

    private double Px(double at1440) => at1440 * _windowHeight / 1440.0;

    public IReadOnlyList<PixelRect> Scan(CropImage scan) => Scan(scan, null);

    public IReadOnlyList<PixelRect> Scan(CropImage scan, Func<PixelRect, bool>? isTracked)
    {
        var origin = scan.Origin;
        var found = new List<PixelRect>();
        var candidates = _scanDetector.FindCandidates(scan.Image);
        var boxes = candidates.Boxes;
        var memo = new Dictionary<int, bool>();
        bool DistanceOk(int line)
        {
            if (!memo.TryGetValue(line, out var ok))
                memo[line] = ok = ReadsAsDistance(scan.Image, boxes[line]);
            return ok;
        }

        var trackedMemo = new Dictionary<int, bool>();
        bool Tracked(int line)
        {
            if (isTracked is null)
                return false;
            if (!trackedMemo.TryGetValue(line, out var tracked))
            {
                var box = boxes[line];
                trackedMemo[line] = tracked = isTracked(new PixelRect(box.X + origin.X, box.Y + origin.Y, box.Width, box.Height));
            }

            return tracked;
        }

        var linePairs = new Dictionary<int, List<(int Top, int Bottom)>>();
        void Index(int line, (int Top, int Bottom) pair)
        {
            if (!linePairs.TryGetValue(line, out var list))
                linePairs[line] = list = new List<(int, int)>();
            list.Add(pair);
        }

        var crowd = new Dictionary<(int, int), IReadOnlyList<int>>();
        foreach (var c in candidates.CrowdPairs)
            crowd[(c.Top, c.Bottom)] = c.Neighbours;
        var ordered = new List<(int Top, int Bottom)>(candidates.Pairs);
        foreach (var c in candidates.CrowdPairs)
            ordered.Add((c.Top, c.Bottom));
        foreach (var pair in ordered.Concat(candidates.LoosePairs))
        {
            Index(pair.Top, pair);
            Index(pair.Bottom, pair);
        }

        var taken = new HashSet<int>();
        foreach (var (top, bottom) in ordered)
        {
            if (taken.Contains(bottom) || Tracked(bottom))
                continue;
            if (crowd.TryGetValue((top, bottom), out var neighbours))
            {
                if (!DistanceOk(bottom))
                    continue;
                var supported = true;
                foreach (var line in neighbours)
                {
                    var any = false;
                    if (linePairs.TryGetValue(line, out var others))
                        foreach (var (otherTop, otherBottom) in others)
                            if (otherTop != top && otherTop != bottom && otherBottom != top && otherBottom != bottom && DistanceOk(otherBottom))
                            {
                                any = true;
                                break;
                            }

                    if (!any)
                    {
                        supported = false;
                        break;
                    }
                }

                if (!supported)
                    continue;
            }

            if (DistanceOk(bottom))
            {
                taken.Add(bottom);
                var box = boxes[bottom];
                found.Add(new PixelRect(box.X + origin.X, box.Y + origin.Y, box.Width, box.Height));
            }
        }

        return found;
    }

    private bool ReadsAsDistance(Mat image, Rect box)
    {
        if (image.Channels() != 4)
            return _scanDetector.ReadDistanceLine(image, box) is not null;
        const int pad = 2;
        var x0 = Math.Max(0, box.X - pad);
        var y0 = Math.Max(0, box.Y - pad);
        var x1 = Math.Min(image.Width, box.X + box.Width + pad);
        var y1 = Math.Min(image.Height, box.Y + box.Height + pad);
        if (x1 <= x0 || y1 <= y0)
            return false;
        using var area = new Mat(image, new Rect(x0, y0, x1 - x0, y1 - y0));
        Cv2.CvtColor(area, _bgrBox, ColorConversionCodes.BGRA2BGR);
        return _scanDetector.ReadDistanceLine(_bgrBox, new Rect(box.X - x0, box.Y - y0, box.Width, box.Height)) is not null;
    }

    public PixelRect? Check(CheckItem item)
    {
        var image = item.Crop.Image;
        if (image.Height < Px(_cropSettings.ReadCropMinHeight) || image.Width < Px(_cropSettings.ReadCropMinWidth))
            return null;
        var origin = item.Crop.Origin;
        var candidates = _cropDetector.FindCandidates(image);
        var near = new Dictionary<int, double>();
        var tolerance = Math.Max(_cropSettings.NearHeightMinPx, _cropSettings.NearHeightTolerance * item.LineHeight);
        void Consider(int line)
        {
            var box = candidates.Boxes[line];
            var dx = Math.Min(Math.Abs(box.X + origin.X - item.PredictedX), Math.Abs(box.X + box.Width / 2.0 + origin.X - (item.PredictedX + item.Expected.Width / 2.0)));
            var dy = box.Y + origin.Y - item.PredictedY;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > item.Margin + Px(_cropSettings.NearSlackPx) || Math.Abs(box.Height - item.LineHeight) > tolerance)
                return;
            if (!near.TryGetValue(line, out var known) || distance < known)
                near[line] = distance;
        }

        foreach (var (_, bottom) in candidates.Pairs)
            Consider(bottom);
        foreach (var crowd in candidates.CrowdPairs)
            Consider(crowd.Bottom);

        var tries = 0;
        foreach (var (line, _) in near.OrderBy(kv => kv.Value))
        {
            if (++tries > _cropSettings.NearMaxTries)
                break;
            if (_cropDetector.ReadDistanceLine(image, candidates.Boxes[line]) is not null)
            {
                var box = candidates.Boxes[line];
                return new PixelRect(box.X + origin.X, box.Y + origin.Y, box.Width, box.Height);
            }
        }

        return null;
    }

    public bool ReadDistance(CropImage crop, PixelRect line)
    {
        var image = crop.Image;
        var origin = crop.Origin;
        var left = Math.Max(0, (int)Math.Floor(line.X - origin.X) - 2);
        var top = Math.Max(0, (int)Math.Floor(line.Y - origin.Y) - 1);
        var right = Math.Min(image.Width, (int)Math.Ceiling(line.Right - origin.X) + 2);
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(line.Bottom - origin.Y) + 1);
        if (right - left < 4 || bottom - top < 4)
            return false;
        return _cropDetector.ReadDistanceLine(image, new Rect(left, top, right - left, bottom - top)) is not null;
    }

    public bool ReadDistanceNear(CropImage crop, PixelRect expected, double margin) => ReadNear(crop, expected, margin).Ok;

    public NearRead ReadNear(CropImage crop, PixelRect expected, double margin)
    {
        var image = crop.Image;
        if (image.Empty() || image.Height < Px(_cropSettings.NearCropMinHeight) || image.Width < Px(_cropSettings.NearCropMinWidth))
            return new NearRead(false, null);
        var origin = crop.Origin;
        var tolerance = Math.Max(_cropSettings.NearHeightMinPx, _cropSettings.NearHeightTolerance * expected.Height);
        var near = new List<(double Distance, Rect Box)>();
        foreach (var box in _cropDetector.FindLines(image, true))
        {
            if (Math.Abs(box.Height - expected.Height) > tolerance)
                continue;
            var dx = Math.Min(Math.Abs(box.X + origin.X - expected.X), Math.Abs(box.X + box.Width / 2.0 + origin.X - (expected.X + expected.Width / 2.0)));
            var dy = box.Y + origin.Y - expected.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= margin + Px(_cropSettings.NearSlackPx))
                near.Add((distance, box));
        }

        var tries = 0;
        foreach (var (_, box) in near.OrderBy(n => n.Distance))
        {
            if (++tries > _cropSettings.NearMaxTries)
                break;
            if (_cropDetector.ReadDistanceLine(image, box) is not null)
                return new NearRead(true, new PixelRect(box.X + origin.X, box.Y + origin.Y, box.Width, box.Height));
        }

        return new NearRead(false, null);
    }

    public ReadResult Read(ReadItem item)
    {
        var image = item.Crop.Image;
        var origin = item.Crop.Origin;
        if (image.Height < Px(_cropSettings.ReadCropMinHeight) || image.Width < Px(_cropSettings.ReadCropMinWidth))
            return new ReadResult(ObservationClass.Unclear, "offscreen");
        var candidates = _cropDetector.FindCandidates(image);
        var boxes = candidates.Boxes;
        var lh = item.LineHeight;
        var heightTolerance = Math.Max(_cropSettings.NearHeightMinPx, _cropSettings.NearHeightTolerance * lh);

        var anchors = new List<int>();
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            var offLeft = Math.Abs(b.X + origin.X - item.Expected.X);
            var offCentre = Math.Abs(b.X + b.Width / 2.0 + origin.X - (item.Expected.X + item.Expected.Width / 2.0));
            if (Math.Min(offLeft, offCentre) <= Px(_cropSettings.AnchorMaxOffsetPx) && Math.Abs(b.Y + origin.Y - item.Expected.Y) <= _cropSettings.AnchorMaxRowOffsetLines * lh && Math.Abs(b.Height - lh) <= heightTolerance)
                anchors.Add(i);
        }

        anchors.Sort((a, b) => boxes[b].Width.CompareTo(boxes[a].Width));
        var bi = -1;
        foreach (var i in anchors.Take(2))
        {
            if (_cropDetector.ReadDistanceLine(image, boxes[i]) is not null)
            {
                bi = i;
                break;
            }
        }

        if (bi < 0)
            return new ReadResult(ObservationClass.Unclear, "nodist");

        var bottom = boxes[bi];
        var bottomCentre = bottom.X + bottom.Width / 2.0;
        var tops = new List<(double Gap, int Line)>();
        for (var ti = 0; ti < boxes.Count; ti++)
        {
            if (ti == bi)
                continue;
            var t = boxes[ti];
            var gap = bottom.Y - (t.Y + t.Height);
            if (!(gap >= 1 && gap <= t.Height * _cropSettings.TopGapMaxLines) || Math.Abs(bottom.Height - t.Height) > Math.Max(Px(_cropSettings.TopHeightDiffMinPx), t.Height * _cropSettings.TopHeightDiffLines))
                continue;
            if (t.X >= bottom.X + bottom.Width || t.X + t.Width <= bottom.X)
                continue;
            tops.Add((gap, ti));
        }

        tops.Sort((a, b) => a.Gap.CompareTo(b.Gap));
        var centred = tops.Where(t => Math.Abs(boxes[t.Line].X + boxes[t.Line].Width / 2.0 - bottomCentre) <= Px(_cropSettings.TopCentreTolerancePx)).Select(t => t.Line).ToList();
        var off = tops.Where(t => Math.Abs(boxes[t.Line].X + boxes[t.Line].Width / 2.0 - bottomCentre) > Px(_cropSettings.TopCentreTolerancePx)).Select(t => t.Line).ToList();
        bool LeftAligned(Rect t) => Math.Abs(t.X - bottom.X) <= Px(_cropSettings.TopLeftTolerancePx) && Math.Abs(t.Width - bottom.Width) >= Px(_cropSettings.TopLeftWidthDifferencePx);
        var valid = centred.Where(i => !LeftAligned(boxes[i])).ToList();
        if (valid.Count > 0)
        {
            var top = valid[0];
            var result = ReadPair(image, origin, candidates, top, bi);
            var strict = candidates.Pairs.Contains((top, bi)) || candidates.CrowdPairs.Any(c => c.Top == top && c.Bottom == bi);
            if (result.Class != ObservationClass.Name && !strict)
                return new ReadResult(ObservationClass.Unclear, "clutter");
            return result;
        }

        if (centred.Count > 0)
            return new ReadResult(ObservationClass.Unclear, "leftcol");
        if (off.Count > 0)
            return new ReadResult(ObservationClass.Stop, "offcenter");
        return new ReadResult(ObservationClass.Unclear, "nothing");
    }

    public Mat? MakeTemplate(CropImage crop, PixelRect nameBox, out PlateColorClass templateClass)
    {
        var local = new Rect(
            (int)Math.Round(nameBox.X - crop.Origin.X), (int)Math.Round(nameBox.Y - crop.Origin.Y),
            (int)Math.Round(nameBox.Width), (int)Math.Round(nameBox.Height));
        return _seeds.MakeTemplate(crop.Image, local, _windowHeight, out templateClass);
    }

    public void Dispose()
    {
        _scanDetector.Dispose();
        _cropDetector.Dispose();
        _seeds.Dispose();
        _bgrBox.Dispose();
    }

    private ReadResult ReadPair(Mat image, Rect origin, NameplateCandidates candidates, int top, int bottom)
    {
        var boxes = candidates.Boxes;
        var stack = candidates.StackOf(boxes[top], boxes[bottom]);
        IReadOnlyList<Rect> rows = stack is { Rows.Count: > 0 } ? stack.Rows : new[] { boxes[top] };
        IReadOnlyList<Rect> blockers = stack?.Blockers ?? Array.Empty<Rect>();
        var nameBox = boxes[top];
        var nameRead = _cropDetector.ReadNameRow(image, nameBox);
        if (nameRead.Text.Length == 0 || !nameRead.OneWord)
            return Bad(nameRead);
        var reads = new List<NameRowRead>(rows.Count);
        var allWords = true;
        foreach (var row in rows)
        {
            var read = row == nameBox ? nameRead : _cropDetector.ReadNameRow(image, row);
            reads.Add(read);
            if (read.Text.Length == 0 || !read.OneWord)
            {
                allWords = false;
                break;
            }
        }

        if (!allWords)
        {
            rows = new[] { nameBox };
            blockers = Array.Empty<Rect>();
            reads = new List<NameRowRead> { nameRead };
        }

        var text = string.Concat(reads.Select(r => r.Text));
        if (reads.Any(r => !r.ValidChars))
            return new ReadResult(ObservationClass.Stop, "badchars");

        var glyphs = new List<O0Glyph>();
        var offset = 0;
        var total = 0.0;
        foreach (var read in reads)
        {
            foreach (var glyph in read.O0)
                glyphs.Add(new O0Glyph(offset + glyph.Index, glyph.ProbabilityO));
            offset += read.Text.Length;
            total += read.Score;
        }

        if (text.Length > _cropSettings.MaxNameLength)
            return new ReadResult(ObservationClass.Stop, "toolong");
        if (text.Length < _cropSettings.MinNameLength)
            return new ReadResult(ObservationClass.Unclear, "short");
        foreach (var blocker in blockers)
        {
            if (_cropDetector.ReadsAsNameNotDistance(image, blocker))
                return new ReadResult(ObservationClass.Unclear, "partialstack");
        }

        if (!NameShape.HasValidEdges(text))
            return new ReadResult(ObservationClass.Unclear, "edgehyphen");
        if (total / text.Length < _cropSettings.MinNameScore)
            return new ReadResult(ObservationClass.Unclear, "lowconf");

        var color = PlateColors.Classify(reads.Select(r => r.Mask!), _cropSettings.PlateColorRules)?.Color;
        var line = boxes[bottom];
        PixelRect Window(Rect r) => new(r.X + origin.X, r.Y + origin.Y, r.Width, r.Height);
        var lowest = rows.OrderByDescending(r => r.Y).First();
        var nameLine = new NameLineInfo(lowest.Width, lowest.Height, line.Y - lowest.Y, _cropDetector.InkColour(image, lowest), _cropDetector.ColumnInk(lowest));
        return new ReadResult(ObservationClass.Name, string.Empty, text, glyphs, Window(line), rows.Select(Window).ToList(), color, nameLine);
    }

    private static ReadResult Bad(NameRowRead read) =>
        read.Text.Length == 0
            ? new ReadResult(ObservationClass.Unclear, "unreadable")
            : new ReadResult(ObservationClass.Stop, "notoneword");
}

public sealed class NullImageOps : IPlateImageOps
{
    public IReadOnlyList<PixelRect> Scan(CropImage scan) => Array.Empty<PixelRect>();

    public PixelRect? Check(CheckItem item) => null;

    public ReadResult Read(ReadItem item) => new(ObservationClass.Unclear, "nothing");

    public Mat? MakeTemplate(CropImage crop, PixelRect nameBox, out PlateColorClass templateClass)
    {
        templateClass = PlateColorClass.Any;
        return null;
    }
}
