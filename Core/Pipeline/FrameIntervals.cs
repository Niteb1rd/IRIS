namespace Iris.Core.Pipeline;

public sealed class FrameIntervals
{
    public const int Kept = 8;

    private readonly List<double> _gaps = new();
    private double _last = double.NaN;

    public void Reset()
    {
        _gaps.Clear();
        _last = double.NaN;
    }

    public void Add(double frameMs)
    {
        if (!double.IsNaN(_last) && frameMs > _last)
        {
            _gaps.Add(frameMs - _last);
            while (_gaps.Count > Kept)
                _gaps.RemoveAt(0);
        }

        _last = frameMs;
    }

    public double MedianMs
    {
        get
        {
            if (_gaps.Count == 0)
                return double.NaN;
            var sorted = _gaps.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }
    }
}
