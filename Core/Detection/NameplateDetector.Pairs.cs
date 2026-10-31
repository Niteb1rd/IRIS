
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Iris.Core.Detection;

public sealed partial class NameplateDetector
{
    private List<(int Top, int Bottom)> PairLines(List<Rect> boxes, int frameHeight, int realCount)
    {
        var heightTolerance = Scaled(frameHeight, _settings.HeightTolerancePx);
        var centerTolerance = Scaled(frameHeight, _settings.CenterTolerance);
        var leftTolerance = Scaled(frameHeight, _settings.LeftTolerance);
        var leftWidthDifference = Scaled(frameHeight, _settings.LeftWidthDifference);
        var pairs = new List<(int, int)>();
        var byTop = new LinesByTop(boxes, null);
        var below = new List<int>();
        for (var t = 0; t < realCount; t++)
        {
            var top = boxes[t];
            byTop.Below(top, below);
            foreach (var b in below)
            {
                if (b == t)
                    continue;
                if (PairOk(top, boxes[b], heightTolerance, centerTolerance, leftTolerance, leftWidthDifference))
                    pairs.Add((t, b));
            }
        }

        return pairs;
    }

    private bool PairOk(Rect top, Rect bottom, int heightTolerance, int centerTolerance, int leftTolerance, int leftWidthDifference)
    {
        var gap = bottom.Y - (top.Y + top.Height);
        if (!(gap >= 1 && gap <= top.Height * NameplateDetectorSettings.PairGapMaxLines))
            return false;
        if (Math.Abs(bottom.Height - top.Height) > Math.Max(heightTolerance, top.Height * _settings.HeightToleranceLines))
            return false;
        var topCenter = top.X + top.Width / 2.0;
        var bottomCenter = bottom.X + bottom.Width / 2.0;
        if (Math.Abs(bottomCenter - topCenter) > centerTolerance)
            return false;
        if (Math.Abs(top.X - bottom.X) <= leftTolerance && Math.Abs(top.Width - bottom.Width) >= leftWidthDifference)
            return false;
        return true;
    }

    private List<(Rect Narrow, List<Rect> Bottoms)> PromoteNarrow(List<Rect> narrow, List<Rect> plain, List<Rect> unions, int frameHeight, Func<List<Rect>> rowPool)
    {
        var heightTolerance = Scaled(frameHeight, _settings.HeightTolerancePx);
        var centerTolerance = Scaled(frameHeight, _settings.CenterTolerance);
        var leftTolerance = Scaled(frameHeight, _settings.LeftTolerance);
        var leftWidthDifference = Scaled(frameHeight, _settings.LeftWidthDifference);
        var plainSet = new HashSet<Rect>(plain);
        var below = new LinesByTop(plain, unions);
        var near = new List<int>();
        var promoted = new List<(Rect Narrow, List<Rect> Bottoms)>();
        foreach (var shape in narrow)
        {
            if (plainSet.Contains(shape))
                continue;
            below.Below(shape, near);
            List<Rect>? bottoms = null;
            foreach (var i in near)
            {
                var bottom = i < plain.Count ? plain[i] : unions[i - plain.Count];
                if (!PairOk(shape, bottom, heightTolerance, centerTolerance, leftTolerance, leftWidthDifference))
                    continue;
                if (StackRows(rowPool(), shape, bottom, frameHeight).Rows.Count > 1)
                    (bottoms ??= new List<Rect>()).Add(bottom);
            }

            if (bottoms is not null && NarrowFillOk(shape))
                promoted.Add((shape, bottoms));
        }

        return promoted;
    }

    private Dictionary<(Rect Top, Rect Bottom), (List<Rect> Rows, List<Rect> Blockers)> FindStacks(
        List<Rect> boxes, List<(int Top, int Bottom)> pairs, Func<List<Rect>> rowPool, int frameHeight)
    {
        var stacks = new Dictionary<(Rect, Rect), (List<Rect>, List<Rect>)>();
        if (pairs.Count == 0)
            return stacks;
        var pool = rowPool();
        foreach (var (t, b) in pairs)
        {
            var (rows, blockers) = StackRows(pool, boxes[t], boxes[b], frameHeight);
            if (rows.Count > 1 || blockers.Count > 0)
                stacks[(boxes[t], boxes[b])] = (rows, blockers);
        }

        return stacks;
    }

    private List<Rect> RowCandidates(List<Rect> boxes, int realCount, int frameHeight)
    {
        var s2 = Scaled(frameHeight, _settings.RowJoinTopPx);
        var s3 = Scaled(frameHeight, _settings.RowJoinHeightPx);
        var s4 = Scaled(frameHeight, _settings.RowJoinGapPx);
        var parent = new int[realCount];
        for (var i = 0; i < realCount; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (var i = 0; i < realCount; i++)
        {
            var a = boxes[i];
            for (var j = i + 1; j < realCount; j++)
            {
                var c = boxes[j];
                if (Math.Abs(a.Y - c.Y) <= s2 && Math.Abs(a.Height - c.Height) <= s3 && c.X <= a.X + a.Width + s4 && a.X <= c.X + c.Width + s4)
                    parent[Find(j)] = Find(i);
            }
        }

        var order = new List<int>();
        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < realCount; i++)
        {
            var root = Find(i);
            if (!members.TryGetValue(root, out var group))
            {
                members[root] = group = new List<int>();
                order.Add(root);
            }

            group.Add(i);
        }

        var pool = new List<Rect>(order.Count);
        foreach (var root in order)
        {
            var group = members[root];
            if (group.Count == 1)
            {
                pool.Add(boxes[group[0]]);
                continue;
            }

            var x0 = group.Min(i => boxes[i].X);
            var y0 = group.Min(i => boxes[i].Y);
            var x1 = group.Max(i => boxes[i].X + boxes[i].Width);
            var y1 = group.Max(i => boxes[i].Y + boxes[i].Height);
            pool.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
        }

        return pool;
    }

    private (List<Rect> Rows, List<Rect> Blockers) StackRows(List<Rect> pool, Rect top, Rect anchor, int frameHeight)
    {
        var heightTolerance = Scaled(frameHeight, _settings.HeightTolerancePx);
        var centerTolerance = Scaled(frameHeight, _settings.CenterTolerance);
        var leftTolerance = Scaled(frameHeight, _settings.LeftTolerance);
        var leftWidthDifference = Scaled(frameHeight, _settings.LeftWidthDifference);
        var anchorCenter = anchor.X + anchor.Width / 2.0;
        var chain = new List<Rect> { top };
        var current = top;
        while (true)
        {
            var joinable = new List<(int Gap, Rect Row)>();
            var broken = new List<Rect>();
            foreach (var row in pool)
            {
                if (row == anchor || chain.Contains(row))
                    continue;
                var gap = current.Y - (row.Y + row.Height);
                if (!(gap >= 1 && gap <= _settings.RowGapMax * current.Height))
                    continue;
                var pitch = current.Y - row.Y;
                if (!(_settings.RowPitchMin * current.Height <= pitch && pitch <= _settings.RowPitchMax * current.Height))
                    continue;
                if (Math.Abs(row.Height - current.Height) > Math.Max(heightTolerance, current.Height * _settings.HeightToleranceLines))
                    continue;
                if (row.Width < _settings.RowMinCaps * row.Height)
                    continue;
                if (row.X >= current.X + current.Width || row.X + row.Width <= current.X)
                    continue;
                if (Math.Abs(row.X - current.X) <= leftTolerance && Math.Abs(row.Width - current.Width) >= leftWidthDifference)
                    continue;
                if (Math.Abs(row.X + row.Width / 2.0 - anchorCenter) > centerTolerance)
                {
                    broken.Add(row);
                    continue;
                }

                joinable.Add((gap, row));
            }

            if (joinable.Count == 0)
            {
                chain.Reverse();
                return (chain, broken);
            }

            var best = joinable[0];
            foreach (var candidate in joinable)
                if (CompareJoinable(candidate, best) < 0)
                    best = candidate;
            if (chain.Count >= _settings.MaxRows)
            {
                chain.Reverse();
                broken.Add(best.Row);
                return (chain, broken);
            }

            chain.Add(best.Row);
            current = best.Row;
        }
    }

    private static int CompareJoinable((int Gap, Rect Row) a, (int Gap, Rect Row) b)
    {
        var byGap = a.Gap.CompareTo(b.Gap);
        if (byGap != 0)
            return byGap;
        var byX = a.Row.X.CompareTo(b.Row.X);
        if (byX != 0)
            return byX;
        var byY = a.Row.Y.CompareTo(b.Row.Y);
        if (byY != 0)
            return byY;
        var byWidth = a.Row.Width.CompareTo(b.Row.Width);
        return byWidth != 0 ? byWidth : a.Row.Height.CompareTo(b.Row.Height);
    }

    private (List<(int Top, int Bottom)> Strict, List<(int Top, int Bottom)> Loose, List<CrowdPair> Crowd) SortPairs(
        List<Rect> boxes, Func<int, bool> clear, List<(int Top, int Bottom)> all, int realCount)
    {
        var strict = new List<(int, int)>();
        var loose = new List<(int, int)>();
        var crowd = new List<CrowdPair>();
        var pairsOfLine = new Dictionary<int, List<(int Top, int Bottom)>>();
        foreach (var pair in all)
        {
            foreach (var line in new[] { pair.Top, pair.Bottom })
            {
                if (!pairsOfLine.TryGetValue(line, out var list))
                    pairsOfLine[line] = list = new List<(int, int)>();
                list.Add(pair);
            }
        }

        var neighbourCache = new Dictionary<int, List<int>?>();
        foreach (var (t, b) in all)
        {
            if (clear(t) && clear(b))
            {
                strict.Add((t, b));
                continue;
            }

            if (_settings.CrowdException)
            {
                var need = new HashSet<int>();
                var blocked = false;
                foreach (var line in new[] { t, b })
                {
                    if (!neighbourCache.TryGetValue(line, out var near))
                        neighbourCache[line] = near = clear(line) ? new List<int>() : Neighbours(boxes, line, realCount);
                    if (near is null)
                    {
                        blocked = true;
                        break;
                    }

                    need.UnionWith(near);
                }

                if (!blocked)
                {
                    need.Remove(t);
                    need.Remove(b);
                    if (need.Count > 0 && need.All(k => pairsOfLine.TryGetValue(k, out var others) && others.Any(o => o.Top != t && o.Top != b && o.Bottom != t && o.Bottom != b)))
                    {
                        crowd.Add(new CrowdPair(t, b, need.OrderBy(k => k).ToList()));
                        continue;
                    }
                }
            }

            loose.Add((t, b));
        }

        return (strict, loose, crowd);
    }

    private List<int>? Neighbours(List<Rect> boxes, int index, int realCount)
    {
        var box = boxes[index];
        var reach = (int)Math.Ceiling(_settings.ClearLines * box.Height);
        var rowStart = Math.Max(0, box.Y);
        var rowEnd = Math.Min(_maskLabels.Rows, box.Y + box.Height);
        var found = new SortedSet<int>();
        if (rowEnd <= rowStart)
            return new List<int>();

        var sides = new[]
        {
            (Start: Math.Max(0, box.X - reach), End: box.X),
            (Start: box.X + box.Width, End: Math.Min(_maskLabels.Cols, box.X + box.Width + reach)),
        };
        foreach (var (start, end) in sides)
        {
            if (end <= start)
                continue;
            var windowWidth = end - start;
            var window = ObstacleWindow(new Rect(start, rowStart, windowWidth, rowEnd - rowStart), out var anyInk);
            if (!anyInk)
                continue;
            var rest = (byte[])window.Clone();
            for (var k = 0; k < realCount; k++)
            {
                if (k == index)
                    continue;
                var other = boxes[k];
                if (other.X >= end || other.X + other.Width <= start || other.Y >= rowEnd || other.Y + other.Height <= rowStart)
                    continue;
                var x0 = Math.Max(start, other.X) - start;
                var x1 = Math.Min(end, other.X + other.Width) - start;
                var y0 = Math.Max(rowStart, other.Y) - rowStart;
                var y1 = Math.Min(rowEnd, other.Y + other.Height) - rowStart;
                var touches = false;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var at = y * windowWidth + x;
                        if (window[at] != 0)
                            touches = true;
                        rest[at] = 0;
                    }
                }

                if (touches)
                    found.Add(k);
            }

            if (Array.Exists(rest, v => v != 0))
                return null;
        }

        return found.ToList();
    }
}
