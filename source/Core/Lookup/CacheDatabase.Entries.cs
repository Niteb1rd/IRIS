
using System.Data;
using System.Globalization;
using Iris.Core.Overlay;
using Microsoft.Data.Sqlite;

namespace Iris.Core.Lookup;

public sealed partial class CacheDatabase
{
    public CachedPlayer? GetPlayer(string handle)
    {
        var key = PlayerKey(handle);
        if (TryMemory<CachedPlayer>(key, out var hit))
            return hit;
        lock (_gate)
        {
            Interlocked.Increment(ref _diskReads);
            var player = ReadPlayer(handle);
            Remember(key, player);
            return player;
        }
    }

    private CachedPlayer? ReadPlayer(string handle)
    {
        string shownHandle;
        string sourceText;
        string refreshedText;
        using (var command = Command("SELECT handle, source, last_refreshed FROM players WHERE handle = $handle", ("$handle", handle)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;
            shownHandle = reader.GetString(0);
            sourceText = reader.GetString(1);
            refreshedText = reader.GetString(2);
        }

        var orgs = new List<OrgMembership>();
        using (var command = Command(
                   "SELECT sid, is_main, archetype, activities FROM player_orgs WHERE handle = $handle ORDER BY position",
                   ("$handle", handle)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string? sid = reader.IsDBNull(0) ? null : reader.GetString(0);
                bool isMain = reader.GetInt32(1) != 0;
                string? archetype = reader.IsDBNull(2) ? null : reader.GetString(2);
                var activities = SplitActivities(reader.GetString(3));
                orgs.Add(new OrgMembership(sid, isMain, archetype, activities));
            }
        }

        return new CachedPlayer(shownHandle, orgs, CacheSources.FromDb(sourceText), ParseTime(refreshedText));
    }

    public void SavePlayer(PlayerOrgs player, CacheSource source, DateTimeOffset now)
    {
        var effective = source;
        InTransaction(() =>
        {
            var existing = ScalarText("SELECT source FROM players WHERE handle = $handle", ("$handle", player.Handle));
            effective = existing is null ? source : CacheSources.Stronger(CacheSources.FromDb(existing), source);
            var time = FormatTime(now);

            Execute("DELETE FROM player_orgs WHERE handle = $handle", ("$handle", player.Handle));
            Execute("DELETE FROM players WHERE handle = $handle", ("$handle", player.Handle));
            Execute("DELETE FROM not_found WHERE kind = 'player' AND name = $handle", ("$handle", player.Handle));
            Execute(
                "INSERT INTO players (handle, source, last_refreshed) VALUES ($handle, $source, $time)",
                ("$handle", player.Handle), ("$source", CacheSources.ToDb(effective)), ("$time", time));

            using var insert = Command(
                "INSERT INTO player_orgs (handle, position, sid, is_main, archetype, activities, last_refreshed) "
                + "VALUES ($handle, $position, $sid, $main, $archetype, $activities, $time)",
                ("$handle", player.Handle), ("$position", 0), ("$sid", null), ("$main", 0), ("$archetype", null),
                ("$activities", ""), ("$time", time));
            int position = 0;
            foreach (var org in player.Orgs)
            {
                insert.Parameters["$position"].Value = position;
                insert.Parameters["$sid"].Value = (object?)org.Sid ?? DBNull.Value;
                insert.Parameters["$main"].Value = org.IsMain ? 1 : 0;
                insert.Parameters["$archetype"].Value = (object?)org.Archetype ?? DBNull.Value;
                insert.Parameters["$activities"].Value = JoinActivities(org.ActivityList);
                insert.ExecuteNonQuery();
                position++;
            }
        },
        afterCommit: () =>
        {
            var orgs = player.Orgs
                .Select(o => new OrgMembership(o.Sid, o.IsMain, o.Archetype, SplitActivities(JoinActivities(o.ActivityList))))
                .ToList();
            Remember(PlayerKey(player.Handle), new CachedPlayer(player.Handle, orgs, effective, ParseTime(FormatTime(now))));
            ForgetNote(NotFoundKind.Player, player.Handle);
        });
    }

    public CachedOrg? GetOrg(string sid)
    {
        var key = OrgKey(sid);
        if (TryMemory<CachedOrg>(key, out var hit))
            return hit;
        lock (_gate)
        {
            Interlocked.Increment(ref _diskReads);
            var org = ReadOrg(sid);
            Remember(key, org);
            return org;
        }
    }

    private CachedOrg? ReadOrg(string sid)
    {
        using var command = Command(
            "SELECT sid, long_name, member_count, archetype, commitment, is_exclusive, activities, "
            + "attrs_refreshed, members_refreshed, source, last_refreshed FROM orgs WHERE sid = $sid",
            ("$sid", sid));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new CachedOrg(
            Sid: reader.GetString(0),
            LongName: reader.IsDBNull(1) ? null : reader.GetString(1),
            MemberCount: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Archetype: reader.IsDBNull(3) ? null : reader.GetString(3),
            Commitment: reader.IsDBNull(4) ? null : reader.GetString(4),
            IsExclusive: reader.GetInt32(5) != 0,
            Activities: SplitActivities(reader.GetString(6)),
            AttributesRefreshed: reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
            MembersRefreshed: reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
            Source: CacheSources.FromDb(reader.GetString(9)),
            LastRefreshed: ParseTime(reader.GetString(10)));
    }

    public IReadOnlyList<string> GetOrgMembers(string sid)
    {
        lock (_gate)
        {
            var handles = new List<string>();
            using var command = Command("SELECT handle FROM org_members WHERE sid = $sid ORDER BY handle", ("$sid", sid));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                handles.Add(reader.GetString(0));
            return handles;
        }
    }

    public bool IsOrgMember(string sid, string handle)
    {
        lock (_gate)
            return ScalarText("SELECT 1 FROM org_members WHERE sid = $sid AND handle = $handle LIMIT 1", ("$sid", sid), ("$handle", handle)) is not null;
    }

    public void SaveOrgListEntry(string sid, string longName, IEnumerable<string> memberHandles, DateTimeOffset now)
    {
        CachedOrg? fresh = null;
        InTransaction(() =>
        {
            var time = FormatTime(now);
            var exists = ScalarText("SELECT sid FROM orgs WHERE sid = $sid", ("$sid", sid)) is not null;
            var source = CacheSources.ToDb(CacheSource.OrgList);

            if (exists)
            {
                Execute(
                    "UPDATE orgs SET long_name = $name, members_refreshed = $time, source = $source, last_refreshed = $time WHERE sid = $sid",
                    ("$name", longName), ("$time", time), ("$source", source), ("$sid", sid));
            }
            else
            {
                Execute(
                    "INSERT INTO orgs (sid, long_name, member_count, archetype, commitment, is_exclusive, activities, "
                    + "attrs_refreshed, members_refreshed, source, last_refreshed) "
                    + "VALUES ($sid, $name, NULL, NULL, NULL, 0, '', NULL, $time, $source, $time)",
                    ("$sid", sid), ("$name", longName), ("$time", time), ("$source", source));
            }

            Execute("DELETE FROM org_members WHERE sid = $sid", ("$sid", sid));

            using var insert = Command(
                "INSERT INTO org_members (sid, handle, last_refreshed) VALUES ($sid, $handle, $time)",
                ("$sid", sid), ("$handle", ""), ("$time", time));
            var handleParameter = insert.Parameters["$handle"];
            foreach (var handle in memberHandles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                handleParameter.Value = handle;
                insert.ExecuteNonQuery();
            }

            Execute("DELETE FROM not_found WHERE kind = 'org' AND name = $sid", ("$sid", sid));
            fresh = ReadOrg(sid);
        },
        afterCommit: () =>
        {
            Remember(OrgKey(sid), fresh);
            ForgetNote(NotFoundKind.Org, sid);
        });
    }

    public void PromoteOrgSource(string sid, CacheSource source)
    {
        CachedOrg? fresh = null;
        bool changed = false;
        InTransaction(() =>
        {
            var existing = ScalarText("SELECT source FROM orgs WHERE sid = $sid", ("$sid", sid));
            if (existing is null)
                return;
            var current = CacheSources.FromDb(existing);
            if (CacheSources.Stronger(current, source) == current)
                return;
            Execute("UPDATE orgs SET source = $source WHERE sid = $sid", ("$source", CacheSources.ToDb(source)), ("$sid", sid));
            fresh = ReadOrg(sid);
            changed = true;
        },
        afterCommit: () =>
        {
            if (changed)
                Remember(OrgKey(sid), fresh);
        });
    }

    public void SaveOrgAttributes(OrgInfo info, CacheSource source, DateTimeOffset now)
    {
        CachedOrg? fresh = null;
        InTransaction(() =>
        {
            var time = FormatTime(now);
            var existing = ScalarText("SELECT source FROM orgs WHERE sid = $sid", ("$sid", info.Sid));
            var effective = CacheSources.ToDb(existing is null ? source : CacheSources.Stronger(CacheSources.FromDb(existing), source));
            var activities = JoinActivities(info.Activities);

            if (existing is null)
            {
                Execute(
                    "INSERT INTO orgs (sid, long_name, member_count, archetype, commitment, is_exclusive, activities, "
                    + "attrs_refreshed, members_refreshed, source, last_refreshed) "
                    + "VALUES ($sid, $name, $count, $archetype, $commitment, $exclusive, $activities, $time, NULL, $source, $time)",
                    ("$sid", info.Sid), ("$name", info.LongName), ("$count", info.MemberCount), ("$archetype", info.Archetype),
                    ("$commitment", info.Commitment), ("$exclusive", info.IsExclusive ? 1 : 0), ("$activities", activities),
                    ("$time", time), ("$source", effective));
            }
            else
            {
                Execute(
                    "UPDATE orgs SET long_name = $name, member_count = $count, archetype = $archetype, commitment = $commitment, "
                    + "is_exclusive = $exclusive, activities = $activities, attrs_refreshed = $time, source = $source, last_refreshed = $time "
                    + "WHERE sid = $sid",
                    ("$name", info.LongName), ("$count", info.MemberCount), ("$archetype", info.Archetype),
                    ("$commitment", info.Commitment), ("$exclusive", info.IsExclusive ? 1 : 0), ("$activities", activities),
                    ("$time", time), ("$source", effective), ("$sid", info.Sid));
            }

            Execute("DELETE FROM not_found WHERE kind = 'org' AND name = $sid", ("$sid", info.Sid));
            fresh = ReadOrg(info.Sid);
        },
        afterCommit: () =>
        {
            Remember(OrgKey(info.Sid), fresh);
            ForgetNote(NotFoundKind.Org, info.Sid);
        });
    }
}
