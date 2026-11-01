using Iris.Core.Overlay;

namespace Iris.Core.Lookup;

public enum CacheSource
{
    Hotkey = 0,

    OrgListMember = 1,

    PlayerList = 2,

    OrgList = 3,
}

public static class CacheSources
{
    public static string ToDb(CacheSource source) => source switch
    {
        CacheSource.Hotkey => "hotkey",
        CacheSource.OrgListMember => "org_list_member",
        CacheSource.PlayerList => "player_list",
        CacheSource.OrgList => "org_list",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static CacheSource FromDb(string text) => text switch
    {
        "hotkey" => CacheSource.Hotkey,
        "org_list_member" => CacheSource.OrgListMember,
        "player_list" => CacheSource.PlayerList,
        "org_list" => CacheSource.OrgList,
        _ => throw new InvalidDataException($"unknown cache source '{text}' in the cache file"),
    };

    public static CacheSource Stronger(CacheSource a, CacheSource b) => (int)a >= (int)b ? a : b;
}

public enum NotFoundKind
{
    Player,
    Org,
}

public sealed record NotFoundNote(DateTimeOffset RecordedAt, int Count);

public sealed record CachedPlayer(
    string Handle,
    IReadOnlyList<OrgMembership> Orgs,
    CacheSource Source,
    DateTimeOffset LastRefreshed);

public sealed record CachedOrg(
    string Sid,
    string? LongName,
    int? MemberCount,
    string? Archetype,
    string? Commitment,
    bool IsExclusive,
    IReadOnlyList<string> Activities,
    DateTimeOffset? AttributesRefreshed,
    DateTimeOffset? MembersRefreshed,
    CacheSource Source,
    DateTimeOffset LastRefreshed)
{
    public bool HasAttributes => AttributesRefreshed is not null;
}
