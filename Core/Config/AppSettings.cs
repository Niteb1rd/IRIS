namespace Iris.Core.Config;

public sealed record AppSettings
{
    public const int FormatVersion = 1;

    public static AppSettings Defaults => DefaultSettings.Values;

    public int CacheTtlDays { get; init; }

    public bool SessionLogEnabled { get; init; }

    public string HostedConfigUrl { get; init; } = string.Empty;

    public string HostedConfigLastHash { get; init; } = string.Empty;

    public bool HostedConfigAutoCheck { get; init; }

    public DateTimeOffset? HostedConfigLastCheck { get; init; }


    public const int PanelWidthPercentMin = 8;
    public const int PanelWidthPercentMax = 25;
    public const int PanelTextSizeMin = 8;
    public const int PanelTextSizeMax = 20;
    public const int PanelRightGapPercentMin = 0;
    public const int PanelRightGapPercentMax = 15;
    public const int PanelTopGapPercentMin = 0;
    public const int PanelTopGapPercentMax = 15;

    public bool PanelEnabled { get; init; }

    public int PanelWidthPercent { get; init; }

    public int PanelTextSize { get; init; }

    public int PanelRightGapPercent { get; init; }

    public int PanelTopGapPercent { get; init; }


    public const int BoundaryPercentMin = 0;
    public const int BoundaryPercentMax = 30;

    public int BoundaryTop { get; init; }

    public int BoundaryBottom { get; init; }

    public int BoundaryLeft { get; init; }

    public int BoundaryRight { get; init; }


    public const int NameplateTrackingFpsMin = 30;

    public const int NameplateTrackingFpsHighest = 90;

    public const int NameplateTrackingFpsMax = NameplateTrackingFpsHighest + NameplateTrackingFpsStep;

    public const int NameplateTrackingFpsStep = 5;

    public int NameplateTrackingFps { get; init; }

    public bool IsTrackingMax => NameplateTrackingFps > NameplateTrackingFpsHighest;

    public int TrackingCapFps => IsTrackingMax ? 0 : Math.Max(NameplateTrackingFpsMin, NameplateTrackingFps);

    public bool OverlayVisibleInCapture { get; init; }

    public bool CheckForUpdates { get; init; }

    public bool HideCaptureBorder { get; init; }


    public bool OverlayDisabled { get; init; }

    public string HotkeyToggleOverlay { get; init; } = string.Empty;

    public string HotkeyTogglePanel { get; init; } = string.Empty;
}
