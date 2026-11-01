using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class FrameRegion
{
    public FrameRegion(CaptureRequest request, Rect region, Mat image)
    {
        Requester = request.Requester;
        Kind = request.Kind;
        MarkerId = request.MarkerId;
        Tag = request.Tag;
        OneTime = request.OneTime;
        Region = region;
        Image = image;
    }

    public PipelineGroup Requester { get; }

    public RequestKind Kind { get; }

    public int MarkerId { get; }

    public long Tag { get; }

    public bool OneTime { get; }

    public Rect Region { get; }

    public Mat Image { get; }
}

public enum ScanReadState
{
    Ready,
    Pending,
    Failed,
}

public sealed class CapturedFrame
{
    private readonly FrameHub _hub;
    private readonly List<FrameRegion> _regions = new();
    private readonly List<long> _requestIds = new();
    private readonly List<Mat> _patchBuffers = new();
    private Mat? _scanBuffer;
    private int _refs;
    private int _oneTimeGroups;
    private readonly ManualResetEventSlim _scanDone = new(true);
    private int _scanState = (int)ScanReadState.Ready;

    internal CapturedFrame(FrameHub hub) => _hub = hub;

    public double FrameMs { get; private set; }

    public double ArrivalMs { get; private set; }

    public double PublishedMs { get; private set; } = double.NaN;

    public int WindowWidth { get; private set; }

    public int WindowHeight { get; private set; }

    public long Sequence { get; internal set; }

    public IReadOnlyList<FrameRegion> Regions => _regions;

    public IReadOnlyList<long> RequestIds => _requestIds;

    public void Begin(double frameMs, double arrivalMs, int windowWidth, int windowHeight)
    {
        FrameMs = frameMs;
        ArrivalMs = arrivalMs;
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        _regions.Clear();
        _requestIds.Clear();
        _oneTimeGroups = 0;
        PublishedMs = double.NaN;
        Volatile.Write(ref _scanState, (int)ScanReadState.Ready);
        _scanDone.Set();
    }

    public void MarkPublished(double nowMs) => PublishedMs = nowMs;

    public ScanReadState ScanState => (ScanReadState)Volatile.Read(ref _scanState);

    public void MarkScanPending()
    {
        _scanDone.Reset();
        Volatile.Write(ref _scanState, (int)ScanReadState.Pending);
    }

    public void CompleteScan(bool ok)
    {
        Volatile.Write(ref _scanState, (int)(ok ? ScanReadState.Ready : ScanReadState.Failed));
        _scanDone.Set();
    }

    public bool WaitForScan(int timeoutMs)
    {
        if (ScanState == ScanReadState.Pending)
            _scanDone.Wait(timeoutMs);
        return ScanState == ScanReadState.Ready;
    }

    public Mat ScanBuffer => _scanBuffer ??= new Mat();

    public Mat PatchBuffer(int index)
    {
        while (_patchBuffers.Count <= index)
            _patchBuffers.Add(new Mat());
        return _patchBuffers[index];
    }

    public void Add(CaptureRequest request, Rect region, Mat image)
    {
        _regions.Add(new FrameRegion(request, region, image));
        _requestIds.Add(request.Id);
        if (request.OneTime)
            _oneTimeGroups |= 1 << (int)request.Requester;
    }

    public bool HasOneTimeFor(PipelineGroup group) => (_oneTimeGroups & (1 << (int)group)) != 0;

    public FrameRegion? Find(PipelineGroup requester, RequestKind kind, int markerId)
    {
        foreach (var region in _regions)
            if (region.Requester == requester && region.Kind == kind && region.MarkerId == markerId)
                return region;
        return null;
    }

    public bool TryRetain()
    {
        while (true)
        {
            var count = Volatile.Read(ref _refs);
            if (count <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _refs, count + 1, count) == count)
                return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
            _hub.Return(this);
    }

    internal void StartRefs() => Volatile.Write(ref _refs, 1);

    internal void DisposeBuffers()
    {
        _scanBuffer?.Dispose();
        _scanBuffer = null;
        foreach (var buffer in _patchBuffers)
            buffer.Dispose();
        _patchBuffers.Clear();
        _regions.Clear();
        _requestIds.Clear();
    }
}

public sealed class FrameHub : IDisposable
{
    private readonly object _gate = new();
    private readonly Stack<CapturedFrame> _free = new();
    private readonly List<CapturedFrame> _all = new();
    private CapturedFrame? _newest;
    private CapturedFrame? _newestReads;
    private CapturedFrame? _newestScan;
    private long _sequence;
    private bool _disposed;

    public FrameHub(int poolSize = 6)
    {
        for (var i = 0; i < Math.Max(2, poolSize); i++)
        {
            var frame = new CapturedFrame(this);
            _all.Add(frame);
            _free.Push(frame);
        }
    }

    public int FreeCount
    {
        get
        {
            lock (_gate)
                return _free.Count;
        }
    }

    public CapturedFrame? Rent()
    {
        CapturedFrame frame;
        lock (_gate)
        {
            if (_disposed || _free.Count == 0)
                return null;
            frame = _free.Pop();
        }

        frame.StartRefs();
        return frame;
    }

    internal void Return(CapturedFrame frame)
    {
        lock (_gate)
        {
            if (!_disposed)
                _free.Push(frame);
        }
    }

    public long Publish(CapturedFrame frame)
    {
        CapturedFrame? droppedNewest;
        CapturedFrame? droppedReads = null;
        CapturedFrame? droppedScan = null;
        long sequence;
        lock (_gate)
        {
            sequence = ++_sequence;
            frame.Sequence = sequence;
            frame.TryRetain();
            droppedNewest = _newest;
            _newest = frame;
            if (frame.HasOneTimeFor(PipelineGroup.Reads))
            {
                frame.TryRetain();
                droppedReads = _newestReads;
                _newestReads = frame;
            }

            if (frame.HasOneTimeFor(PipelineGroup.Scan))
            {
                frame.TryRetain();
                droppedScan = _newestScan;
                _newestScan = frame;
            }
        }

        droppedNewest?.Release();
        droppedReads?.Release();
        droppedScan?.Release();
        frame.Release();
        return sequence;
    }

    public CapturedFrame? TakeNewest()
    {
        lock (_gate)
            return _newest is { } frame && frame.TryRetain() ? frame : null;
    }

    public CapturedFrame? TakeNewestWith(PipelineGroup group)
    {
        lock (_gate)
        {
            var frame = group switch
            {
                PipelineGroup.Reads => _newestReads,
                PipelineGroup.Scan => _newestScan,
                _ => _newest,
            };
            return frame is not null && frame.TryRetain() ? frame : null;
        }
    }

    public void Clear()
    {
        CapturedFrame? a;
        CapturedFrame? b;
        CapturedFrame? c;
        lock (_gate)
        {
            a = _newest;
            b = _newestReads;
            c = _newestScan;
            _newest = _newestReads = _newestScan = null;
        }

        a?.Release();
        b?.Release();
        c?.Release();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _free.Clear();
            _newest = _newestReads = _newestScan = null;
        }

        foreach (var frame in _all)
            frame.DisposeBuffers();
    }
}
