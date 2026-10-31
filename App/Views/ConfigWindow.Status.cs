
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
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var row in e.OldItems.OfType<INotifyPropertyChanged>())
                row.PropertyChanged -= OnRowPropertyChanged;
        if (e.NewItems is not null)
            foreach (var row in e.NewItems.OfType<INotifyPropertyChanged>())
                row.PropertyChanged += OnRowPropertyChanged;
        UpdateSaveButton();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ListRowViewModel.Value):
            case nameof(ListRowViewModel.Color):
            case nameof(OrgRowViewModel.Priority):
            case nameof(RuleRowViewModel.Kind):
                UpdateSaveButton();
                break;
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => UpdateSaveButton();

    private void UpdateSaveButton()
    {
        if (!_ready || _isClosed)
            return;
        SaveButton.IsEnabled = _busyCount == 0
            && (HasUnsavedChanges(ignoreHostedUrl: false) || _services.ListFileHasErrors || _services.SettingsFileNeedsSave);
    }

    private void RefreshStateUi()
    {
        UpdateBanners();
        UpdateSaveButton();
    }

    private void UpdateBanners()
    {
        ShowBanner(ListBanner, ListBannerText, DataPaths.SocialListFileName, _services.ListFileErrors, _services.ListFileNote);
        ShowBanner(SettingsBanner, SettingsBannerText, DataPaths.SettingsFileName, _services.SettingsFileErrors, _services.SettingsFileNote);
    }

    private static void ShowBanner(Border banner, TextBlock text, string fileName, IReadOnlyList<string> errors, string note)
    {
        if (errors.Count == 0)
        {
            banner.Visibility = Visibility.Collapsed;
            return;
        }

        const int shown = 3;
        var message = new StringBuilder();
        message.Append(fileName).Append(" has errors. ").Append(note);
        foreach (var error in errors.Take(shown))
            message.Append("\n  - ").Append(error);
        if (errors.Count > shown)
            message.Append($"\n  ...and {errors.Count - shown} more (open the file to see them all).");
        text.Text = message.ToString();
        banner.Visibility = Visibility.Visible;
    }

    private void OnOpenListFile(object sender, RoutedEventArgs e) => OpenInEditor(_services.Paths.SocialListFile);

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e) => OpenInEditor(_services.Paths.SettingsFile);

    private void OpenInEditor(string path)
    {
        try
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            SetFooter("Could not open " + Path.GetFileName(path) + ": " + ex.Message);
        }
    }

    private void SetFooter(string message)
    {
        FooterStatus.Text = message;
        FooterStatus.ToolTip = message.Length == 0 ? null : message;
    }

    public void ShowStatus(string message)
    {
        if (!_isClosed)
            SetFooter(message);
    }

    private bool IsIdle() => !_saving && _busyCount == 0 && !_imports.IsBusy && !_closePromptOpen;

    private async void OnWatchTick(object? sender, EventArgs e)
    {
        if (_isClosed || _polling || _closePromptOpen)
            return;

        _polling = true;
        try
        {
            var poll = _pendingPoll;
            if (poll is null)
            {
                if (!IsIdle())
                    return;
                poll = await Task.Run(() => _services.PollConfigFiles());
            }

            if (!_isClosed && !IsIdle())
            {
                _pendingPoll = poll;
                return;
            }

            _pendingPoll = null;
            var changes = _services.ApplyPoll(poll);
            if (!_isClosed)
                ShowConfigChanges(changes);
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Checking the config files failed: " + ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowConfigChanges(ConfigChanges changes)
    {
        if (changes.ListChanged)
        {
            LoadRowsFrom(_services.CurrentConfig);
            _baseline = Snapshot();
        }
        if (changes.SettingsChanged)
            LoadSettingsControls();
        if (changes.ExclusionsChanged)
        {
            foreach (var row in _players)
                row.RefreshExclusionNote();
        }

        RefreshStateUi();
        if (!_services.ListFileMissing && FooterStatus.Text == MissingListNotice)
            SetFooter(string.Empty);
        if (changes.Message is { } message)
            SetFooter(message);
    }
}
