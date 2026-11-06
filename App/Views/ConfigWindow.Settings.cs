
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Services;
using Iris.Core.Config;
using Iris.Core.Mining;
using Iris.Core.Overlay;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Iris.App.Views;

public partial class ConfigWindow
{
    private void LoadSettingsControls()
    {
        var settings = _services.Settings;
        CacheTtlCombo.SelectedIndex = settings.CacheTtlDays == 7 ? 1 : 0;
        OverlayHotkeyBox.HotkeyValue = settings.HotkeyToggleOverlay;
        PanelHotkeyBox.HotkeyValue = settings.HotkeyTogglePanel;
        TimedHotkeyBox.HotkeyValue = settings.HotkeyTimedOverlay;
        TimedSecondsCombo.SelectedIndex = TimedSecondsIndex(settings.TimedOverlaySeconds);
        PanelWidthSlider.Value = settings.PanelWidthPercent;
        PanelTextSizeSlider.Value = settings.PanelTextSize;
        PanelRightGapSlider.Value = settings.PanelRightGapPercent;
        PanelTopGapSlider.Value = settings.PanelTopGapPercent;
        OrgDisplayCombo.SelectedIndex = settings.PanelOrgDisplay == OrgDisplay.Sid ? 1 : 0;
        MainOrgOnlyCheck.IsChecked = settings.PanelMainOrgOnly;
        BoundaryTopSlider.Value = settings.BoundaryTop;
        BoundaryBottomSlider.Value = settings.BoundaryBottom;
        BoundaryLeftSlider.Value = settings.BoundaryLeft;
        BoundaryRightSlider.Value = settings.BoundaryRight;
        TrackingFpsSlider.Value = settings.NameplateTrackingFps;
        UpdateTrackingFpsLabel();
        CaptureMethodCombo.SelectedIndex = settings.CaptureMethod == CaptureMethod.WindowCapture ? 1 : 0;
        _visibleInCaptureStored = settings.OverlayVisibleInCapture;
        _hideBorderStored = settings.HideCaptureBorder;
        ShowCaptureControls();
        CheckForUpdatesCheck.IsChecked = settings.CheckForUpdates;
        MiningDisabledCheck.IsChecked = settings.MiningDisabled;
        WindowScaleCombo.SelectedIndex = ScaleIndex(settings.SettingsWindowScale);
        _darkModeShownOn = AppTheme.Resolve(settings.SettingsDarkMode);
        DarkModeCombo.SelectedIndex = _darkModeShownOn ? 0 : 1;
        LogEnabledCheck.IsChecked = settings.SessionLogEnabled;
        HostedUrlBox.Text = settings.HostedConfigUrl;
        HostedUrlError.Text = string.Empty;
        HostedUrlError.Visibility = Visibility.Collapsed;
        HostedAutoCheck.IsChecked = settings.HostedConfigAutoCheck && IsHostedUrlUsable();
        UpdateAutoCheckState();
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    private AppSettings BuildSettings() => _services.Settings with
    {
        CacheTtlDays = CacheTtlCombo.SelectedIndex == 1 ? 7 : 30,
        HotkeyToggleOverlay = OverlayHotkeyBox.HotkeyValue,
        HotkeyTogglePanel = PanelHotkeyBox.HotkeyValue,
        HotkeyTimedOverlay = TimedHotkeyBox.HotkeyValue,
        TimedOverlaySeconds = TimedSecondsCombo.SelectedIndex >= 0
            ? AppSettings.TimedOverlaySecondsMin + TimedSecondsCombo.SelectedIndex
            : _services.Settings.TimedOverlaySeconds,
        PanelWidthPercent = (int)Math.Round(PanelWidthSlider.Value),
        PanelTextSize = (int)Math.Round(PanelTextSizeSlider.Value),
        PanelRightGapPercent = (int)Math.Round(PanelRightGapSlider.Value),
        PanelTopGapPercent = (int)Math.Round(PanelTopGapSlider.Value),
        PanelOrgDisplay = OrgDisplayCombo.SelectedIndex == 1 ? OrgDisplay.Sid : OrgDisplay.FullName,
        PanelMainOrgOnly = MainOrgOnlyCheck.IsChecked == true,
        BoundaryTop = (int)Math.Round(BoundaryTopSlider.Value),
        BoundaryBottom = (int)Math.Round(BoundaryBottomSlider.Value),
        BoundaryLeft = (int)Math.Round(BoundaryLeftSlider.Value),
        BoundaryRight = (int)Math.Round(BoundaryRightSlider.Value),
        NameplateTrackingFps = (int)Math.Round(TrackingFpsSlider.Value),
        CaptureMethod = SelectedCaptureMethod(),
        OverlayVisibleInCapture = OverlayVisibleInCaptureCheck.IsEnabled ? OverlayVisibleInCaptureCheck.IsChecked == true : _visibleInCaptureStored,
        HideCaptureBorder = HideCaptureBorderCheck.IsEnabled ? HideCaptureBorderCheck.IsChecked == true : _hideBorderStored,
        CheckForUpdates = CheckForUpdatesCheck.IsChecked == true,
        MiningDisabled = MiningDisabledCheck.IsChecked == true,
        SettingsWindowScale = WindowScaleCombo.SelectedIndex >= 0
            ? AppSettings.SettingsWindowScales[WindowScaleCombo.SelectedIndex]
            : _services.Settings.SettingsWindowScale,
        SettingsDarkMode = SelectedDarkMode(),
        SessionLogEnabled = LogEnabledCheck.IsChecked == true,
        HostedConfigUrl = HostedUrlBox.Text.Trim(),
        HostedConfigAutoCheck = HostedAutoCheck.IsChecked == true && IsHostedUrlUsable(),
    };

    private static int TimedSecondsIndex(int seconds) =>
        Math.Clamp(seconds, AppSettings.TimedOverlaySecondsMin, AppSettings.TimedOverlaySecondsMax) - AppSettings.TimedOverlaySecondsMin;

    private DarkMode SelectedDarkMode()
    {
        var saved = _services.Settings.SettingsDarkMode;
        if (DarkModeCombo.SelectedIndex < 0)
            return saved;
        var on = DarkModeCombo.SelectedIndex == 0;
        if (saved == DarkMode.System && on == _darkModeShownOn)
            return DarkMode.System;
        return on ? DarkMode.On : DarkMode.Off;
    }

    private static int ScaleIndex(int percent)
    {
        var scales = AppSettings.SettingsWindowScales;
        for (var i = 0; i < scales.Count; i++)
            if (scales[i] == percent)
                return i;
        return 0;
    }

    private void ApplyWindowScale()
    {
        var scale = _services.Settings.SettingsWindowScale / 100.0;
        if (scale <= 0 || Math.Abs(scale - _windowScale) < 0.001)
            return;
        var ratio = scale / _windowScale;
        _windowScale = scale;
        RootGrid.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);

        var area = WorkArea();
        MinWidth = Math.Min(BaseMinWidth * scale, area.Width);
        MinHeight = Math.Min(BaseMinHeight * scale, area.Height);
        var width = double.IsNaN(Width) ? ActualWidth : Width;
        var height = double.IsNaN(Height) ? ActualHeight : Height;
        Width = Math.Clamp(width * ratio, MinWidth, area.Width);
        Height = Math.Clamp(height * ratio, MinHeight, area.Height);
        if (!IsLoaded)
            return;

        Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
    }

    private const double BaseMinWidth = 750;
    private const double BaseMinHeight = 445;

    private Rect WorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = PresentationSource.FromVisual(this);
        if (handle == IntPtr.Zero || source?.CompositionTarget is null)
            return SystemParameters.WorkArea;
        var pixels = Forms.Screen.FromHandle(handle).WorkingArea;
        var toDip = source.CompositionTarget.TransformFromDevice;
        return new Rect(toDip.Transform(new Point(pixels.Left, pixels.Top)), toDip.Transform(new Point(pixels.Right, pixels.Bottom)));
    }

    private CaptureMethod SelectedCaptureMethod() => CaptureMethodCombo.SelectedIndex == 1 ? CaptureMethod.WindowCapture : CaptureMethod.DesktopDuplication;

    internal const string DuplicationOverlayTip = "Desktop Duplication captures the screen, so the overlay is always hidden from capture software (IRIS would read its own overlay otherwise).";
    internal const string DuplicationBorderTip = "Desktop Duplication draws no capture border.";
    internal const string BorderTip = "Windows 11 only. Asks Windows to drop the yellow border it draws around captured windows; Windows asks you once to allow it.";

    private void ShowCaptureControls()
    {
        var wgc = SelectedCaptureMethod() == CaptureMethod.WindowCapture;
        OverlayVisibleInCaptureCheck.IsEnabled = wgc;
        OverlayVisibleInCaptureCheck.IsChecked = wgc && _visibleInCaptureStored;
        OverlayVisibleInCaptureCheck.ToolTip = wgc ? null : DuplicationOverlayTip;
        var border = wgc && CaptureBorder.IsSupported;
        HideCaptureBorderCheck.IsEnabled = border;
        HideCaptureBorderCheck.IsChecked = border && _hideBorderStored;
        HideCaptureBorderCheck.ToolTip = wgc ? BorderTip : DuplicationBorderTip;
    }

    private void OnCaptureMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverlayVisibleInCaptureCheck is null || HideCaptureBorderCheck is null)
            return;
        if (OverlayVisibleInCaptureCheck.IsEnabled)
            _visibleInCaptureStored = OverlayVisibleInCaptureCheck.IsChecked == true;
        if (HideCaptureBorderCheck.IsEnabled)
            _hideBorderStored = HideCaptureBorderCheck.IsChecked == true;
        ShowCaptureControls();
        OnSettingChanged(sender, e);
    }

    private bool ValidateHostedUrl()
    {
        var text = HostedUrlBox.Text.Trim();
        var problem = text.Length == 0 ? null : HostedConfigUrl.Check(text);
        HostedUrlError.Text = problem ?? string.Empty;
        HostedUrlError.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        return problem is null;
    }

    private void OnHotkeyBoxChanged(object? sender, EventArgs e)
    {
        if (!_ready)
            return;
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    private void OnClearOverlayHotkey(object sender, RoutedEventArgs e) => OverlayHotkeyBox.ClearHotkey();

    private void OnClearPanelHotkey(object sender, RoutedEventArgs e) => PanelHotkeyBox.ClearHotkey();

    private void OnClearTimedHotkey(object sender, RoutedEventArgs e) => TimedHotkeyBox.ClearHotkey();

    private void OnHotkeyStatusChanged(object? sender, EventArgs e) => UpdateHotkeyErrors();

    private void UpdateHotkeyErrors()
    {
        if (OverlayHotkeyError is null || PanelHotkeyError is null || TimedHotkeyError is null)
            return;
        var saved = _services.Settings;
        var rows = new (HotkeyAction Action, HotkeyBox Box, TextBlock Error)[]
        {
            (HotkeyAction.ToggleOverlay, OverlayHotkeyBox, OverlayHotkeyError),
            (HotkeyAction.TogglePanel, PanelHotkeyBox, PanelHotkeyError),
            (HotkeyAction.TimedOverlay, TimedHotkeyBox, TimedHotkeyError),
        };

        foreach (var row in rows)
        {
            var text = row.Box.HotkeyValue;
            string? message = null;
            foreach (var other in rows)
            {
                if (other.Action != row.Action && text.Length > 0 && other.Box.HotkeyValue == text)
                {
                    message = $"Same as the {HotkeyManager.NameOf(other.Action)} hotkey. Choose different ones.";
                    break;
                }
            }

            if (message is null && text == HotkeyManager.TextOf(saved, row.Action))
                message = _overlay.Hotkeys.ErrorOf(row.Action);
            ShowHotkeyError(row.Error, message);
        }
    }

    private static void ShowHotkeyError(TextBlock block, string? message)
    {
        block.Text = message ?? string.Empty;
        block.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnServicesSettingsApplied(object? sender, EventArgs e)
    {
        if (_isClosed || !_ready)
            return;
        ApplyWindowScale();
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    private static void ConfigureSlider(Slider slider, int minimum, int maximum)
    {
        slider.Maximum = maximum;
        slider.Minimum = minimum;
    }

    private void OnOverlaySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OverlayValuesChanged();

    private void OnPanelOrgOptionChanged(object sender, RoutedEventArgs e) => OverlayValuesChanged();

    private void OnTrackingFpsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTrackingFpsLabel();
        OverlayValuesChanged();
    }

    private void UpdateTrackingFpsLabel()
    {
        if (TrackingFpsLabel is null || TrackingFpsSlider is null)
            return;
        var value = (int)Math.Round(TrackingFpsSlider.Value);
        TrackingFpsLabel.Text = value > AppSettings.NameplateTrackingFpsHighest
            ? "Max"
            : value.ToString(CultureInfo.InvariantCulture) + " fps";
    }

    private void OverlayValuesChanged()
    {
        if (!_ready)
            return;
        UpdateSaveButton();
        _overlay.UpdateTestSettings(BuildSettings());
    }

    internal const string RecordingTip =
        "Recording software must be set to capture desktop to record the IRIS overlay. If set to capture Star Citizen, the overlay will not be captured.";

    private void OnVisibleInCaptureClick(object sender, RoutedEventArgs e)
    {
        if (OverlayVisibleInCaptureCheck.IsChecked != true)
            return;
        PromptDialog.Tell(RecordingTip, owner: this, userJustActed: true, okText: "Close");
    }

    private void OnShowBoundary(object sender, RoutedEventArgs e) => ToggleTest(TestOverlayPart.Boundary);

    private void OnShowInfoPanel(object sender, RoutedEventArgs e) => ToggleTest(TestOverlayPart.Panel);

    private void ToggleTest(TestOverlayPart part)
    {
        try
        {
            if (_overlay.IsTestRunning && _overlay.TestPart == part)
            {
                _testStoppedByUser = true;
                try
                {
                    _overlay.StopTest();
                }
                finally
                {
                    _testStoppedByUser = false;
                }
                SetFooter($"{TestName(part)} stopped.");
                return;
            }
            var where = _overlay.StartTest(BuildSettings(), this, part);
            SetFooter($"{TestName(part)} shown {where} for {OverlayController.TestSeconds} seconds.");
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Test overlay failed: " + ex);
            SetFooter("Could not show the test overlay: " + ex.Message);
        }
    }

    private void OnTestStateChanged(object? sender, EventArgs e)
    {
        if (_isClosed)
            return;
        UpdateTestButton();
        if (!_overlay.IsTestRunning && !_testStoppedByUser)
            SetFooter("Sample overlay ended.");
    }

    private static string TestName(TestOverlayPart part) => part == TestOverlayPart.Boundary ? "Boundary sample" : "Info panel sample";

    private void UpdateTestButton()
    {
        var running = _overlay.IsTestRunning;
        BoundaryTestButton.Content = running && _overlay.TestPart == TestOverlayPart.Boundary
            ? $"Stop Boundary ({_overlay.TestSecondsLeft} s)"
            : "Show Boundary";
        PanelTestButton.Content = running && _overlay.TestPart == TestOverlayPart.Panel
            ? $"Stop Info Panel ({_overlay.TestSecondsLeft} s)"
            : "Show Info Panel";
    }

    private void OnHostedUrlLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ValidateHostedUrl();

    private bool IsHostedUrlUsable() => HostedConfigUrl.Check(HostedUrlBox.Text) is null;

    private void UpdateAutoCheckState()
    {
        var usable = IsHostedUrlUsable();
        if (!usable && HostedAutoCheck.IsChecked == true)
            HostedAutoCheck.IsChecked = false;
        HostedAutoCheck.IsEnabled = usable;
    }

    private void OnHostedUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        UpdateAutoCheckState();
        UpdateSaveButton();
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _services.Paths.LogsFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetFooter("Could not open the log folder: " + ex.Message);
        }
    }

    private void OnEditMiningData(object sender, RoutedEventArgs e)
    {
        var source = new MiningTableSource(_services.Paths.MiningFile);
        if (!source.EnsureFile(out var error))
        {
            SetFooter("Could not write Config\\mining.txt: " + error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(source.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetFooter("Could not open Config\\mining.txt: " + ex.Message);
        }
    }

    private async void OnRefreshCache(object sender, RoutedEventArgs e)
    {
        if (_updater.IsRefreshing)
            return;

        SetFooter("Refreshing the cache… this takes a long time (about one request per second, slower if RSI asks us to wait).");
        var message = await _updater.RunFullRefreshAsync();
        if (message is null || _isClosed)
            return;
        SetFooter(message);
    }

    private void OnRefreshingChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnRefreshingChanged(sender, e)));
            return;
        }
        RefreshCacheButton.IsEnabled = !_updater.IsRefreshing;
    }
}
