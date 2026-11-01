using Iris.Core.Lookup;

namespace Iris.Core.Config;

public enum RowCheckState
{
    Valid,

    NotFound,

    InvalidFormat,

    Failed,
}

public sealed record RowCheckResult(RowCheckState State, string Message, string? DisplayText = null)
{
    public bool IsValid => State == RowCheckState.Valid;
}

public sealed class RowValidator
{
    public const string InvalidHandleMessage = "Handles are 4 to 64 characters: letters, digits, - and _";
    public const string InvalidSidMessage = "SIDs are 1 to 20 characters: letters, digits, - and _";
    public const string DuplicateMessage = "already in the list";
    public const string FallbackFailureMessage = "RSI not reachable";

    private readonly CacheDatabase _database;
    private readonly IRsiClient _rsi;
    private readonly IClock _clock;
    private readonly CacheSettings _settings;
    private readonly ILog _log;

    public RowValidator(CacheDatabase database, IRsiClient rsi, IClock clock, CacheSettings settings, ILog? log = null)
    {
        _database = database;
        _rsi = rsi;
        _clock = clock;
        _settings = settings;
        _log = log ?? NullLog.Instance;
    }


    public static RowCheckResult? CheckHandleFormat(string? handle) =>
        RsiNames.IsValidHandle(handle?.Trim()) ? null : new RowCheckResult(RowCheckState.InvalidFormat, InvalidHandleMessage);

    public static RowCheckResult? CheckSidFormat(string? sid) =>
        RsiNames.IsValidSid(sid?.Trim()) ? null : new RowCheckResult(RowCheckState.InvalidFormat, InvalidSidMessage);

    public static bool IsDuplicate(string value, IEnumerable<string> others)
    {
        var trimmed = value.Trim();
        return others.Any(o => string.Equals(o?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<RowCheckResult> ValidatePlayerAsync(
        string handle, IEnumerable<string> otherHandles, CancellationToken ct = default)
    {
        var name = (handle ?? string.Empty).Trim();
        var format = CheckHandleFormat(name);
        if (format is not null)
            return format;
        if (IsDuplicate(name, otherHandles))
            return new RowCheckResult(RowCheckState.InvalidFormat, DuplicateMessage);

        var now = _clock.UtcNow;

        var cached = _database.GetPlayer(name);
        if (cached is not null && !IsStale(cached.LastRefreshed, now))
        {
            if (CacheSources.Stronger(cached.Source, CacheSource.PlayerList) != cached.Source)
                _database.SavePlayer(new PlayerOrgs(cached.Handle, cached.Orgs), CacheSource.PlayerList, cached.LastRefreshed);
            return Valid(cached.Handle);
        }

        if (_database.IsKnownNotFound(name, now))
            return NotFound();

        var result = await _rsi.GetPlayerOrgsAsync(name, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                _database.SavePlayer(result.Data!, CacheSource.PlayerList, _clock.UtcNow);
                return Valid(result.Data!.Handle);
            case RsiStatus.NotFound:
                _database.SaveNotFound(NotFoundKind.Player, name, CacheSource.PlayerList, _clock.UtcNow);
                return NotFound();
            default:
                return Failed("player " + name, result.Reason);
        }
    }

    public async Task<RowCheckResult> ValidateOrgAsync(
        string sid, IEnumerable<string> otherSids, CancellationToken ct = default)
    {
        var name = (sid ?? string.Empty).Trim();
        var format = CheckSidFormat(name);
        if (format is not null)
            return format;
        if (IsDuplicate(name, otherSids))
            return new RowCheckResult(RowCheckState.InvalidFormat, DuplicateMessage);

        var now = _clock.UtcNow;

        var cached = _database.GetOrg(name);
        if (cached?.LongName is { } cachedName && !IsStale(cached.LastRefreshed, now))
        {
            if (CacheSources.Stronger(cached.Source, CacheSource.OrgList) != cached.Source)
                _database.PromoteOrgSource(cached.Sid, CacheSource.OrgList);
            return Valid(cachedName);
        }

        if (_database.IsKnownOrgNotFound(name, now))
            return NotFound();

        var result = await _rsi.GetOrgInfoAsync(name, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                _database.SaveOrgAttributes(result.Data!, CacheSource.OrgList, _clock.UtcNow);
                return Valid(result.Data!.LongName);
            case RsiStatus.NotFound:
                _database.SaveNotFound(NotFoundKind.Org, name, CacheSource.OrgList, _clock.UtcNow);
                return NotFound();
            default:
                return Failed("org " + name, result.Reason);
        }
    }

    private bool IsStale(DateTimeOffset lastRefreshed, DateTimeOffset now) => now - lastRefreshed > _settings.Ttl;

    private static RowCheckResult Valid(string displayText) =>
        new(RowCheckState.Valid, displayText, displayText);

    private static RowCheckResult NotFound() =>
        new(RowCheckState.NotFound, "Not found on RSI");

    private RowCheckResult Failed(string what, string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? FallbackFailureMessage : reason!;
        _log.Write(_clock.UtcNow, $"Config check of {what} failed: {text}");
        return new RowCheckResult(RowCheckState.Failed, text);
    }
}
