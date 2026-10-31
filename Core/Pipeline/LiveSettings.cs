namespace Iris.Core.Pipeline;

public sealed record LiveSettings
{
    public static LiveSettings Default { get; } = new();

    public double MinSpeedPxPerMs { get; init; } = 0.01;

    public double MaxShiftBoxHeights { get; init; } = 3.0;

    public double OrgCacheMs { get; init; } = 2000.0;

    public int OrgCacheEntries { get; init; } = 256;

    public int CacheRemembered { get; init; } = 512;

    public double LookupRetryMs { get; init; } = 60_000.0;

    public double IdleReleaseMs { get; init; } = 5000.0;

    public double SummaryEveryMs { get; init; } = 60_000.0;


    public const double FrameMaxAheadMs = 2.0;

    public const double FrameMaxBehindMs = 1000.0;

    public const double LateFrameThresholdMs = 50.0;

    public const double LateFrameLogEveryMs = 10_000.0;

    public const int LateFrameMinCount = 10;

    public const double LateFrameWindowMs = 10_000.0;

    public const double DefaultFrameIntervalMs = 1000.0 / 60.0;

    public const double LineHeightAt1440 = 22.0;

    public const double BoundaryDropLines = 0.5;


    public static IReadOnlyList<double> EngineRetryBackoffMs { get; } = new double[] { 5000, 30000, 300000 };

    public const int WorkerIdleWaitMs = 100;

    public const int WorkerMaxWaitMs = 20;

    public const int CaptureUnavailablePauseMs = 50;

    public const int WorkerErrorPauseMs = 50;

    public const int TooSmallPauseMs = 100;

    public const int FramePoolSize = 8;

    public const int CaptureAcquireTimeoutMs = 50;

    public const long CaptureRetryAfterFailureMs = 2000;

    public const long CaptureNoFrameRestartMs = 3000;

    public const int RenderIdleWaitMs = 100;

    public const long RenderRetryDelayMs = 500;

    public const double RenderRoomAroundPx = 64.0;

    public const int SurfaceBucketPx = 128;

    public const double VBlankInstantFraction = 0.1;

    public const int VBlankInstantLimit = 8;

    public const long VBlankGiveUpMs = 5000;

    public const long VBlankResolveRetryMs = 2000;

    public const double VBlankLeadMs = 1.0;

    public const double VBlankMaxPeriodMs = 50.0;

    public const double DisplayFallbackPeriodMs = 1000.0 / 60;

    public const long DisplayPeriodCacheMs = 5000;

    public static readonly TimeSpan GameCheckInterval = TimeSpan.FromMilliseconds(250);

    public const long GameProcessCacheMs = 60_000;

    public const long OtherProcessCacheMs = 5_000;
}
