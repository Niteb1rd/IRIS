using System.Collections.Concurrent;
using Iris.Core.Lookup;

namespace Iris.Tests.Support;

public sealed class FakeRsi : IRsiClient
{
    public ConcurrentQueue<(string Kind, string Name, RsiLane Lane)> Calls { get; } = new();

    public Func<string, RsiResult<PlayerOrgs>> Player { get; set; } = handle => RsiResult<PlayerOrgs>.Found(new PlayerOrgs(handle, Array.Empty<Iris.Core.Overlay.OrgMembership>()));

    public Func<string, Task<RsiResult<OrgInfo>>> Org { get; set; } = sid => Task.FromResult(RsiResult<OrgInfo>.NotFound());

    public int Count(string kind) => Calls.Count(c => c.Kind == kind);

    public Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default, RsiLane lane = RsiLane.Background)
    {
        Calls.Enqueue(("player", handle, lane));
        return Task.FromResult(Player(handle));
    }

    public Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default, RsiLane lane = RsiLane.Background)
    {
        Calls.Enqueue(("org", sid, lane));
        return Org(sid);
    }

    public Task<RsiResult<PlayerProfile>> GetPlayerProfileAsync(string handle, CancellationToken ct = default, RsiLane lane = RsiLane.Background)
    {
        Calls.Enqueue(("profile", handle, lane));
        return Task.FromResult(RsiResult<PlayerProfile>.Found(new PlayerProfile(handle, null, null, true)));
    }

    public Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default)
    {
        Calls.Enqueue(("members", sid, RsiLane.Background));
        return Task.FromResult(RsiResult<OrgMembers>.Found(new OrgMembers(sid, Array.Empty<string>())));
    }
}
