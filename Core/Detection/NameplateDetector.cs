using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Iris.Core.Detection;

public sealed partial class NameplateDetector : IDisposable
{
    public const string NameAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";

    private readonly NameplateReader _reader;
    private readonly NameplateDetectorSettings _settings;
    private readonly bool[] _distanceAlphabet;
    private readonly bool[] _nameAlphabet;
    private readonly Dictionary<(int Width, int Height), Mat> _kernels = new();

    private readonly Mat _blue = new();
    private readonly Mat _green = new();
    private readonly Mat _red = new();
    private readonly Mat _hat = new();
    private readonly Mat _hatBits = new();
    private readonly Mat _cyanWhite = new();
    private readonly Mat _redMask = new();
    private readonly Mat _greenMask = new();
    private readonly Mat _scratch = new();
    private readonly Mat _mask = new();
    private readonly Mat _kept = new();
    private readonly Mat _labelHit = new();
    private readonly Mat _maskLabels = new();
    private readonly Mat _joined = new();
    private readonly Mat _labels = new();
    private readonly Mat _stats = new();
    private readonly Mat _centroids = new();
    private readonly Mat _value = new();
    private readonly Mat _bgSmall = new();
    private readonly Mat _bgBlur = new();
    private readonly Mat _background = new();
    private readonly Mat _bright = new();
    private readonly Mat _limit = new();
    private readonly Mat _ink = new();
    private readonly Mat _hatLimits;
    private int[] _statsBuffer = Array.Empty<int>();
    private Mat? _narrowFillMask;
    private byte[] _fillRow = Array.Empty<byte>();
    private bool[] _solid = Array.Empty<bool>();
    private bool[] _tall = Array.Empty<bool>();
    private bool _disposed;

    public NameplateDetector(NameplateReader reader, NameplateDetectorSettings? settings = null)
    {
        _reader = reader;
        _settings = settings ?? NameplateDetectorSettings.Default;
        var problem = _settings.Validate();
        if (problem is not null)
            throw new ArgumentException(problem, nameof(settings));
        _distanceAlphabet = reader.CreateAlphabet(DistanceLineRule.Alphabet);
        _nameAlphabet = reader.CreateAlphabet(NameAlphabet);
        _hatLimits = HatLimits(_settings);
    }

    public IContrastMaskSink? ContrastSink { get; set; }

    private DistanceReading? ReadDistance(Mat bgr, Rect box) => ReadDistance(NameplateFeatures.CreateMask(bgr, box));

    private DistanceReading? ReadDistance(LineMask? mask)
    {
        if (mask is null)
            return null;
        var read = _reader.ReadPieces(mask, _distanceAlphabet, _settings.DistanceMinInk);
        return DistanceLineRule.Evaluate(read, _settings, piece => _reader.IsOpenBracket(mask, piece.Start, piece.End, _settings.BracketMinScore));
    }

    public NameplateCandidates FindCandidates(Mat bgr) => FindCandidates(bgr, null);


    public NameplateCandidates FindCandidates(Mat bgr, IReadOnlyList<Rect>? blanks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || (bgr.Channels() != 3 && bgr.Channels() != 4))
            throw new ArgumentException("Expected a non-empty 8 bit BGR or BGRA image.", nameof(bgr));

        var width = bgr.Width;
        var height = bgr.Height;
        var scaleHeight = _settings.EffectiveScaleHeight(height);
        var x0 = (int)(_settings.RegionLeft * width);
        var x1 = Math.Min(width, (int)(_settings.RegionRight * width));
        var y0 = (int)(_settings.RegionTop * height);
        var y1 = Math.Min(height, (int)(_settings.RegionBottom * height));
        var empty = new NameplateCandidates(Array.Empty<Rect>(), Array.Empty<(int, int)>(), Array.Empty<(int, int)>(), Array.Empty<CrowdPair>());
        if (x1 <= x0 || y1 <= y0)
            return empty;

        var pad = _settings.ContextPadding(scaleHeight);
        var px0 = Math.Max(0, x0 - pad);
        var px1 = Math.Min(width, x1 + pad);

        using var region = new Mat(bgr, new Rect(px0, y0, px1 - px0, y1 - y0));
        BuildColorMask(region, scaleHeight);
        BlankMask(blanks, px0, y0);
        var narrowShapes = new List<Rect>();
        var lines = FindTextLines(scaleHeight, narrowShapes);
        if (lines.Count == 0)
            return empty;

        var plain = new List<Rect>();
        foreach (var line in lines)
            if (x0 <= line.X + px0 && line.X + line.Width + px0 <= x1)
                plain.Add(line);
        if (plain.Count == 0)
            return empty;

        var plainCount = plain.Count;
        var unions = SpeedUnions(plain, scaleHeight);

        var narrowInside = new List<Rect>();
        foreach (var shape in narrowShapes)
            if (x0 <= shape.X + px0 && shape.X + shape.Width + px0 <= x1)
                narrowInside.Add(shape);
        List<Rect>? rowPool = null;
        List<Rect> Pool() => rowPool ??= RowCandidates(plain, plain.Count, scaleHeight);
        var promoted = narrowInside.Count > 0 ? PromoteNarrow(narrowInside, plain, unions, scaleHeight, Pool) : new List<(Rect Narrow, List<Rect> Bottoms)>();
        var local = new List<Rect>(plain);
        foreach (var item in promoted)
            local.Add(item.Narrow);
        var realCount = plainCount + promoted.Count;
        local.AddRange(unions);

        var clearKnown = new sbyte[local.Count];
        bool Clear(int index)
        {
            if (clearKnown[index] == 0)
                clearKnown[index] = IsClear(local[index]) ? (sbyte)1 : (sbyte)-1;
            return clearKnown[index] > 0;
        }

        var allPairs = PairLines(local, scaleHeight, realCount);
        if (promoted.Count > 0)
        {
            var sandwich = new HashSet<(Rect, Rect)>();
            foreach (var (narrowRow, bottoms) in promoted)
                foreach (var bottomRow in bottoms)
                    sandwich.Add((narrowRow, bottomRow));
            allPairs = allPairs
                .Where(pair => !(pair.Bottom >= plainCount && pair.Bottom < realCount)
                    && !(pair.Top >= plainCount && pair.Top < realCount && !sandwich.Contains((local[pair.Top], local[pair.Bottom]))))
                .ToList();
        }

        var stacks = FindStacks(local, allPairs, Pool, scaleHeight);
        var (pairs, loose, crowd) = SortPairs(local, Clear, allPairs, realCount);
        var boxes = new Rect[local.Count];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new Rect(local[i].X + px0, local[i].Y + y0, local[i].Width, local[i].Height);
        Rect Shift(Rect r) => new(r.X + px0, r.Y + y0, r.Width, r.Height);
        Dictionary<(Rect Top, Rect Bottom), PlateStack>? shifted = null;
        if (stacks.Count > 0)
        {
            shifted = new Dictionary<(Rect Top, Rect Bottom), PlateStack>();
            foreach (var ((t, b), (rows, blockers)) in stacks)
                shifted[(Shift(t), Shift(b))] = new PlateStack(rows.Select(Shift).ToList(), blockers.Select(Shift).ToList());
        }

        return new NameplateCandidates(boxes, pairs, loose, crowd, shifted);
    }

    public IReadOnlyList<Rect> FindLines(Mat bgr) => FindLines(bgr, false);

    public IReadOnlyList<Rect> FindLines(Mat bgr, bool withSpeedUnions) => FindLines(bgr, withSpeedUnions, out _);

    public IReadOnlyList<Rect> FindLines(Mat bgr, bool withSpeedUnions, out int realCount) => FindLines(bgr, withSpeedUnions, out realCount, out _, false);

    public IReadOnlyList<Rect> FindLines(Mat bgr, bool withSpeedUnions, out int realCount, out int narrowCount, bool withNarrowRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));
        var scaleHeight = _settings.EffectiveScaleHeight(bgr.Height);
        BuildColorMask(bgr, scaleHeight);
        var narrow = withNarrowRows ? new List<Rect>() : null;
        var lines = FindTextLines(scaleHeight, narrow);
        realCount = lines.Count;
        if (withSpeedUnions && lines.Count > 1)
            lines.AddRange(SpeedUnions(lines, scaleHeight));
        narrowCount = 0;
        if (narrow is { Count: > 0 })
        {
            var heightTolerance = Scaled(scaleHeight, _settings.HeightTolerancePx);
            var centerTolerance = Scaled(scaleHeight, _settings.CenterTolerance);
            var leftTolerance = Scaled(scaleHeight, _settings.LeftTolerance);
            var leftWidthDifference = Scaled(scaleHeight, _settings.LeftWidthDifference);
            var below = new LinesByTop(lines, null);
            var near = new List<int>();
            foreach (var shape in narrow)
            {
                below.Below(shape, near);
                var found = false;
                foreach (var i in near)
                {
                    if (PairOk(shape, lines[i], heightTolerance, centerTolerance, leftTolerance, leftWidthDifference))
                    {
                        found = true;
                        break;
                    }
                }

                if (found && NarrowFillOk(shape))
                {
                    lines.Add(shape);
                    narrowCount++;
                }
            }
        }

        return lines;
    }

    public bool IsNarrowLine(double width, double height, int frameHeight) => !(width >= Scaled(frameHeight, _settings.LineMinWidth) && width > height * _settings.LineAspectMin);

    public int[] ColumnInk(Rect box)
    {
        var columns = new int[Math.Max(0, box.Width)];
        if (_mask.Empty() || columns.Length == 0)
            return columns;
        var top = Math.Max(0, box.Y);
        var bottom = Math.Min(_mask.Rows, box.Y + box.Height);
        var row = new byte[columns.Length];
        for (var y = top; y < bottom; y++)
        {
            var from = Math.Max(0, box.X);
            var to = Math.Min(_mask.Cols, box.X + box.Width);
            if (to <= from)
                break;
            Marshal.Copy(IntPtr.Add(_mask.Ptr(y), from), row, 0, to - from);
            for (var x = from; x < to; x++)
                if (row[x - from] != 0)
                    columns[x - box.X]++;
        }

        return columns;
    }

    public PlateColor? InkColour(Mat bgr, Rect box)
    {
        if (_mask.Empty() || bgr.Empty() || bgr.Type() != MatType.CV_8UC3 || bgr.Rows != _mask.Rows || bgr.Cols != _mask.Cols)
            return null;
        var left = Math.Max(0, box.X);
        var right = Math.Min(_mask.Cols, box.X + box.Width);
        var top = Math.Max(0, box.Y);
        var bottom = Math.Min(_mask.Rows, box.Y + box.Height);
        if (right <= left || bottom <= top)
            return null;
        var width = right - left;
        var maskRow = new byte[width];
        var colourRow = new byte[width * 3];
        var colours = new List<byte>(width * (bottom - top));
        for (var y = top; y < bottom; y++)
        {
            Marshal.Copy(IntPtr.Add(_mask.Ptr(y), left), maskRow, 0, width);
            Marshal.Copy(IntPtr.Add(bgr.Ptr(y), left * 3), colourRow, 0, width * 3);
            for (var x = 0; x < width; x++)
            {
                if (maskRow[x] == 0)
                    continue;
                colours.Add(colourRow[x * 3]);
                colours.Add(colourRow[x * 3 + 1]);
                colours.Add(colourRow[x * 3 + 2]);
            }
        }

        var count = colours.Count / 3;
        if (count == 0)
            return null;
        var ink = new LineMask(1, count, new byte[count]) { InkColors = colours.ToArray() };
        return PlateColors.Classify(new[] { ink }, _settings.PlateColorRules)?.Color;
    }

    public DistanceReading? ReadDistanceLine(Mat bgr, Rect box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadDistance(bgr, box);
    }

    public NameRowRead ReadNameRow(Mat bgr, Rect box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadNameRow(bgr, NameplateFeatures.CreateMask(bgr, box, keepColors: true));
    }

    public bool ReadsAsNameNotDistance(Mat bgr, Rect box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mask = NameplateFeatures.CreateMask(bgr, box, keepColors: true);
        return ReadNameRow(bgr, mask).Usable && ReadDistance(mask) is null;
    }

    private NameRowRead ReadNameRow(Mat bgr, LineMask? mask)
    {
        if (mask is null)
            return NameRowRead.Empty;
        var read = _reader.ReadPieces(mask, _nameAlphabet);
        if (read.Pieces.Count == 0)
            return NameRowRead.Empty;
        var text = string.Concat(read.Pieces.Select(p => p.Text));
        if (text.Length == 0)
            return NameRowRead.Empty;
        var oneWord = IsOneWord(read);
        var validChars = true;
        foreach (var ch in text)
            if (!(ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                validChars = false;
        var score = 0.0;
        foreach (var piece in read.Pieces)
            score += piece.Score;
        if (!oneWord || !validChars)
            return new NameRowRead(text, score, oneWord, validChars, mask, Array.Empty<O0Glyph>());

        IReadOnlyList<O0Glyph> evidence = Array.Empty<O0Glyph>();
        if (_settings.SubPixelO0Rule)
        {
            var measured = new List<O0Glyph>();
            text = O0Rule.Correct(bgr, read, text, measured);
            evidence = measured;
        }

        if (_settings.UnderscoreHyphenRule)
            text = UnderscoreHyphenRule.Correct(read, text);
        return new NameRowRead(text, score, true, true, mask, evidence);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mat in new[] { _blue, _green, _red, _hat, _hatBits, _cyanWhite, _redMask, _greenMask, _scratch, _mask, _kept, _labelHit, _maskLabels, _joined, _labels, _stats, _centroids, _value, _bgSmall, _bgBlur, _background, _bright, _limit, _ink, _hatLimits })
            mat.Dispose();
        foreach (var kernel in _kernels.Values)
            kernel.Dispose();
        _kernels.Clear();
    }

    private bool IsOneWord(PieceRead read)
    {
        var pieces = read.Pieces;
        for (var i = 0; i + 1 < pieces.Count; i++)
            if (pieces[i + 1].Start - pieces[i].End > _settings.WordGap * read.CapHeight)
                return false;
        return true;
    }
}
