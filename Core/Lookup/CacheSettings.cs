namespace Iris.Core.Lookup;

public sealed class CacheSettings
{
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";

    public const int DefaultTtlDays = 30;

    public static TimeSpan NotFoundRetentionFor(int misses) =>
        misses <= 1 ? TimeSpan.FromDays(1) : misses == 2 ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30);


    public static readonly TimeSpan RsiRequestTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan RsiPlayerTimeout = TimeSpan.FromSeconds(7);

    public static readonly TimeSpan RsiOrgTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan RsiMembersTimeout = RsiRequestTimeout;

    public const int RsiMaxMemberPages = 400;

    public const int RsiMaxRedirects = 3;

    public static readonly TimeSpan RsiDefaultInterval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan RsiMaxBackoff = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan RsiMaxRetryAfter = TimeSpan.FromSeconds(120);

    public const int RsiBreakerThreshold = 5;

    public static readonly TimeSpan RsiBreakerPause = TimeSpan.FromMinutes(5);


    public static readonly TimeSpan OrgRetryFirst = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan OrgRetrySecond = TimeSpan.FromMinutes(5);

    public const int OrgFetchMaxFailures = 3;

    public static readonly TimeSpan LiveWaitingExpiry = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan YieldPoll = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan MemoryIdle = TimeSpan.FromMinutes(5);

    public const int DefaultMemoryCapacity = 2000;


    public static readonly TimeSpan HostedRequestTimeout = TimeSpan.FromSeconds(15);

    public const int HostedMaxRedirects = 3;

    public const int ConfigMaxFileBytes = 1024 * 1024;

    public static readonly TimeSpan HostedCheckInterval = TimeSpan.FromHours(24);

    public static readonly TimeSpan HostedWakeInterval = TimeSpan.FromMinutes(10);


    public const string UpdateCheckUrl = "https://api.github.com/repos/Niteb1rd/IRIS/releases/latest";

    public const string ReleasesPageUrl = "https://github.com/Niteb1rd/IRIS/releases/latest";

    public static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(10);

    public const int UpdateCheckMaxBytes = 256 * 1024;

    private int _ttlDays;

    public CacheSettings(int ttlDays = DefaultTtlDays)
    {
        TtlDays = ttlDays;
    }

    public int TtlDays
    {
        get => Volatile.Read(ref _ttlDays);
        set
        {
            if (value != 7 && value != 30)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The refresh interval must be 7 or 30 days.");
            Volatile.Write(ref _ttlDays, value);
        }
    }

    public TimeSpan Ttl => TimeSpan.FromDays(TtlDays);
}
