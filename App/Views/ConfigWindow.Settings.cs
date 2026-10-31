
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
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Services;
using Iris.Core.Config;
using Microsoft.Win32;

namespace Iris.App.Views;

public partial class ConfigWindow
{
    private void LoadSettingsControls()
    {
        var settings = _services.Settings;
        CacheTtlCombo.SelectedIndex = settings.CacheTtlDays == 7 ? 1 : 0;
        OverlayHotkeyBox.HotkeyValue = settings.HotkeyToggleOverlay;
        PanelHotkeyBox.HotkeyValue = settings.HotkeyTogglePanel;
        PanelWidthSlider.Value = settings.PanelWidthPercent;
        PanelTextSizeSlider.Value = settings.PanelTextSize;
        PanelRightGapSlider.Value = settings.PanelRightGapPercent;
        PanelTopGapSlider.Value = settings.PanelTopGapPercent;
        BoundaryTopSlider.Value = settings.BoundaryTop;
        BoundaryBottomSlider.Value = settings.BoundaryBottom;
        BoundaryLeftSlider.Value = settings.BoundaryLeft;
        BoundaryRightSlider.Value = settings.BoundaryRight;
        TrackingFpsSlider.Value = settings.NameplateTrackingFps;
        UpdateTrackingFpsLabel();
        OverlayVisibleInCaptureCheck.IsChecked = settings.OverlayVisibleInCapture;
        ShowCaptureBorderSetting(settings.HideCaptureBorder);
        CheckForUpdatesCheck.IsChecked = settings.CheckForUpdates;
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
        PanelWidthPercent = (int)Math.Round(PanelWidthSlider.Value),
        PanelTextSize = (int)Math.Round(PanelTextSizeSlider.Value),
        PanelRightGapPercent = (int)Math.Round(PanelRightGapSlider.Value),
        PanelTopGapPercent = (int)Math.Round(PanelTopGapSlider.Value),
        BoundaryTop = (int)Math.Round(BoundaryTopSlider.Value),
        BoundaryBottom = (int)Math.Round(BoundaryBottomSlider.Value),
        BoundaryLeft = (int)Math.Round(BoundaryLeftSlider.Value),
        BoundaryRight = (int)Math.Round(BoundaryRightSlider.Value),
        NameplateTrackingFps = (int)Math.Round(TrackingFpsSlider.Value),
        OverlayVisibleInCapture = OverlayVisibleInCaptureCheck.IsChecked == true,
        HideCaptureBorder = CaptureBorder.IsSupported ? HideCaptureBorderCheck.IsChecked == true : _services.Settings.HideCaptureBorder,
        CheckForUpdates = CheckForUpdatesCheck.IsChecked == true,
        SessionLogEnabled = LogEnabledCheck.IsChecked == true,
        HostedConfigUrl = HostedUrlBox.Text.Trim(),
        HostedConfigAutoCheck = HostedAutoCheck.IsChecked == true && IsHostedUrlUsable(),
    };

    private void ShowCaptureBorderSetting(bool stored)
    {
        if (CaptureBorder.IsSupported)
        {
            HideCaptureBorderCheck.IsEnabled = true;
            HideCaptureBorderCheck.IsChecked = stored;
            return;
        }
        HideCaptureBorderCheck.IsEnabled = false;
        HideCaptureBorderCheck.IsChecked = false;
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

    private void OnHotkeyStatusChanged(object? sender, EventArgs e) => UpdateHotkeyErrors();

    private void UpdateHotkeyErrors()
    {
        if (OverlayHotkeyError is null || PanelHotkeyError is null)
            return;
        var saved = _services.Settings;
        var overlayText = OverlayHotkeyBox.HotkeyValue;
        var panelText = PanelHotkeyBox.HotkeyValue;
        var same = overlayText.Length > 0 && overlayText == panelText;
        const string SameText = "Both hotkeys are the same. Choose two different ones.";

        ShowHotkeyError(OverlayHotkeyError,
            same ? SameText : overlayText == saved.HotkeyToggleOverlay ? _overlay.Hotkeys.OverlayError : null);
        ShowHotkeyError(PanelHotkeyError,
            same ? SameText : panelText == saved.HotkeyTogglePanel ? _overlay.Hotkeys.PanelError : null);
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
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    private static void ConfigureSlider(Slider slider, int minimum, int maximum)
    {
        slider.Maximum = maximum;
        slider.Minimum = minimum;
    }

    private void OnOverlaySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OverlayValuesChanged();

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
        PromptDialog.Tell(RecordingTip, balloonText: string.Empty, notify: null, owner: this, userJustActed: true, okText: "Close");
    }

    private void OnTestOverlay(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_overlay.IsTestRunning)
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
                SetFooter("Test overlay stopped.");
                return;
            }
            var where = _overlay.StartTest(BuildSettings(), this);
            SetFooter($"Test overlay shown {where} for {OverlayController.TestSeconds} seconds.");
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
            SetFooter("Test overlay ended.");
    }

    private void UpdateTestButton()
    {
        TestOverlayButton.Content = _overlay.IsTestRunning
            ? $"Stop Test Overlay ({_overlay.TestSecondsLeft} s)"
            : "Show Test Overlay";
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

    private async void OnRefreshCache(object sender, RoutedEventArgs e)
    {
        if (_updater.IsRefreshing)
            return;

        SetFooter("Refreshing the cache… this takes a long time (about one request per second, slower if RSI asks us to wait).");
        var message = await _updater.RunFullRefreshAsync();
        if (message is null)
            return;
        if (_isClosed)
            _services.Notify?.Invoke(message);
        else
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
