namespace Iris.Core.Lookup;

public interface IRsiClient
{
    Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default);

    Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default);

    Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default);
}
