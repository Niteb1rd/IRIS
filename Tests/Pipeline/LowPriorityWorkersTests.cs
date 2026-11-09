using System.Collections.Concurrent;
using Iris.Core.Pipeline;
using Xunit;

namespace Iris.Tests.Pipeline;

public class LowPriorityWorkersTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void EveryItemRunsExactlyOnce(int helpers)
    {
        using var workers = new LowPriorityWorkers(helpers);
        var hits = new int[500];

        workers.Run(hits.Length, i => Interlocked.Increment(ref hits[i]));

        Assert.All(hits, count => Assert.Equal(1, count));
    }

    [Fact]
    public void AHelperAndTheCallerWorkAtTheSameTime()
    {
        using var workers = new LowPriorityWorkers(1);
        using var bothInside = new Barrier(2);
        var met = 0;

        workers.Run(2, _ =>
        {
            if (bothInside.SignalAndWait(Patience))
                Interlocked.Increment(ref met);
        });

        Assert.Equal(2, met);
    }

    [Fact]
    public void HelpersRunBelowNormalPriority()
    {
        using var workers = new LowPriorityWorkers(1);
        using var bothInside = new Barrier(2);
        var caller = Environment.CurrentManagedThreadId;
        var helperPriority = new ConcurrentBag<ThreadPriority>();

        workers.Run(2, _ =>
        {
            bothInside.SignalAndWait(Patience);
            if (Environment.CurrentManagedThreadId != caller)
                helperPriority.Add(Thread.CurrentThread.Priority);
        });

        Assert.Equal(new[] { ThreadPriority.BelowNormal }, helperPriority);
    }

    [Fact]
    public void WithNoHelpersTheCallerDoesEverything()
    {
        using var workers = new LowPriorityWorkers(0);
        var threads = new ConcurrentBag<int>();

        workers.Run(20, _ => threads.Add(Environment.CurrentManagedThreadId));

        Assert.Equal(new[] { Environment.CurrentManagedThreadId }, threads.Distinct());
    }

    [Fact]
    public void ASingleItemRunsOnTheCaller()
    {
        using var workers = new LowPriorityWorkers(3);
        var thread = 0;

        workers.Run(1, _ => thread = Environment.CurrentManagedThreadId);

        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }

    [Fact]
    public void NoItemsIsFine()
    {
        using var workers = new LowPriorityWorkers(1);

        workers.Run(0, _ => throw new InvalidOperationException("should not run"));
    }

    [Fact]
    public void RunReturnsOnlyAfterEveryItemHasFinished()
    {
        using var workers = new LowPriorityWorkers(2);
        var inside = 0;
        var finished = 0;

        workers.Run(30, _ =>
        {
            Interlocked.Increment(ref inside);
            Thread.Sleep(2);
            Interlocked.Increment(ref finished);
            Interlocked.Decrement(ref inside);
        });

        Assert.Equal(0, Volatile.Read(ref inside));
        Assert.Equal(30, Volatile.Read(ref finished));
    }

    [Fact]
    public void AFailureSurfacesAfterTheOtherItemsFinish()
    {
        using var workers = new LowPriorityWorkers(1);
        var ran = 0;

        Assert.Throws<InvalidOperationException>(() => workers.Run(50, i =>
        {
            Interlocked.Increment(ref ran);
            if (i == 10)
                throw new InvalidOperationException("boom");
        }));

        Assert.Equal(50, Volatile.Read(ref ran));
    }

    [Fact]
    public void WorkersCanBeReusedManyTimes()
    {
        using var workers = new LowPriorityWorkers(2);
        var total = 0;

        for (var round = 0; round < 500; round++)
            workers.Run(8, _ => Interlocked.Increment(ref total));

        Assert.Equal(4000, total);
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        var workers = new LowPriorityWorkers(2);

        workers.Dispose();
        workers.Dispose();
    }

    [Fact]
    public void AfterDisposalTheCallerStillCompletesWork()
    {
        var workers = new LowPriorityWorkers(2);
        workers.Dispose();
        var total = 0;

        workers.Run(5, _ => Interlocked.Increment(ref total));

        Assert.Equal(5, total);
    }
}
