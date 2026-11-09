using Iris.Core.Overlay;

namespace Iris.Core.Lookup;

public sealed record PlayerOrgs(string Handle, IReadOnlyList<OrgMembership> Orgs);

public sealed record PlayerProfile(string Handle, string? Moniker, string? CitizenRecord, bool HasRecordLabel);

public sealed record OrgInfo(
    string Sid,
    string LongName,
    int? MemberCount,
    string? Archetype,
    string? Commitment,
    bool IsExclusive,
    IReadOnlyList<string> Activities,
    IReadOnlyList<string>? UnknownActivities = null);

public sealed record OrgMembers(string Sid, IReadOnlyList<string> Handles);

public sealed record OrgMembersPage(IReadOnlyList<string> Handles, int EntryCount, int? TotalCount);
