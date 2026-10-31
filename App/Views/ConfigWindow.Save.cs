
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
    private enum SaveScope
    {
        ListsAndSettings,

        ListsAndSettingsKeepHostedUrl,

        ListsOnly,
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving || _busyCount > 0)
            return;
        _saving = true;
        try
        {
            await SaveAfterChecksAsync(SaveScope.ListsAndSettings, allowWhileBusy: false);
        }
        catch (Exception ex)
        {
            if (!_closing.IsCancellationRequested)
            {
                SetFooter("Not saved.");
                MessageBox.Show(this, "Your changes could not be saved:\n\n" + ex.Message, "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task<bool> SaveAfterChecksAsync(SaveScope scope, bool allowWhileBusy)
    {
        var pending = _checks.Values.Where(t => !t.IsCompleted).ToList();
        if (pending.Count > 0)
        {
            SetFooter("Waiting for name checks…");
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(25), _closing.Token));
            if (_isClosed || _closing.IsCancellationRequested)
                return false;
            SetFooter(string.Empty);
        }

        if (!allowWhileBusy && _busyCount > 0)
        {
            SetFooter("Not saved: an import or export is running. Try again when it is done.");
            return false;
        }

        return SaveCore(scope);
    }

    private bool SaveCore(SaveScope scope)
    {
        if (scope == SaveScope.ListsAndSettings && !ValidateHostedUrl())
        {
            Tabs.SelectedItem = SettingsTab;
            MessageBox.Show(
                this,
                "The hosted config URL is not valid:\n\n" + HostedUrlError.Text + "\n\nFix it or clear the box, then save again.",
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var listsSaved = false;
        var settingsSaved = false;
        var keptBroken = new List<string>();

        var listsEdited = Snapshot() != _baseline;
        if (listsEdited || _services.ListFileHasErrors)
        {
            if (SaveListsCore())
                listsSaved = true;
            else if (listsEdited)
                return false;
            else
                keptBroken.Add(DataPaths.SocialListFileName);
        }

        if (scope != SaveScope.ListsOnly)
        {
            var settings = BuildSettings();
            if (scope == SaveScope.ListsAndSettingsKeepHostedUrl)
                settings = settings with
                {
                    HostedConfigUrl = _services.Settings.HostedConfigUrl,
                    HostedConfigAutoCheck = _services.Settings.HostedConfigAutoCheck,
                };

            var settingsEdited = settings != _services.Settings;
            if (settingsEdited || _services.SettingsFileNeedsSave)
            {
                if (KeepCopyOfInvalidSettingsFile() != CopyOutcome.Proceed)
                {
                    if (settingsEdited)
                    {
                        SetFooter("Settings not saved.");
                        return false;
                    }
                    keptBroken.Add(DataPaths.SettingsFileName);
                }
                else
                {
                    var savedSettings = _services.SaveSettings(settings);
                    if (!savedSettings.Success)
                    {
                        MessageBox.Show(this, (savedSettings.Error ?? "The settings could not be saved.")
                            + (listsSaved ? "\n\nYour lists were saved, the settings were not." : "\n\nThe settings were not saved."),
                            "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
                        SetFooter("Settings not saved.");
                        return false;
                    }
                    settingsSaved = true;
                }
            }
        }

        var stamp = "Saved at " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (keptBroken.Count == 0)
            SetFooter(stamp);
        else
            SetFooter((listsSaved || settingsSaved ? stamp + ". " : "Nothing saved. ")
                      + "Not replaced (still has errors): " + string.Join(", ", keptBroken) + ".");
        RefreshStateUi();
        return true;
    }

    private bool SaveListsCore()
    {
        var problems = new List<string>();
        CollectNameProblems("Players", _players, RowValidator.CheckHandleFormat, problems);
        CollectNameProblems("Orgs", _orgs, RowValidator.CheckSidFormat, problems);
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.NeedsValue && rule.Value.Trim().Length == 0)
                problems.Add($"Org-Attribute, row {i + 1}: the {Choices.DisplayName(rule.Kind)} rule needs a value.");
            else if (!rule.IsValueAcceptable)
                problems.Add($"Org-Attribute, row {i + 1}: \"{rule.Value}\" is not a known {Choices.DisplayName(rule.Kind)} value. Pick one from the list.");
        }

        if (problems.Count > 0)
        {
            MessageBox.Show(
                this,
                "These rows must be fixed before saving:\n\n" + string.Join("\n", problems.Take(15))
                + (problems.Count > 15 ? $"\n...and {problems.Count - 15} more." : string.Empty),
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var nameRows = NameRows().ToList();
        var checking = nameRows.Count(r => r.StatusKind == RowStatusKind.Checking);
        if (checking > 0)
        {
            MessageBox.Show(
                this,
                $"{Plural(checking, "name is", "names are")} still being checked. Wait a moment and press Save again.",
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var missing = _players.Where(r => r.StatusKind == RowStatusKind.NotFound).Select(r => "Player: " + r.Value.Trim())
            .Concat(_orgs.Where(r => r.StatusKind == RowStatusKind.NotFound).Select(r => "Org: " + r.Value.Trim()))
            .ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(
                this,
                "These names were not found on RSI and must be corrected or removed before saving:\n\n"
                + string.Join("\n", missing.Take(15))
                + (missing.Count > 15 ? $"\n...and {missing.Count - 15} more." : string.Empty),
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var failed = nameRows.Count(r => r.StatusKind == RowStatusKind.Failed);
        if (failed > 0)
        {
            var answer = MessageBox.Show(
                this, $"{Plural(failed, "name", "names")} could not be checked (RSI not reachable?).\n\nSave anyway?",
                "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return false;
        }

        if (KeepCopyOfInvalidListFile() != CopyOutcome.Proceed)
            return false;

        var config = BuildConfig();
        var saved = SocialListStore.Save(_services.Paths.SocialListFile, config, _services.ListFile);
        if (!saved.Success)
        {
            MessageBox.Show(this, saved.Error ?? "The list could not be saved.", "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooter("Not saved.");
            return false;
        }

        _services.SetCurrentConfig(config);
        _baseline = Snapshot();
        RefreshStateUi();
        return true;
    }

    private enum CopyOutcome
    {
        Proceed,

        Declined,

        Failed,
    }

    private CopyOutcome KeepCopyOfInvalidListFile()
    {
        if (!_services.ListFileHasErrors)
            return CopyOutcome.Proceed;

        return KeepCopyOfInvalidFile(
            _services.Paths.SocialListFile, _services.ListFileErrorHash ?? "unreadable", ref _copiedInvalidKey,
            copyName =>
                $"Your list file ({DataPaths.SocialListFileName}) has {Plural(_services.ListFileErrors.Count, "problem", "problems")}.\n\n"
                + "Saving replaces the file with what you see in this window. Entries in the file that could not be read will be dropped.\n\n"
                + "A copy of the broken file is kept as:\n" + copyName + "\n\nSave now?");
    }

    private CopyOutcome KeepCopyOfInvalidSettingsFile()
    {
        if (!_services.SettingsFileHasErrors)
            return CopyOutcome.Proceed;

        return KeepCopyOfInvalidFile(
            _services.Paths.SettingsFile, _services.SettingsFileErrorHash ?? "unreadable", ref _copiedInvalidSettingsKey,
            copyName =>
                $"{DataPaths.SettingsFileName} has errors. Replace it with the settings shown here? A copy of the broken file is kept.\n\n"
                + "The copy: " + copyName);
    }

    private CopyOutcome KeepCopyOfInvalidFile(string file, string key, ref string? copiedKey, Func<string, string> question)
    {
        if (copiedKey == key)
            return CopyOutcome.Proceed;

        if (!File.Exists(file))
        {
            copiedKey = key;
            return CopyOutcome.Proceed;
        }

        var copy = Path.Combine(Path.GetDirectoryName(file) ?? string.Empty,
            Path.GetFileNameWithoutExtension(file) + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(file));
        var answer = MessageBox.Show(
            this, question(Path.GetFileName(copy)),
            "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return CopyOutcome.Declined;

        try
        {
            File.Copy(file, copy, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "A copy of the original file could not be made, so nothing was saved:\n\n" + ex.Message,
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooter("Not saved.");
            return CopyOutcome.Failed;
        }

        copiedKey = key;
        return CopyOutcome.Proceed;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busyCount > 0)
        {
            e.Cancel = true;
            return;
        }
        if (!HasUnsavedChanges(ignoreHostedUrl: false))
            return;
        _closePromptOpen = true;
        try
        {
            var answer = MessageBox.Show(
                this, "You have unsaved changes. Discard them?",
                "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private SocialConfig BuildConfig()
    {
        var config = new SocialConfig { Name = _listName };
        foreach (var p in _players)
            config.Players.Add(new PlayerEntry(p.Value.Trim(), p.Color));
        foreach (var o in _orgs)
            config.Orgs.Add(new OrgEntry(o.Value.Trim(), o.Priority, o.Color));
        foreach (var r in _rules.Where(rule => !rule.IsNoMatch))
            config.OrgRules.Add(new OrgAttributeRule(r.Kind, r.NeedsValue ? r.Value.Trim() : null, r.Priority, r.Color));
        var noMatch = _rules.FirstOrDefault(rule => rule.IsNoMatch);
        config.OrgRules.Add(new OrgAttributeRule(
            AttributeKind.NoMatch, null, NoMatchRule.Priority, noMatch?.Color ?? NoMatchRule.DefaultColor));
        return config;
    }

    private string Snapshot()
    {
        var lines = new List<string>();
        lines.AddRange(_players.Select(p => $"P|{p.Value.Trim()}|{p.Color}"));
        lines.AddRange(_orgs.Select(o => $"O|{o.Value.Trim()}|{o.Priority}|{o.Color}"));
        lines.AddRange(_rules.Select(r => $"R|{r.Kind}|{(r.NeedsValue ? r.Value.Trim() : string.Empty)}|{r.Priority}|{r.Color}"));
        return string.Join("\n", lines);
    }

    private IEnumerable<ListRowViewModel> NameRows()
    {
        foreach (var p in _players)
            yield return p;
        foreach (var o in _orgs)
            yield return o;
    }

    private static void CollectNameProblems(
        string tab, IEnumerable<ListRowViewModel> rows, Func<string?, RowCheckResult?> checkFormat, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int number = 0;
        foreach (var row in rows)
        {
            number++;
            var text = row.Value.Trim();
            if (text.Length == 0)
            {
                problems.Add($"{tab}, row {number}: empty (fill it in or remove the row).");
                continue;
            }

            var bad = checkFormat(text);
            if (bad is not null)
                problems.Add($"{tab}, row {number}: \"{text}\" - {bad.Message}.");
            else if (!seen.Add(text))
                problems.Add($"{tab}, row {number}: \"{text}\" is already in the list.");
        }
    }
}
