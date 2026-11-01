using System.Data;
using System.Globalization;
using Iris.Core.Overlay;
using Microsoft.Data.Sqlite;

namespace Iris.Core.Lookup;

public sealed partial class CacheDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 3;

    private sealed class Absent
    {
        public static readonly Absent Instance = new();
    }

    private const string ActivitySeparator = "|";

    private readonly SqliteConnection _connection;
    private readonly bool _ownsConnection;
    private readonly object _gate = new();
    private readonly LruCache<string, object> _memory;
    private SqliteTransaction? _transaction;
    private int _diskReads;

    public CacheDatabase(SqliteConnection openConnection, IClock? clock = null, int memoryCapacity = CacheSettings.DefaultMemoryCapacity)
        : this(openConnection, ownsConnection: false, clock, memoryCapacity)
    {
    }

    private CacheDatabase(SqliteConnection connection, bool ownsConnection, IClock? clock, int memoryCapacity)
    {
        if (connection.State != ConnectionState.Open)
            throw new ArgumentException("The SQLite connection must be open.", nameof(connection));
        _connection = connection;
        _ownsConnection = ownsConnection;
        _memory = new LruCache<string, object>(memoryCapacity, CacheSettings.MemoryIdle, clock, StringComparer.OrdinalIgnoreCase);
        ConfigureConnection();
        EnsureSchema();
    }

    public int DiskReads => Volatile.Read(ref _diskReads);

    public int MemoryCount => _memory.Count;

    private void ConfigureConnection()
    {
        lock (_gate)
        {
            Scalar("PRAGMA busy_timeout = 5000;");
            try
            {
                Scalar("PRAGMA journal_mode = WAL;");
                Scalar("PRAGMA synchronous = NORMAL;");
            }
            catch (SqliteException)
            {
            }
        }
    }

    public static CacheDatabase OpenFile(string path, IClock? clock = null, int memoryCapacity = CacheSettings.DefaultMemoryCapacity)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        try
        {
            return new CacheDatabase(connection, ownsConnection: true, clock, memoryCapacity);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ownsConnection)
                _connection.Dispose();
        }
    }

    public int SchemaVersion
    {
        get
        {
            lock (_gate)
                return (int)Convert.ToInt64(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        }
    }

    private static readonly string[] CreateStatements =
    {
        "CREATE TABLE players ("
        + "handle TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "source TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL)",

        "CREATE TABLE player_orgs ("
        + "handle TEXT NOT NULL COLLATE NOCASE, "
        + "position INTEGER NOT NULL, "
        + "sid TEXT COLLATE NOCASE, "
        + "is_main INTEGER NOT NULL, "
        + "archetype TEXT, "
        + "activities TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL, "
        + "PRIMARY KEY (handle, position))",

        "CREATE TABLE orgs ("
        + "sid TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "long_name TEXT, "
        + "member_count INTEGER, "
        + "archetype TEXT, "
        + "commitment TEXT, "
        + "is_exclusive INTEGER NOT NULL, "
        + "activities TEXT NOT NULL, "
        + "attrs_refreshed TEXT, "
        + "members_refreshed TEXT, "
        + "source TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL)",

        "CREATE TABLE org_members ("
        + "sid TEXT NOT NULL COLLATE NOCASE, "
        + "handle TEXT NOT NULL COLLATE NOCASE, "
        + "last_refreshed TEXT NOT NULL, "
        + "PRIMARY KEY (sid, handle))",

        "CREATE TABLE not_found ("
        + "kind TEXT NOT NULL, "
        + "name TEXT NOT NULL COLLATE NOCASE, "
        + "source TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL, "
        + "PRIMARY KEY (kind, name))",
    };

    private static readonly string[] Version2Statements =
    {
        "CREATE TABLE IF NOT EXISTS duplicate_name_counts ("
        + "name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "frames INTEGER NOT NULL, "
        + "last_seen TEXT NOT NULL)",
    };

    private static readonly string[] Version3Statements =
    {
        "ALTER TABLE not_found ADD COLUMN miss_count INTEGER NOT NULL DEFAULT 1",
    };

    private void EnsureSchema()
    {
        lock (_gate)
        {
            var version = Convert.ToInt64(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"The cache file has schema version {version}, but this IRIS only understands up to {CurrentSchemaVersion}. "
                    + "It was probably written by a newer IRIS. Update IRIS or delete the cache file (cache.db, and cache.db-wal / cache.db-shm next to it if they are there).");
            if (version == CurrentSchemaVersion)
                return;

            InTransaction(() =>
            {
                if (version == 0)
                    foreach (var statement in CreateStatements)
                        Execute(statement);
                if (version < 2)
                    foreach (var statement in Version2Statements)
                        Execute(statement);
                if (version < 3 && !HasColumn("not_found", "miss_count"))
                    foreach (var statement in Version3Statements)
                        Execute(statement);
                Execute("PRAGMA user_version = " + CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
            });
        }
    }

    private void InTransaction(Action body, Action? afterCommit = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            _transaction = transaction;
            try
            {
                body();
                transaction.Commit();
            }
            finally
            {
                _transaction = null;
            }

            afterCommit?.Invoke();
        }
    }

    private bool HasColumn(string table, string column) =>
        Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column", ("$column", column)), CultureInfo.InvariantCulture) > 0;

    private static string PlayerKey(string handle) => "p:" + handle;

    private static string OrgKey(string sid) => "o:" + sid;

    private static string NotFoundKey(NotFoundKind kind, string name) => (kind == NotFoundKind.Player ? "np:" : "no:") + name;

    private bool TryMemory<T>(string key, out T? value) where T : class
    {
        if (_memory.TryGet(key, out var stored))
        {
            value = stored as T;
            return true;
        }

        value = null;
        return false;
    }

    private void ForgetNote(NotFoundKind kind, string name)
    {
        var key = NotFoundKey(kind, name);
        if (_memory.TryGet(key, out _))
            Remember(key, null);
    }

    private void Remember(string key, object? value) => _memory.Set(key, value ?? Absent.Instance);

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private string? ScalarText(string sql, params (string Name, object? Value)[] parameters)
    {
        var value = Scalar(sql, parameters);
        if (value is null || value is DBNull)
            return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string KindText(NotFoundKind kind) => kind == NotFoundKind.Player ? "player" : "org";

    private static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string JoinActivities(IEnumerable<string> activities) =>
        string.Join(ActivitySeparator, activities.Select(a => a.Replace(ActivitySeparator, "")));

    private static IReadOnlyList<string> SplitActivities(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Split(ActivitySeparator);
}
