using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Iris.Core.Config;

public sealed class AppSettingsLoadResult
{
    public AppSettingsLoadResult(AppSettings settings, bool isNew)
    {
        Settings = settings;
        IsNew = isNew;
    }

    public AppSettings Settings { get; }

    public bool IsNew { get; }

    public List<string> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

public static class AppSettingsToml
{
    private static readonly string[] KnownKeys =
    {
        "format_version", "cache_ttl_days", "session_log_enabled", "hosted_config_url", "hosted_config_last_hash",
        "hosted_config_auto_check", "hosted_config_last_check",
        "panel_width_percent", "panel_text_size", "panel_right_gap_percent", "panel_top_gap_percent",
        "boundary_top", "boundary_bottom", "boundary_left", "boundary_right",
        "nameplate_tracking_fps", "tracking_loss_display_seconds", "capture_method", "overlay_visible_in_capture", "hide_capture_border", "check_for_updates",
        "settings_window_scale", "settings_dark_mode", "overlay_disabled", "hotkey_toggle_overlay",
        "hotkey_timed_overlay", "timed_overlay_seconds", "mining_disabled", "mining_label_zoom_percent", "panel_org_display", "panel_main_org_only", "import_mode",
        "hotkey_interactive_overlay", "dossier_width_percent",
    };

    private static readonly string[] RetiredKeys = { "interactive_exit_key", "panel_enabled", "hotkey_toggle_panel" };

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string Write(AppSettings settings)
    {
        var ttl = settings.CacheTtlDays == 7 ? 7 : 30;
        var sb = new StringBuilder();
        sb.AppendLine("# IRIS settings for this PC (TOML format). Not part of the shareable list file; never exported.");
        sb.AppendLine($"format_version = {AppSettings.FormatVersion}");
        sb.AppendLine($"cache_ttl_days = {ttl}");
        sb.AppendLine($"session_log_enabled = {Bool(settings.SessionLogEnabled)}");
        sb.AppendLine($"hosted_config_url = {SocialConfigToml.Quote(settings.HostedConfigUrl ?? string.Empty)}");
        sb.AppendLine($"hosted_config_last_hash = {SocialConfigToml.Quote(settings.HostedConfigLastHash ?? string.Empty)}");
        sb.AppendLine($"hosted_config_auto_check = {Bool(settings.HostedConfigAutoCheck)}");
        sb.AppendLine($"import_mode = {SocialConfigToml.Quote(ImportModeText(settings.ImportMode))}");
        if (settings.HostedConfigLastCheck is { } lastCheck)
            sb.AppendLine($"hosted_config_last_check = {SocialConfigToml.Quote(lastCheck.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture))}");
        sb.AppendLine($"panel_width_percent = {Math.Clamp(settings.PanelWidthPercent, AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax)}");
        sb.AppendLine($"panel_text_size = {Math.Clamp(settings.PanelTextSize, AppSettings.PanelTextSizeMin, AppSettings.PanelTextSizeMax)}");
        sb.AppendLine($"panel_right_gap_percent = {Math.Clamp(settings.PanelRightGapPercent, AppSettings.PanelRightGapPercentMin, AppSettings.PanelRightGapPercentMax)}");
        sb.AppendLine($"panel_top_gap_percent = {Math.Clamp(settings.PanelTopGapPercent, AppSettings.PanelTopGapPercentMin, AppSettings.PanelTopGapPercentMax)}");
        sb.AppendLine($"panel_org_display = {SocialConfigToml.Quote(OrgDisplayText(settings.PanelOrgDisplay))}");
        sb.AppendLine($"panel_main_org_only = {Bool(settings.PanelMainOrgOnly)}");
        sb.AppendLine($"dossier_width_percent = {Math.Clamp(settings.DossierWidthPercent, AppSettings.DossierWidthPercentMin, AppSettings.DossierWidthPercentMax)}");
        sb.AppendLine($"boundary_top = {ClampBoundary(settings.BoundaryTop)}");
        sb.AppendLine($"boundary_bottom = {ClampBoundary(settings.BoundaryBottom)}");
        sb.AppendLine($"boundary_left = {ClampBoundary(settings.BoundaryLeft)}");
        sb.AppendLine($"boundary_right = {ClampBoundary(settings.BoundaryRight)}");
        sb.AppendLine($"nameplate_tracking_fps = {WriteTrackingFps(settings.NameplateTrackingFps)}");
        sb.AppendLine($"tracking_loss_display_seconds = {SnapLossSeconds(settings.TrackingLossDisplaySeconds)}");
        sb.AppendLine($"capture_method = {SocialConfigToml.Quote(CaptureMethodText(settings.CaptureMethod))}");
        sb.AppendLine($"overlay_visible_in_capture = {Bool(settings.OverlayVisibleInCapture)}");
        sb.AppendLine($"hide_capture_border = {Bool(settings.HideCaptureBorder)}");
        sb.AppendLine($"check_for_updates = {Bool(settings.CheckForUpdates)}");
        sb.AppendLine($"settings_window_scale = {WriteWindowScale(settings.SettingsWindowScale)}");
        sb.AppendLine($"settings_dark_mode = {SocialConfigToml.Quote(DarkModeText(settings.SettingsDarkMode))}");
        sb.AppendLine($"overlay_disabled = {Bool(settings.OverlayDisabled)}");
        sb.AppendLine($"hotkey_toggle_overlay = {SocialConfigToml.Quote(HotkeyText.Normalize(settings.HotkeyToggleOverlay))}");
        sb.AppendLine($"hotkey_timed_overlay = {SocialConfigToml.Quote(HotkeyText.Normalize(settings.HotkeyTimedOverlay))}");
        sb.AppendLine($"hotkey_interactive_overlay = {SocialConfigToml.Quote(HotkeyText.Normalize(settings.HotkeyInteractiveOverlay))}");
        sb.AppendLine($"timed_overlay_seconds = {Math.Clamp(settings.TimedOverlaySeconds, AppSettings.TimedOverlaySecondsMin, AppSettings.TimedOverlaySecondsMax)}");
        sb.AppendLine($"mining_disabled = {Bool(settings.MiningDisabled)}");
        sb.AppendLine($"mining_label_zoom_percent = {SnapZoom(settings.MiningLabelZoomPercent)}");
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    public static string CaptureMethodText(CaptureMethod method) => method == CaptureMethod.WindowCapture ? "wgc" : "desktop_duplication";

    public static readonly IReadOnlyList<ImportMode> ImportModes = new[] { ImportMode.Merge, ImportMode.MergeImportedWins, ImportMode.BackupExisting };

    public static string ImportModeText(ImportMode mode) => mode switch
    {
        ImportMode.Merge => "merge_local",
        ImportMode.MergeImportedWins => "merge_imported",
        _ => "backup",
    };

    public static string OrgDisplayText(OrgDisplay display) => display == OrgDisplay.Sid ? "sid" : "name";

    public static string DarkModeText(DarkMode mode) => mode switch
    {
        DarkMode.On => "on",
        DarkMode.Off => "off",
        _ => "system",
    };

    private static int ClampBoundary(int value) => Math.Clamp(value, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax);

    private static string WriteTrackingFps(int fps)
    {
        if (fps > AppSettings.NameplateTrackingFpsHighest)
            return "\"max\"";
        var step = AppSettings.NameplateTrackingFpsStep;
        var rounded = (int)Math.Round((double)fps / step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(rounded, AppSettings.NameplateTrackingFpsMin, AppSettings.NameplateTrackingFpsHighest).ToString(CultureInfo.InvariantCulture);
    }

    private static string WriteWindowScale(int percent) =>
        AppSettings.SettingsWindowScales.MinBy(v => Math.Abs(v - percent)).ToString(CultureInfo.InvariantCulture);

    public static AppSettingsLoadResult Read(string text) => ReadWith(text, AppSettings.Defaults);

    public static IReadOnlyList<string> MissingKeys(string text)
    {
        var doc = Toml.Parse(text);
        if (doc.HasErrors)
            return Array.Empty<string>();
        var root = Toml.ToModel(doc);
        return KnownKeys.Where(k => !root.ContainsKey(k)).ToList();
    }

    internal static AppSettingsLoadResult ReadWith(string text, AppSettings defaults)
    {
        var doc = Toml.Parse(text);
        if (doc.HasErrors)
        {
            var bad = new AppSettingsLoadResult(defaults, isNew: false);
            foreach (var d in doc.Diagnostics)
                bad.Errors.Add("TOML syntax: " + d);
            return bad;
        }

        TomlTable root;
        try
        {
            root = Toml.ToModel(doc);
        }
        catch (Exception ex)
        {
            var bad = new AppSettingsLoadResult(defaults, isNew: false);
            bad.Errors.Add("TOML structure: " + ex.Message);
            return bad;
        }

        var warnings = new List<string>();
        var errors = new List<string>();

        if (root.TryGetValue("format_version", out var versionRaw))
        {
            if (versionRaw is not long version)
                errors.Add("format_version: must be a whole number.");
            else if (version < 1 || version > AppSettings.FormatVersion)
                errors.Add($"format_version: {version} is not supported (this app reads version {AppSettings.FormatVersion}).");
        }

        foreach (var key in root.Keys)
            if (!KnownKeys.Contains(key) && !RetiredKeys.Contains(key))
                warnings.Add($"{key}: unknown setting, ignored.");

        if (errors.Count > 0)
        {
            var failed = new AppSettingsLoadResult(defaults, isNew: false);
            failed.Errors.AddRange(errors);
            failed.Warnings.AddRange(warnings);
            return failed;
        }

        var ttl = defaults.CacheTtlDays;
        if (root.TryGetValue("cache_ttl_days", out var ttlRaw))
        {
            if (ttlRaw is long days && (days == 7 || days == 30))
                ttl = (int)days;
            else
                warnings.Add($"cache_ttl_days: must be 30 or 7; using {defaults.CacheTtlDays}.");
        }

        var logEnabled = ReadBool(root, "session_log_enabled", defaults.SessionLogEnabled, warnings);

        var url = defaults.HostedConfigUrl;
        if (root.TryGetValue("hosted_config_url", out var urlRaw))
        {
            if (urlRaw is not string urlText)
            {
                warnings.Add("hosted_config_url: must be text; ignored.");
            }
            else if (urlText.Trim().Length > 0)
            {
                var problem = HostedConfigUrl.Check(urlText);
                if (problem is null)
                    url = urlText.Trim();
                else
                    warnings.Add("hosted_config_url: " + problem + " The address is ignored.");
            }
        }

        var hash = defaults.HostedConfigLastHash;
        if (root.TryGetValue("hosted_config_last_hash", out var hashRaw))
        {
            if (hashRaw is string hashText)
            {
                var candidate = hashText.Trim().ToLowerInvariant();
                if (candidate.Length == 0)
                    hash = string.Empty;
                else if (IsValidHash(candidate))
                    hash = candidate;
                else
                    warnings.Add("hosted_config_last_hash: must be 64 hex characters (a SHA-256); cleared, so the hosted config will be offered again.");
            }
            else
                warnings.Add("hosted_config_last_hash: must be text; ignored.");
        }

        var autoCheck = ReadBool(root, "hosted_config_auto_check", defaults.HostedConfigAutoCheck, warnings);
        if (autoCheck && url.Length == 0)
        {
            autoCheck = false;
            warnings.Add("hosted_config_auto_check: needs a valid hosted_config_url; switched off.");
        }

        DateTimeOffset? lastCheck = defaults.HostedConfigLastCheck;
        if (root.TryGetValue("hosted_config_last_check", out var checkRaw))
        {
            switch (checkRaw)
            {
                case string checkText when checkText.Trim().Length == 0:
                    break;
                case string checkText when TryParseTimestamp(checkText, out var parsed):
                    lastCheck = parsed;
                    break;
                case TomlDateTime tomlTime:
                    lastCheck = TruncateToSeconds(tomlTime.DateTime.ToUniversalTime());
                    break;
                case DateTimeOffset offsetValue:
                    lastCheck = TruncateToSeconds(offsetValue.ToUniversalTime());
                    break;
                case DateTime dateValue:
                    lastCheck = TruncateToSeconds(new DateTimeOffset(dateValue.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dateValue, DateTimeKind.Utc) : dateValue.ToUniversalTime()));
                    break;
                default:
                    warnings.Add("hosted_config_last_check: must be a date and time as text, like \"2026-10-22T09:30:00Z\"; ignored (treated as never checked).");
                    break;
            }
        }

        var panelWidth = ReadInt(root, "panel_width_percent", defaults.PanelWidthPercent,
            AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax, warnings);
        var dossierWidth = ReadInt(root, "dossier_width_percent", defaults.DossierWidthPercent,
            AppSettings.DossierWidthPercentMin, AppSettings.DossierWidthPercentMax, warnings);
        var panelText = ReadInt(root, "panel_text_size", defaults.PanelTextSize,
            AppSettings.PanelTextSizeMin, AppSettings.PanelTextSizeMax, warnings);
        var panelRightGap = ReadInt(root, "panel_right_gap_percent", defaults.PanelRightGapPercent,
            AppSettings.PanelRightGapPercentMin, AppSettings.PanelRightGapPercentMax, warnings);
        var panelTopGap = ReadInt(root, "panel_top_gap_percent", defaults.PanelTopGapPercent,
            AppSettings.PanelTopGapPercentMin, AppSettings.PanelTopGapPercentMax, warnings);

        var boundaryTop = ReadInt(root, "boundary_top", defaults.BoundaryTop, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax, warnings);
        var boundaryBottom = ReadInt(root, "boundary_bottom", defaults.BoundaryBottom, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax, warnings);
        var boundaryLeft = ReadInt(root, "boundary_left", defaults.BoundaryLeft, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax, warnings);
        var boundaryRight = ReadInt(root, "boundary_right", defaults.BoundaryRight, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax, warnings);

        var trackingFps = ReadTrackingFps(root, defaults.NameplateTrackingFps, warnings);
        var lossSeconds = SnapLossSeconds(ReadInt(root, "tracking_loss_display_seconds", defaults.TrackingLossDisplaySeconds,
            AppSettings.TrackingLossSecondsMin, AppSettings.TrackingLossSecondsMax, warnings));

        var captureMethod = ReadCaptureMethod(root, defaults.CaptureMethod, warnings);
        var visibleInCapture = ReadBool(root, "overlay_visible_in_capture", defaults.OverlayVisibleInCapture, warnings);
        var hideBorder = ReadBool(root, "hide_capture_border", defaults.HideCaptureBorder, warnings);
        var checkForUpdates = ReadBool(root, "check_for_updates", defaults.CheckForUpdates, warnings);
        var windowScale = ReadWindowScale(root, defaults.SettingsWindowScale, warnings);
        var darkMode = ReadDarkMode(root, defaults.SettingsDarkMode, warnings);
        var overlayDisabled = ReadBool(root, "overlay_disabled", defaults.OverlayDisabled, warnings);
        var hotkeyOverlay = ReadHotkey(root, "hotkey_toggle_overlay", defaults.HotkeyToggleOverlay, warnings);
        var hotkeyTimed = ReadHotkey(root, "hotkey_timed_overlay", defaults.HotkeyTimedOverlay, warnings);
        var hotkeyInteractive = ReadHotkey(root, "hotkey_interactive_overlay", defaults.HotkeyInteractiveOverlay, warnings);
        var timedSeconds = ReadTimedSeconds(root, defaults.TimedOverlaySeconds, warnings);
        var miningDisabled = ReadBool(root, "mining_disabled", defaults.MiningDisabled, warnings);
        var miningZoom = SnapZoom(ReadInt(root, "mining_label_zoom_percent", defaults.MiningLabelZoomPercent,
            AppSettings.MiningLabelZoomPercentMin, AppSettings.MiningLabelZoomPercentMax, warnings));
        var orgDisplay = ReadOrgDisplay(root, defaults.PanelOrgDisplay, warnings);
        var mainOrgOnly = ReadBool(root, "panel_main_org_only", defaults.PanelMainOrgOnly, warnings);
        var importMode = ReadImportMode(root, defaults.ImportMode, warnings);

        var settings = new AppSettings
        {
            OverlayDisabled = overlayDisabled,
            HotkeyToggleOverlay = hotkeyOverlay,
            HotkeyTimedOverlay = hotkeyTimed,
            HotkeyInteractiveOverlay = hotkeyInteractive,
            TimedOverlaySeconds = timedSeconds,
            MiningDisabled = miningDisabled,
            MiningLabelZoomPercent = miningZoom,
            PanelWidthPercent = panelWidth,
            PanelTextSize = panelText,
            PanelRightGapPercent = panelRightGap,
            PanelTopGapPercent = panelTopGap,
            PanelOrgDisplay = orgDisplay,
            PanelMainOrgOnly = mainOrgOnly,
            DossierWidthPercent = dossierWidth,
            ImportMode = importMode,
            BoundaryTop = boundaryTop,
            BoundaryBottom = boundaryBottom,
            BoundaryLeft = boundaryLeft,
            BoundaryRight = boundaryRight,
            NameplateTrackingFps = trackingFps,
            TrackingLossDisplaySeconds = lossSeconds,
            CaptureMethod = captureMethod,
            OverlayVisibleInCapture = visibleInCapture,
            HideCaptureBorder = hideBorder,
            CheckForUpdates = checkForUpdates,
            SettingsWindowScale = windowScale,
            SettingsDarkMode = darkMode,
            CacheTtlDays = ttl,
            SessionLogEnabled = logEnabled,
            HostedConfigUrl = url,
            HostedConfigLastHash = hash,
            HostedConfigAutoCheck = autoCheck,
            HostedConfigLastCheck = lastCheck,
        };
        var result = new AppSettingsLoadResult(settings, isNew: false);
        result.Warnings.AddRange(warnings);
        return result;
    }

    public static bool TryParseTimestamp(string? text, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = TruncateToSeconds(parsed);
            return true;
        }
        value = default;
        return false;
    }

    public static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    public static bool IsValidHash(string? text)
    {
        if (text is null || text.Length != 64)
            return false;
        foreach (var c in text)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static bool ReadBool(TomlTable root, string key, bool fallback, List<string> warnings)
    {
        if (!root.TryGetValue(key, out var raw))
            return fallback;
        if (raw is bool value)
            return value;
        warnings.Add($"{key}: must be true or false; using {Bool(fallback)}.");
        return fallback;
    }

    private static int SnapZoom(int percent)
    {
        var step = AppSettings.MiningLabelZoomPercentStep;
        var snapped = (int)Math.Round(percent / (double)step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, AppSettings.MiningLabelZoomPercentMin, AppSettings.MiningLabelZoomPercentMax);
    }

    internal static int SnapLossSeconds(int seconds)
    {
        var step = AppSettings.TrackingLossSecondsStep;
        var snapped = (int)Math.Round(seconds / (double)step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, AppSettings.TrackingLossSecondsMin, AppSettings.TrackingLossSecondsMax);
    }

    private static int ReadInt(TomlTable root, string key, int fallback, int min, int max, List<string> warnings)
    {
        if (!root.TryGetValue(key, out var raw))
            return fallback;
        if (raw is long value)
        {
            if (value >= min && value <= max)
                return (int)value;
            var clamped = value < min ? min : max;
            warnings.Add($"{key}: {value} is outside {min} to {max}; using {clamped}.");
            return clamped;
        }

        warnings.Add($"{key}: must be a whole number from {min} to {max}; using {fallback}.");
        return fallback;
    }

    private static int ReadTrackingFps(TomlTable root, int fallback, List<string> warnings)
    {
        const string Key = "nameplate_tracking_fps";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;

        var min = AppSettings.NameplateTrackingFpsMin;
        var highest = AppSettings.NameplateTrackingFpsHighest;
        var step = AppSettings.NameplateTrackingFpsStep;
        switch (raw)
        {
            case long value when value > highest:
                return AppSettings.NameplateTrackingFpsMax;
            case long value when value < min:
                warnings.Add($"{Key}: {value} is below {min}; using {min}.");
                return min;
            case long value when value % step == 0:
                return (int)value;
            case string text when text.Trim().Equals("max", StringComparison.OrdinalIgnoreCase):
                return AppSettings.NameplateTrackingFpsMax;
            default:
                var fallbackText = fallback > highest ? "max" : fallback.ToString(CultureInfo.InvariantCulture);
                warnings.Add($"{Key}: must be a whole number from {min} to {highest} in steps of {step}, or \"max\"; using {fallbackText}.");
                return fallback;
        }
    }

    private static int ReadWindowScale(TomlTable root, int fallback, List<string> warnings)
    {
        const string Key = "settings_window_scale";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;
        if (raw is long value && AppSettings.SettingsWindowScales.Any(v => v == value))
            return (int)value;
        warnings.Add($"{Key}: must be one of {string.Join(", ", AppSettings.SettingsWindowScales)}; using {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private static ImportMode ReadImportMode(TomlTable root, ImportMode fallback, List<string> warnings)
    {
        const string Key = "import_mode";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;
        if (raw is string text)
        {
            foreach (var mode in ImportModes)
            {
                if (text.Trim().Equals(ImportModeText(mode), StringComparison.OrdinalIgnoreCase))
                    return mode;
            }
        }

        warnings.Add($"{Key}: must be \"merge_local\", \"merge_imported\" or \"backup\"; using \"{ImportModeText(fallback)}\".");
        return fallback;
    }

    private static OrgDisplay ReadOrgDisplay(TomlTable root, OrgDisplay fallback, List<string> warnings)
    {
        const string Key = "panel_org_display";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;
        if (raw is string text)
        {
            foreach (var display in new[] { OrgDisplay.FullName, OrgDisplay.Sid })
            {
                if (text.Trim().Equals(OrgDisplayText(display), StringComparison.OrdinalIgnoreCase))
                    return display;
            }
        }

        warnings.Add($"{Key}: must be \"name\" or \"sid\"; using \"{OrgDisplayText(fallback)}\".");
        return fallback;
    }

    private static DarkMode ReadDarkMode(TomlTable root, DarkMode fallback, List<string> warnings)
    {
        const string Key = "settings_dark_mode";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;
        if (raw is string text)
        {
            foreach (var mode in new[] { DarkMode.System, DarkMode.On, DarkMode.Off })
            {
                if (text.Trim().Equals(DarkModeText(mode), StringComparison.OrdinalIgnoreCase))
                    return mode;
            }
        }

        warnings.Add($"{Key}: must be \"system\", \"on\" or \"off\"; using \"{DarkModeText(fallback)}\".");
        return fallback;
    }

    private static int ReadTimedSeconds(TomlTable root, int fallback, List<string> warnings)
    {
        const string Key = "timed_overlay_seconds";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;
        if (raw is long value && value >= AppSettings.TimedOverlaySecondsMin && value <= AppSettings.TimedOverlaySecondsMax)
            return (int)value;
        warnings.Add($"{Key}: must be a whole number from {AppSettings.TimedOverlaySecondsMin} to {AppSettings.TimedOverlaySecondsMax}; using {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private static CaptureMethod ReadCaptureMethod(TomlTable root, CaptureMethod fallback, List<string> warnings)
    {
        const string Key = "capture_method";
        if (!root.TryGetValue(Key, out var raw))
            return fallback;
        if (raw is string text)
        {
            var value = text.Trim();
            if (value.Equals("desktop_duplication", StringComparison.OrdinalIgnoreCase))
                return CaptureMethod.DesktopDuplication;
            if (value.Equals("wgc", StringComparison.OrdinalIgnoreCase))
                return CaptureMethod.WindowCapture;
        }

        warnings.Add($"{Key}: must be \"desktop_duplication\" or \"wgc\"; using \"{CaptureMethodText(fallback)}\".");
        return fallback;
    }

    private static string ReadHotkey(TomlTable root, string key, string fallback, List<string> warnings)
    {
        if (!root.TryGetValue(key, out var raw))
            return fallback;
        var fallbackText = fallback.Length == 0 ? "no hotkey is set" : $"using {fallback}";
        if (raw is not string text)
        {
            warnings.Add($"{key}: must be text like \"Ctrl+Shift+O\"; {fallbackText}.");
            return fallback;
        }
        if (text.Trim().Length == 0)
            return string.Empty;
        if (HotkeyText.TryParse(text, out var hotkey, out var problem))
        {
            if (HotkeyText.EscapeProblem(hotkey!) is not { } escape)
                return HotkeyText.Format(hotkey!);
            problem = escape;
        }
        warnings.Add($"{key}: {problem} {char.ToUpperInvariant(fallbackText[0])}{fallbackText[1..]}.");
        return fallback;
    }

    public static AppSettingsLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettingsLoadResult(AppSettings.Defaults, isNew: true);

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var unreadable = new AppSettingsLoadResult(AppSettings.Defaults, isNew: false);
            unreadable.Errors.Add("The file could not be read: " + ex.Message);
            return unreadable;
        }

        return Read(text);
    }

    public static SaveResult Save(string path, AppSettings settings, WatchedFile? watched = null)
    {
        var text = Write(settings);

        var check = Read(text);
        if (!check.IsValid)
            return SaveResult.Failed("The settings are not valid and were not saved: " + string.Join(" ", check.Errors));

        try
        {
            AtomicFile.WriteText(path, text, keepBackup: true);
            watched?.Wrote(text);
            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveResult.Failed("The settings could not be saved: " + ex.Message);
        }
    }
}
