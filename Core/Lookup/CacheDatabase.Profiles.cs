
using System.Globalization;

namespace Iris.Core.Lookup;

public sealed record PlayerExtra(string Handle, DateTimeOffset? LastSeenPlate, string? Moniker, string? CitizenRecord, DateTimeOffset? ProfileRefreshed,
    DateOnly? Enlisted = null, bool EnlistedRead = false);

public sealed record SeenPlayer(string Handle, DateTimeOffset LastSeenPlate);

public sealed partial class CacheDatabase
{
    private const string PlayerExtraTable =
        "CREATE TABLE IF NOT EXISTS player_extra ("
        + "handle TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "last_seen_plate TEXT, "
        + "moniker TEXT, "
        + "citizen_record TEXT, "
        + "profile_refreshed TEXT, "
        + "enlisted TEXT)";

    private const string EnlistedColumn = "ALTER TABLE player_extra ADD COLUMN enlisted TEXT";

    private const string EnlistedFormat = "yyyy-MM-dd";

    public void NoteSeenOnPlate(string handle, DateTimeOffset now)
    {
        lock (_gate)
            Execute(
                "INSERT INTO player_extra (handle, last_seen_plate) VALUES ($handle, $time) "
                + "ON CONFLICT(handle) DO UPDATE SET last_seen_plate = excluded.last_seen_plate",
                ("$handle", handle), ("$time", FormatTime(now)));
    }

    public void SaveProfile(string handle, string? moniker, string citizenRecord, DateTimeOffset now, DateOnly? enlisted = null, bool enlistedRead = true)
    {
        string? enlistedText = !enlistedRead ? null : enlisted?.ToString(EnlistedFormat, CultureInfo.InvariantCulture) ?? string.Empty;
        lock (_gate)
            Execute(
                "INSERT INTO player_extra (handle, moniker, citizen_record, profile_refreshed, enlisted) VALUES ($handle, $moniker, $record, $time, $enlisted) "
                + "ON CONFLICT(handle) DO UPDATE SET moniker = excluded.moniker, citizen_record = excluded.citizen_record, profile_refreshed = excluded.profile_refreshed, "
                + "enlisted = excluded.enlisted",
                ("$handle", handle), ("$moniker", moniker), ("$record", citizenRecord), ("$time", FormatTime(now)), ("$enlisted", enlistedText));
    }

    public PlayerExtra? GetPlayerExtra(string handle)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT handle, last_seen_plate, moniker, citizen_record, profile_refreshed, enlisted FROM player_extra WHERE handle = $handle", ("$handle", handle));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            var enlistedText = reader.IsDBNull(5) ? null : reader.GetString(5);
            DateOnly? enlisted = enlistedText is { Length: > 0 }
                && DateOnly.TryParseExact(enlistedText, EnlistedFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
            return new PlayerExtra(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : ParseTime(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : ParseTime(reader.GetString(4)),
                enlisted,
                enlistedText is not null);
        }
    }

    public IReadOnlyList<SeenPlayer> SearchSeenPlayers(string text, int limit)
    {
        var found = new List<SeenPlayer>();
        if (text.Length == 0 || limit <= 0)
            return found;
        lock (_gate)
        {
            using var command = Command(
                "SELECT handle, last_seen_plate FROM player_extra WHERE last_seen_plate IS NOT NULL AND instr(lower(handle), lower($text)) > 0 "
                + "ORDER BY last_seen_plate DESC LIMIT $limit",
                ("$text", text), ("$limit", limit));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                found.Add(new SeenPlayer(reader.GetString(0), ParseTime(reader.GetString(1))));
        }
        return found;
    }

    public IReadOnlyList<string> SearchSavedPlayers(string text, int limit)
    {
        var found = new List<string>();
        if (text.Length == 0 || limit <= 0)
            return found;
        lock (_gate)
        {
            using var command = Command(
                "SELECT handle FROM players WHERE instr(lower(handle), lower($text)) > 0 ORDER BY handle COLLATE NOCASE LIMIT $limit",
                ("$text", text), ("$limit", limit));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                found.Add(reader.GetString(0));
        }
        return found;
    }
}
