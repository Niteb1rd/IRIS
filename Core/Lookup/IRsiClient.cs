namespace Iris.Core.Lookup;

public enum RsiLane
{
    Background,

    Live,
}

public interface IRsiClient
{
    int LiveTurnsAvailable => int.MaxValue;

    Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default, RsiLane lane = RsiLane.Background);

    Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default, RsiLane lane = RsiLane.Background);

    Task<RsiResult<PlayerProfile>> GetPlayerProfileAsync(string handle, CancellationToken ct = default, RsiLane lane = RsiLane.Background);

    Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default);
}
