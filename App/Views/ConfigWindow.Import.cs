
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
    private async void OnImportFromFile(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        BeginBusy();
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import IRIS config",
                Filter = ConfigFileFilter,
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var read = ConfigImporter.ReadFile(dialog.FileName);
            if (!read.Success || read.Text is null)
            {
                SetFooter("Import failed.");
                MessageBox.Show(this, "The file could not be imported:\n\n" + read.Error, "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var attempt = await _imports.ImportTextAsync(read.Text, "Import " + Path.GetFileName(dialog.FileName), ignoreHostedUrl: false, userJustActed: true);
            if (!_isClosed && attempt.Outcome != ImportOutcome.Imported)
                SetFooter(attempt.Message);
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Import from file failed: " + ex);
            if (!_isClosed)
                SetFooter("Import failed: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private async void OnImportFromUrl(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        var url = HostedUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            HostedUrlError.Text = "Enter the web address of your config file first (it must start with https://).";
            HostedUrlError.Visibility = Visibility.Visible;
            HostedUrlBox.Focus();
            return;
        }
        if (!ValidateHostedUrl())
        {
            HostedUrlBox.Focus();
            return;
        }

        BeginBusy();
        var wantAutoCheck = HostedAutoCheck.IsChecked == true;
        try
        {
            SetFooter("Fetching " + url + "…");
            var fetched = await _services.FetchHostedConfigAsync(url);
            if (!fetched.Success || fetched.Text is null || fetched.Hash is null)
            {
                SetFooter("Import failed.");
                MessageBox.Show(this, "The config could not be fetched:\n\n" + fetched.Error, "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var hash = fetched.Hash;
            if (string.Equals(hash, _services.Settings.HostedConfigLastHash, StringComparison.OrdinalIgnoreCase))
            {
                const string unchanged = ConfigMessages.HostedConfigUnchanged;
                _services.Log.Write(_services.Clock.UtcNow, "Hosted config unchanged since the last import.");
                _services.Notify?.Invoke(unchanged);
                if (!_isClosed)
                    SetFooter(unchanged);
                return;
            }

            var attempt = await _imports.ImportTextAsync(fetched.Text, "Import from " + new Uri(url).Host, ignoreHostedUrl: true, userJustActed: true);
            if (attempt.Outcome != ImportOutcome.Imported)
            {
                SetFooter(attempt.Message);
                return;
            }

            var remembered = _services.UpdateSettings(
                s => s with { HostedConfigUrl = url, HostedConfigLastHash = hash, HostedConfigAutoCheck = wantAutoCheck });
            LoadSettingsControls();
            SetFooter(remembered.Success
                ? attempt.Message
                : attempt.Message + " (the address could not be saved: " + remembered.Error + ")");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Import from URL failed: " + ex);
            if (!_isClosed)
                SetFooter("Import failed: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        BeginBusy();
        try
        {
            if (Snapshot() != _baseline)
            {
                var answer = MessageBox.Show(
                    this,
                    "You have unsaved changes to your lists.\n\nYes: save the lists, then export.\nNo: export the last saved version.\nCancel: stop.",
                    "IRIS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel)
                    return;
                if (answer == MessageBoxResult.Yes && !await SaveWithGuardAsync(SaveScope.ListsOnly))
                    return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export IRIS config",
                Filter = ConfigFileFilter,
                FileName = "iris-config.txt",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            if (_services.Paths.IsOwnConfigFile(dialog.FileName))
            {
                SetFooter("Export cancelled.");
                MessageBox.Show(
                    this,
                    "That is one of IRIS's own files (" + Path.GetFileName(dialog.FileName) + "), and exporting onto it would overwrite your "
                    + "current " + (string.Equals(Path.GetFileName(dialog.FileName), DataPaths.SettingsFileName, StringComparison.OrdinalIgnoreCase)
                        ? "settings" : "lists")
                    + ".\n\nNothing was exported. Choose another file name or folder.",
                    "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = ConfigImporter.Export(dialog.FileName, _services.CurrentConfig);
            if (result.Success)
            {
                SetFooter("Exported to " + Path.GetFileName(dialog.FileName));
            }
            else
            {
                SetFooter("Export failed.");
                MessageBox.Show(this, result.Error ?? "The file could not be written.", "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Export failed: " + ex);
            if (!_isClosed)
                SetFooter("Export failed: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    public Window DialogOwner => this;

    public bool IsBusy => _busyCount > 0;

    public bool IsIdleForPrompt => IsIdle();

    public bool HasUnsavedChanges(bool ignoreHostedUrl)
    {
        if (Snapshot() != _baseline)
            return true;

        var shown = BuildSettings();
        var saved = _services.Settings;
        if (ignoreHostedUrl)
            shown = shown with { HostedConfigUrl = saved.HostedConfigUrl, HostedConfigAutoCheck = saved.HostedConfigAutoCheck };
        return shown != saved;
    }

    public Task<bool> SaveChangesAsync(bool ignoreHostedUrl) =>
        SaveWithGuardAsync(ignoreHostedUrl ? SaveScope.ListsAndSettingsKeepHostedUrl : SaveScope.ListsAndSettings);

    private async Task<bool> SaveWithGuardAsync(SaveScope scope)
    {
        if (_saving)
            return false;
        _saving = true;
        try
        {
            return await SaveAfterChecksAsync(scope, allowWhileBusy: true);
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                SetFooter("Not saved.");
                MessageBox.Show(this, "Your changes could not be saved:\n\n" + ex.Message, "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    public void BeginBusy()
    {
        _busyCount++;
        ApplyBusy();
    }

    public void EndBusy()
    {
        if (_busyCount > 0)
            _busyCount--;
        ApplyBusy();
    }

    private void ApplyBusy()
    {
        var idle = _busyCount == 0;
        ImportFileButton.IsEnabled = idle;
        ImportUrlButton.IsEnabled = idle;
        ExportButton.IsEnabled = idle;
        UpdateSaveButton();
    }

    public void ReloadAfterImport(string status)
    {
        if (_isClosed)
            return;

        LoadRowsFrom(_services.CurrentConfig);
        LoadSettingsControls();
        _baseline = Snapshot();
        RefreshStateUi();
        SetFooter(status);
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
