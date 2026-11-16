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


    public const int PanelWidthPercentMin = 15;

    public const int PanelWidthPercentMax = 22;
    public const int PanelTextSizeMin = 8;
    public const int PanelTextSizeMax = 25;
    public const int PanelRightGapPercentMin = 0;
    public const int PanelRightGapPercentMax = 15;
    public const int PanelVerticalGapPercentMin = 0;
    public const int PanelVerticalGapPercentMax = 15;

    public int PanelWidthPercent { get; init; }

    public int PanelTextSize { get; init; }

    public int PanelRightGapPercent { get; init; }

    public int PanelVerticalGapPercent { get; init; }

    public OrgDisplay PanelOrgDisplay { get; init; }

    public DateFormat DateFormat { get; init; }

    public AccentColor UiAccentColor { get; init; }

    public const int SoundVolumePercentMin = 0;
    public const int SoundVolumePercentMax = 100;
    public const int SoundVolumePercentStep = 5;

    public int SoundVolumePercent { get; init; }

    public const int UiCurvePercentMin = 0;
    public const int UiCurvePercentMax = 200;
    public const int UiCurvePercentStep = 10;

    public int UiCurvePercent { get; init; }

    public ImportMode ImportMode { get; init; }

    public bool PanelMainOrgOnly { get; init; }

    public const int DossierWidthPercentMin = 40;
    public const int DossierWidthPercentMax = 55;

    public int DossierWidthPercent { get; init; }


    public const int BoundaryPercentMin = 0;
    public const int BoundaryPercentMax = 30;

    public int BoundaryTop { get; init; }

    public int BoundaryBottom { get; init; }

    public int BoundaryLeft { get; init; }

    public int BoundaryRight { get; init; }


    public CpuUsage CpuUsage { get; init; }

    public const int TrackingLossSecondsMin = 5;
    public const int TrackingLossSecondsMax = 30;
    public const int TrackingLossSecondsStep = 5;

    public int TrackingLossDisplaySeconds { get; init; }

    public CaptureMethod CaptureMethod { get; init; }

    public bool OverlayVisibleInCapture { get; init; }

    public bool OverlayShownInCapture => OverlayVisibleInCapture && CaptureMethod == CaptureMethod.WindowCapture;

    public bool CheckForUpdates { get; init; }

    public bool HideCaptureBorder { get; init; }

    public bool CaptureCopyQueue { get; init; }

    public bool MiningDisabled { get; init; }

    public const int MiningLabelZoomPercentMin = 100;
    public const int MiningLabelZoomPercentMax = 200;
    public const int MiningLabelZoomPercentStep = 10;

    public int MiningLabelZoomPercent { get; init; }


    public static IReadOnlyList<int> SettingsWindowScales { get; } = new[] { 100, 110, 125, 150, 175 };

    public int SettingsWindowScale { get; init; }

    public DarkMode SettingsDarkMode { get; init; }


    public bool OverlayDisabled { get; init; }

    public string HotkeyToggleOverlay { get; init; } = string.Empty;

    public const int TimedOverlaySecondsMin = 5;
    public const int TimedOverlaySecondsMax = 30;

    public string HotkeyTimedOverlay { get; init; } = string.Empty;

    public string HotkeyInteractiveOverlay { get; init; } = string.Empty;

    public int TimedOverlaySeconds { get; init; }
}

public enum DarkMode
{
    System,

    On,

    Off,
}

public enum AccentColor
{
    Blue,
    Cyan,
    Mint,
    Green,
    Amber,
    Red,
    Magenta,
    Purple,
    White,
}

public enum DateFormat
{
    StarCitizen,

    RealLife,
}

public enum OrgDisplay
{
    FullName,

    Sid,
}

public enum CpuUsage
{
    Normal,

    High,

    Ultra,
}

public enum CaptureMethod
{
    DesktopDuplication,

    WindowCapture,
}
