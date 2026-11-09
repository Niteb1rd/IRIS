using System.Runtime.ExceptionServices;

namespace Iris.Core.Pipeline;

public sealed class LowPriorityWorkers : IDisposable
{
    private sealed class Job
    {
        public Job(Action<int> work, int count)
        {
            Work = work;
            Count = count;
        }

        public Action<int> Work { get; }

        public int Count { get; }

        public int Next = -1;

        public int HelpersInside;

        public Exception? Failure;

        public bool IsExhausted => Volatile.Read(ref Next) >= Count - 1;
    }

    private readonly object _gate = new();
    private readonly List<Thread> _helpers = new();
    private Job? _job;
    private bool _stopping;

    public LowPriorityWorkers(int helpers, string name = "IRIS scan helper")
    {
        for (var i = 0; i < helpers; i++)
        {
            var thread = new Thread(HelperLoop) { Name = name, IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _helpers.Add(thread);
            thread.Start();
        }
    }

    public int Threads => _helpers.Count + 1;

    public void Run(int count, Action<int> work)
    {
        if (count <= 0)
            return;

        var job = new Job(work, count);
        var shared = false;
        if (count > 1)
        {
            lock (_gate)
            {
                if (!_stopping && _helpers.Count > 0)
                {
                    _job = job;
                    shared = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        Drain(job);
        if (shared)
        {
            lock (_gate)
            {
                _job = null;
                while (job.HelpersInside > 0)
                    Monitor.Wait(_gate);
            }
        }

        if (job.Failure is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void HelperLoop()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (!_stopping && (_job is null || _job.IsExhausted))
                    Monitor.Wait(_gate);
                if (_stopping)
                    return;
                job = _job!;
                job.HelpersInside++;
            }

            Drain(job);
            lock (_gate)
            {
                job.HelpersInside--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static void Drain(Job job)
    {
        while (true)
        {
            var index = Interlocked.Increment(ref job.Next);
            if (index >= job.Count)
                return;
            try
            {
                job.Work(index);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.CompareExchange(ref job.Failure, ex, null);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        foreach (var helper in _helpers)
            helper.Join();
    }
}
