
using System.Data;
using System.Globalization;
using Iris.Core.Overlay;
using Microsoft.Data.Sqlite;

namespace Iris.Core.Lookup;

public sealed partial class CacheDatabase
{
    public void SaveNotFound(NotFoundKind kind, string name, CacheSource source, DateTimeOffset now)
    {
        int count = 1;
        InTransaction(() =>
        {
            var before = ScalarText("SELECT miss_count FROM not_found WHERE kind = $kind AND name = $name", ("$kind", KindText(kind)), ("$name", name));
            if (before is not null && int.TryParse(before, NumberStyles.Integer, CultureInfo.InvariantCulture, out var earlier) && earlier >= 1)
                count = earlier == int.MaxValue ? earlier : earlier + 1;

            if (kind == NotFoundKind.Player)
            {
                Execute("DELETE FROM player_orgs WHERE handle = $name", ("$name", name));
                Execute("DELETE FROM players WHERE handle = $name", ("$name", name));
            }
            else
            {
                Execute("DELETE FROM org_members WHERE sid = $name", ("$name", name));
                Execute("DELETE FROM orgs WHERE sid = $name", ("$name", name));
            }

            Execute("DELETE FROM not_found WHERE kind = $kind AND name = $name", ("$kind", KindText(kind)), ("$name", name));
            Execute(
                "INSERT INTO not_found (kind, name, source, last_refreshed, miss_count) VALUES ($kind, $name, $source, $time, $count)",
                ("$kind", KindText(kind)), ("$name", name), ("$source", CacheSources.ToDb(source)), ("$time", FormatTime(now)), ("$count", count));
        },
        afterCommit: () =>
        {
            Remember(kind == NotFoundKind.Player ? PlayerKey(name) : OrgKey(name), null);
            Remember(NotFoundKey(kind, name), new NotFoundNote(ParseTime(FormatTime(now)), count));
        });
    }

    public NotFoundNote? GetNotFoundNote(NotFoundKind kind, string name)
    {
        var key = NotFoundKey(kind, name);
        if (TryMemory<NotFoundNote>(key, out var hit))
            return hit;
        lock (_gate)
        {
            Interlocked.Increment(ref _diskReads);
            NotFoundNote? note = null;
            using (var command = Command(
                       "SELECT last_refreshed, miss_count FROM not_found WHERE kind = $kind AND name = $name",
                       ("$kind", KindText(kind)), ("$name", name)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                    note = new NotFoundNote(ParseTime(reader.GetString(0)), Math.Max(1, reader.GetInt32(1)));
            }

            Remember(key, note);
            return note;
        }
    }

    public DateTimeOffset? GetNotFoundTime(NotFoundKind kind, string name) => GetNotFoundNote(kind, name)?.RecordedAt;

    public bool IsKnownNotFound(string handle, DateTimeOffset now) => IsKnownNotFound(NotFoundKind.Player, handle, now);

    public bool IsKnownOrgNotFound(string sid, DateTimeOffset now) => IsKnownNotFound(NotFoundKind.Org, sid, now);

    private bool IsKnownNotFound(NotFoundKind kind, string name, DateTimeOffset now) =>
        GetNotFoundNote(kind, name) is { } note && now - note.RecordedAt < CacheSettings.NotFoundRetentionFor(note.Count);
}
