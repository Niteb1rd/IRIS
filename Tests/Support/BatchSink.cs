using Iris.Core.Pipeline;

namespace Iris.Tests.Support;

public sealed class BatchSink : IUpdateSink
{
    public List<IReadOnlyList<IndexUpdate>> Batches { get; } = new();

    public IEnumerable<IndexUpdate> All => Batches.SelectMany(b => b);

    public void Submit(IReadOnlyList<IndexUpdate> updates) => Batches.Add(updates.ToArray());
}
