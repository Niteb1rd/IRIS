using System.Collections.Concurrent;
using Iris.Core.Config;
using Iris.Core.Pipeline;

namespace Iris.Tests.Support;

public sealed class FakeNameSource : INameSource
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NameAnswer?>> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _refreshes = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, NameAnswer> StoredAnswers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> WantRefresh { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<string> _askLog = new();

    public IReadOnlyCollection<string> Asked => _askLog.ToArray();

    public IReadOnlyCollection<string> RefreshesStarted => _refreshes.Keys.ToArray();

    public int LastLiveWaiting { get; private set; }

    public int LiveTurnsAvailable { get; set; } = int.MaxValue;

    public void Answer(string name, NameAnswer? answer) => _asked[name].SetResult(answer);

    public void FinishRefresh(string name) => _refreshes[name].SetResult();

    public NameAnswer? Stored(string name) => StoredAnswers.TryGetValue(name, out var answer) ? answer : null;

    public Task<NameAnswer?> AskRsiAsync(string name, SocialConfig config, CancellationToken ct)
    {
        var pending = new TaskCompletionSource<NameAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _askLog.Enqueue(name);
        _asked[name] = pending;
        return pending.Task;
    }

    public bool NeedsAffiliateTopUp(string sid) => false;

    public Task TopUpAffiliateAsync(string sid, CancellationToken ct) => Task.CompletedTask;

    public bool WantsBackgroundRefresh(string name) => WantRefresh.Remove(name);

    public Task RefreshInBackgroundAsync(string name, CancellationToken ct)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshes[name] = pending;
        return pending.Task;
    }

    public void SetLiveWaiting(int count) => LastLiveWaiting = count;
}
