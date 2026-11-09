using Iris.Core.Detection;
using Xunit;

namespace Iris.Tests.Detection;

public class LayerCheckTests
{
    [Fact]
    public void VerdictsNotedFromManyThreadsAreAllKept()
    {
        var check = new LayerCheck();

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => check.Note(i % 2 == 0));

        Assert.Equal(200, check.TakeVerdicts().Count);
    }

    [Fact]
    public void TheVerdictCapHoldsUnderConcurrency()
    {
        var check = new LayerCheck();

        Parallel.For(0, LayerCheck.MaxVerdicts * 4, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => check.Note(true));

        Assert.Equal(LayerCheck.MaxVerdicts, check.TakeVerdicts().Count);
    }

    [Fact]
    public void TakingVerdictsEmptiesTheList()
    {
        var check = new LayerCheck();
        check.Note(true);

        check.TakeVerdicts();

        Assert.Empty(check.TakeVerdicts());
    }
}
