using Iris.Core.Overlay;

namespace Iris.Core.Mining;

public sealed class LockGeometry
{
    private readonly MiningSettings _settings;
    private readonly List<(PixelRect Box, double RowTop, double DigitHeight)> _reads = new();

    public LockGeometry(MiningSettings settings) => _settings = settings;

    public void Clear() => _reads.Clear();

    public (PixelRect Box, double RowTop, double DigitHeight)? Add(PixelRect box, double rowTop, double digitHeight, MiningState locked)
    {
        _reads.Add((box, rowTop, digitHeight));
        if (_reads.Count > _settings.GeometryReads)
            _reads.RemoveAt(0);
        if (_reads.Count < _settings.GeometryMinReads)
            return null;
        var median = new PixelRect(Median(r => r.Box.X), Median(r => r.Box.Y), Median(r => r.Box.Width), Median(r => r.Box.Height));
        var row = Median(r => r.RowTop);
        var height = Median(r => r.DigitHeight);
        if (locked.DigitHeight > 0
            && Math.Abs(height - locked.DigitHeight) < Math.Max(_settings.LabelSizeChangePx, _settings.LabelSizeChangeShare * locked.DigitHeight))
            height = locked.DigitHeight;
        var move = _settings.GeometryChangePx;
        var differs = Math.Abs(median.X - locked.SignalBox.X) >= move || Math.Abs(median.Y - locked.SignalBox.Y) >= move
            || Math.Abs(median.Right - locked.SignalBox.Right) >= move || Math.Abs(median.Y + median.Height - (locked.SignalBox.Y + locked.SignalBox.Height)) >= move
            || Math.Abs(row - locked.RowTop) >= move || Math.Abs(height - locked.DigitHeight) >= move;
        return differs ? (median, row, height) : null;
    }

    private double Median(Func<(PixelRect Box, double RowTop, double DigitHeight), double> value)
    {
        var sorted = _reads.Select(value).OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
