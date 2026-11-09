
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Iris.Core.Detection;

public sealed partial class NameplateDetector
{
    private int Scaled(int frameHeight, double value) => _settings.Scale(frameHeight, value);

    private int ScaledOdd(int frameHeight, double value)
    {
        var k = Scaled(frameHeight, value);
        return k % 2 == 1 ? k : k + 1;
    }

    private Mat Kernel(int width, int height)
    {
        if (!_kernels.TryGetValue((width, height), out var kernel))
        {
            kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(width, height));
            _kernels[(width, height)] = kernel;
        }

        return kernel;
    }

    private List<Rect> FindTextLines(int frameHeight, List<Rect>? narrow = null)
    {
        var maxHeight = Scaled(frameHeight, _settings.LineMaxHeight);
        var minHeight = Scaled(frameHeight, _settings.LineMinHeight);
        var minWidth = Scaled(frameHeight, _settings.LineMinWidth);
        var maxWidth = Scaled(frameHeight, NameplateDetectorSettings.MaxLineWidth);
        var minNarrowWidth = Scaled(frameHeight, _settings.NarrowMinWidth);
        var speck = Math.Max(1, (int)Math.Floor(_settings.SpeckArea * Math.Pow(frameHeight / (double)_settings.ReferenceHeight, 2) + 0.5));

        var boxes = new List<Rect>();
        var count = Cv2.ConnectedComponentsWithStats(_mask, _maskLabels, _stats, _centroids, PixelConnectivity.Connectivity8);
        if (count <= 1)
            return boxes;

        if (_maskLabels.Type() != MatType.CV_32SC1)
            throw new InvalidOperationException($"Unexpected label image type {_maskLabels.Type()}.");
        CopyStats(count);

        if (_solid.Length < count)
            _solid = new bool[count];
        if (_tall.Length < count)
            _tall = new bool[count];
        _solid[0] = false;
        _tall[0] = false;
        var tallCount = 0;
        for (var i = 1; i < count; i++)
        {
            _solid[i] = _statsBuffer[i * 5 + 4] > speck;
            var isTall = _statsBuffer[i * 5 + 3] > maxHeight;
            _tall[i] = isTall;
            if (isTall)
                tallCount++;
        }

        Mat kept;
        if (tallCount == 0)
        {
            kept = _mask;
        }
        else
        {
            _mask.CopyTo(_kept);
            ClearTallShapes(count, tallCount);
            kept = _kept;
        }

        _narrowFillMask = kept;

        Cv2.Dilate(kept, _joined, Kernel(Scaled(frameHeight, _settings.JoinWidthPx), 1));
        count = Cv2.ConnectedComponentsWithStats(_joined, _labels, _stats, _centroids, PixelConnectivity.Connectivity8);
        CopyStats(count);
        for (var i = 1; i < count; i++)
        {
            var x = _statsBuffer[i * 5 + 0];
            var y = _statsBuffer[i * 5 + 1];
            var w = _statsBuffer[i * 5 + 2];
            var h = _statsBuffer[i * 5 + 3];
            if (h < minHeight || h > maxHeight)
                continue;
            var isLine = w >= minWidth && w <= maxWidth && w > h * _settings.LineAspectMin;
            if (!isLine)
            {
                if (narrow is not null && w >= minNarrowWidth && w <= _settings.NarrowMaxLines * h)
                    narrow.Add(new Rect(x, y, w, h));
                continue;
            }

            using var roi = new Mat(kept, new Rect(x, y, w, h));
            var fill = Cv2.CountNonZero(roi) / (double)(w * h);
            if (fill > _settings.FillMin && fill < _settings.FillMax)
                boxes.Add(new Rect(x, y, w, h));
        }

        return boxes;
    }

    private void ClearTallShapes(int count, int tallCount)
    {
        if (tallCount <= 2)
        {
            for (var i = 1; i < count; i++)
                if (_tall[i])
                    ClearComponent(_kept, i);
            return;
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var i = 1; i < count; i++)
        {
            if (!_tall[i])
                continue;
            left = Math.Min(left, _statsBuffer[i * 5 + 0]);
            top = Math.Min(top, _statsBuffer[i * 5 + 1]);
            right = Math.Max(right, _statsBuffer[i * 5 + 0] + _statsBuffer[i * 5 + 2]);
            bottom = Math.Max(bottom, _statsBuffer[i * 5 + 1] + _statsBuffer[i * 5 + 3]);
        }

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(_kept.Cols, right);
        bottom = Math.Min(_kept.Rows, bottom);
        var width = right - left;
        if (width <= 0 || bottom <= top)
            return;
        var labelRow = new int[width];
        var maskRow = new byte[width];
        for (var y = top; y < bottom; y++)
        {
            Marshal.Copy(IntPtr.Add(_maskLabels.Ptr(y), left * 4), labelRow, 0, width);
            var any = false;
            for (var x = 0; x < width; x++)
            {
                if (labelRow[x] > 0 && _tall[labelRow[x]])
                {
                    any = true;
                    break;
                }
            }

            if (!any)
                continue;
            var target = IntPtr.Add(_kept.Ptr(y), left);
            Marshal.Copy(target, maskRow, 0, width);
            for (var x = 0; x < width; x++)
                if (labelRow[x] > 0 && _tall[labelRow[x]])
                    maskRow[x] = 0;
            Marshal.Copy(maskRow, 0, target, width);
        }
    }

    private void ClearComponent(Mat target, int label)
    {
        var box = new Rect(_statsBuffer[label * 5 + 0], _statsBuffer[label * 5 + 1], _statsBuffer[label * 5 + 2], _statsBuffer[label * 5 + 3]);
        using var labelsRoi = new Mat(_maskLabels, box);
        using var targetRoi = new Mat(target, box);
        Cv2.Compare(labelsRoi, new Scalar(label), _labelHit, CmpType.EQ);
        targetRoi.SetTo(Scalar.All(0), _labelHit);
    }

    private void CopyStats(int count)
    {
        var values = count * 5;
        if (_statsBuffer.Length < values)
            _statsBuffer = new int[values];
        Marshal.Copy(_stats.Data, _statsBuffer, 0, values);
    }

    private bool NarrowFillOk(Rect box)
    {
        if (_narrowFillMask is not { } fillMask || box.Width <= 0 || box.Height <= 0)
            return false;
        if (_fillRow.Length < box.Width)
            _fillRow = new byte[box.Width];
        var nonZero = 0;
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(fillMask.Ptr(y), box.X), _fillRow, 0, box.Width);
            for (var x = 0; x < box.Width; x++)
                if (_fillRow[x] != 0)
                    nonZero++;
        }

        var fill = nonZero / (double)(box.Width * box.Height);
        return fill > _settings.FillMin && fill < _settings.FillMax;
    }

    private sealed class LinesByTop
    {
        private readonly int[] _order;
        private readonly int[] _tops;

        public LinesByTop(List<Rect> first, List<Rect>? second)
        {
            var count = first.Count + (second?.Count ?? 0);
            var tops = new int[count];
            for (var i = 0; i < count; i++)
                tops[i] = i < first.Count ? first[i].Y : second![i - first.Count].Y;
            _order = new int[count];
            for (var i = 0; i < count; i++)
                _order[i] = i;
            Array.Sort(tops.ToArray(), _order);
            _tops = new int[count];
            for (var i = 0; i < count; i++)
                _tops[i] = tops[_order[i]];
        }

        public void Below(Rect shape, List<int> result)
        {
            result.Clear();
            var bottom = shape.Y + shape.Height;
            var farthest = bottom + (int)Math.Floor(shape.Height * NameplateDetectorSettings.PairGapMaxLines);
            var low = 0;
            var high = _tops.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (_tops[middle] < bottom + 1)
                    low = middle + 1;
                else
                    high = middle;
            }

            for (var i = low; i < _tops.Length && _tops[i] <= farthest; i++)
                result.Add(_order[i]);
            result.Sort();
        }
    }

    private bool IsClear(Rect box)
    {
        var reach = (int)Math.Ceiling(_settings.ClearLines * box.Height);
        var maskWidth = _maskLabels.Cols;
        var maskHeight = _maskLabels.Rows;
        var rowStart = Math.Max(0, box.Y);
        var rowEnd = Math.Min(maskHeight, box.Y + box.Height);
        if (rowEnd <= rowStart)
            return true;

        var leftStart = Math.Max(0, box.X - reach);
        var leftEnd = box.X;
        if (leftEnd > leftStart && HasInk(new Rect(leftStart, rowStart, leftEnd - leftStart, rowEnd - rowStart)))
            return false;

        var rightStart = box.X + box.Width;
        var rightEnd = Math.Min(maskWidth, box.X + box.Width + reach);
        if (rightEnd > rightStart && HasInk(new Rect(rightStart, rowStart, rightEnd - rightStart, rowEnd - rowStart)))
            return false;
        return true;
    }

    private bool HasInk(Rect area)
    {
        var row = new int[area.Width];
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(_maskLabels.Ptr(y), area.X * 4), row, 0, area.Width);
            foreach (var label in row)
                if (label > 0 && _solid[label])
                    return true;
        }

        return false;
    }

    private byte[] ObstacleWindow(Rect area, out bool any)
    {
        var window = new byte[area.Width * area.Height];
        var row = new int[area.Width];
        any = false;
        for (var y = 0; y < area.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(_maskLabels.Ptr(area.Y + y), area.X * 4), row, 0, area.Width);
            for (var x = 0; x < area.Width; x++)
            {
                var label = row[x];
                if (label > 0 && _solid[label])
                {
                    window[y * area.Width + x] = 1;
                    any = true;
                }
            }
        }

        return window;
    }

    private List<Rect> SpeedUnions(List<Rect> boxes, int frameHeight)
    {
        var heightTolerance = Scaled(frameHeight, _settings.HeightTolerancePx);
        var rowTolerance = Scaled(frameHeight, _settings.RowTolerancePx);
        var unions = new List<Rect>();
        for (var l = 0; l < boxes.Count; l++)
        {
            var left = boxes[l];
            var best = -1;
            var bestGap = 0;
            for (var r = 0; r < boxes.Count; r++)
            {
                if (r == l)
                    continue;
                var right = boxes[r];
                var gap = right.X - (left.X + left.Width);
                if (!(gap >= 1 && gap <= _settings.SpeedJoinGap * left.Height) || right.Width > _settings.SpeedJoinMaxLines * left.Height)
                    continue;
                if (Math.Abs(right.Height - left.Height) > Math.Max(heightTolerance, left.Height * _settings.HeightToleranceLines))
                    continue;
                if (Math.Abs(right.Y + right.Height / 2.0 - (left.Y + left.Height / 2.0)) > Math.Max(rowTolerance, _settings.RowToleranceLines * left.Height))
                    continue;
                if (best < 0 || gap < bestGap)
                {
                    best = r;
                    bestGap = gap;
                }
            }

            if (best >= 0)
            {
                var right = boxes[best];
                var top = Math.Min(left.Y, right.Y);
                var bottom = Math.Max(left.Y + left.Height, right.Y + right.Height);
                unions.Add(new Rect(left.X, top, right.X + right.Width - left.X, bottom - top));
            }
        }

        return unions;
    }
}
