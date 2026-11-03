namespace Iris.Core.Pipeline;

public sealed record FrameBreakdown(
    IReadOnlyList<double> CaptureHandoverMs, IReadOnlyList<double> CopyMs, IReadOnlyList<double> TrackingHandOverMs, IReadOnlyList<double> WorkMs, IReadOnlyList<double> DeliveryMs,
    IReadOnlyList<double> ApplyMs, IReadOnlyList<double> RenderPickupMs);

public sealed record IndexCounts(IReadOnlyList<int> Rejected, IReadOnlyList<int> ExpiredUnfulfilled, IReadOnlyList<double> LatenciesMs, FrameBreakdown? Breakdown = null)
{
    public int RejectedBy(PipelineGroup group) => Rejected[(int)group];

    public int ExpiredBy(PipelineGroup group) => ExpiredUnfulfilled[(int)group];
}

public sealed class IndexCounters
{
    public const int TextsPerGroup = 5;
    public const int MaxSamples = 20000;

    private readonly object _gate = new();
    private readonly int[] _rejected = new int[PipelineStats.GroupCount];
    private readonly int[] _expired = new int[PipelineStats.GroupCount];
    private readonly int[] _textsKept = new int[PipelineStats.GroupCount];
    private readonly List<string> _texts = new();
    private readonly List<double> _latencies = new();
    private readonly List<double> _handover = new();
    private readonly List<double> _copy = new();
    private readonly List<double> _trackingHandOver = new();
    private readonly List<double> _work = new();
    private readonly List<double> _delivery = new();
    private readonly List<double> _apply = new();
    private readonly List<double> _pickup = new();

    public IndexCounters(bool enabled = true) => Enabled = enabled;

    public bool Enabled { get; }

    public void Reject(PipelineGroup group, string text)
    {
        if (!Enabled)
            return;
        lock (_gate)
        {
            _rejected[(int)group]++;
            if (_textsKept[(int)group] < TextsPerGroup)
            {
                _textsKept[(int)group]++;
                _texts.Add(text);
            }
        }
    }

    public void Expired(PipelineGroup group)
    {
        if (!Enabled)
            return;
        lock (_gate)
            _expired[(int)group]++;
    }

    public void AddLatency(double milliseconds)
    {
        if (!Enabled)
            return;
        if (!(milliseconds >= 0.0))
            return;
        lock (_gate)
            _latencies.Add(milliseconds);
    }

    public void AddBreakdown(double handoverMs, double copyMs, double trackingHandOverMs, double workMs, double deliveryMs)
    {
        if (!Enabled)
            return;
        if (!double.IsFinite(handoverMs) || !double.IsFinite(copyMs) || !double.IsFinite(trackingHandOverMs) || !double.IsFinite(workMs) || !double.IsFinite(deliveryMs))
            return;
        lock (_gate)
        {
            Keep(_handover, handoverMs);
            Keep(_copy, copyMs);
            Keep(_trackingHandOver, trackingHandOverMs);
            Keep(_work, workMs);
            Keep(_delivery, deliveryMs);
        }
    }

    public void AddApply(double milliseconds)
    {
        if (!Enabled)
            return;
        if (!double.IsFinite(milliseconds))
            return;
        lock (_gate)
            Keep(_apply, milliseconds);
    }

    public void AddRenderPickup(double milliseconds)
    {
        if (!Enabled)
            return;
        if (!double.IsFinite(milliseconds))
            return;
        lock (_gate)
            Keep(_pickup, milliseconds);
    }

    private static void Keep(List<double> list, double value)
    {
        if (list.Count < MaxSamples)
            list.Add(value);
    }

    public int Rejected(PipelineGroup group)
    {
        lock (_gate)
            return _rejected[(int)group];
    }

    public int ExpiredUnfulfilled(PipelineGroup group)
    {
        lock (_gate)
            return _expired[(int)group];
    }

    public IReadOnlyList<string> TakeTexts()
    {
        lock (_gate)
        {
            var taken = _texts.ToArray();
            _texts.Clear();
            return taken;
        }
    }

    public IndexCounts Take()
    {
        lock (_gate)
        {
            var breakdown = new FrameBreakdown(_handover.ToArray(), _copy.ToArray(), _trackingHandOver.ToArray(), _work.ToArray(), _delivery.ToArray(), _apply.ToArray(), _pickup.ToArray());
            var counts = new IndexCounts((int[])_rejected.Clone(), (int[])_expired.Clone(), _latencies.ToArray(), breakdown);
            Array.Clear(_rejected);
            Array.Clear(_expired);
            _latencies.Clear();
            _handover.Clear();
            _copy.Clear();
            _trackingHandOver.Clear();
            _work.Clear();
            _delivery.Clear();
            _apply.Clear();
            _pickup.Clear();
            return counts;
        }
    }
}
