namespace Iris.Core.Pipeline;

public sealed class IndexSubscription : IDisposable
{
    private readonly MarkerIndex _owner;
    private readonly AutoResetEvent _event = new(false);

    internal IndexSubscription(MarkerIndex owner, IndexTrigger mask)
    {
        _owner = owner;
        Mask = mask;
    }

    public IndexTrigger Mask { get; }

    public WaitHandle Handle => _event;

    public bool Wait(int timeoutMs) => _event.WaitOne(timeoutMs);

    internal void Signal()
    {
        try
        {
            _event.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _owner.Unsubscribe(this);
        _event.Dispose();
    }
}
