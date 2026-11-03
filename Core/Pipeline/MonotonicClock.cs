using System.Diagnostics;

namespace Iris.Core.Pipeline;

public interface IMonotonicClock
{
    double NowMs { get; }
}

public sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch _watch;

    public StopwatchClock()
    {
        OriginTimestamp = Stopwatch.GetTimestamp();
        _watch = Stopwatch.StartNew();
    }

    public static StopwatchClock Shared { get; } = new();

    public double NowMs => _watch.Elapsed.TotalMilliseconds;

    public long OriginTimestamp { get; }
}
