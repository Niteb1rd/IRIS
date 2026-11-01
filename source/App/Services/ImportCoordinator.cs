using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Views;
using Iris.Core.Config;

namespace Iris.App.Services;

public interface IImportHost
{
    Window DialogOwner { get; }

    bool HasUnsavedChanges(bool ignoreHostedUrl);

    Task<bool> SaveChangesAsync(bool ignoreHostedUrl);

    bool IsBusy { get; }

    bool IsIdleForPrompt { get; }

    void BeginBusy();

    void EndBusy();

    void ReloadAfterImport(string status);
}

public sealed record ImportAttempt(ImportOutcome Outcome, string Message);

public sealed class ImportCoordinator
{
    private const int MaxListed = 15;

    private readonly AppServices _services;

    private bool _active;

    private int _questions;

    public ImportCoordinator(AppServices services)
    {
        _services = services;
    }

    public IImportHost? Host { get; set; }

    public bool IsBusy => _active || _questions > 0;

    public bool TryBeginQuestion()
    {
        if (IsQuitting || IsBusy)
            return false;
        _questions++;
        return true;
    }

    public void EndQuestion()
    {
        if (_questions > 0)
            _questions--;
    }

    public bool IsQuitting { get; set; }

    public async Task<ImportAttempt> ImportTextAsync(string text, string headline, bool ignoreHostedUrl, bool userJustActed = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var inner = await dispatcher.InvokeAsync<Task<ImportAttempt>>(
                () => ImportTextAsync(text, headline, ignoreHostedUrl, userJustActed)).Task;
            return await inner;
        }

        if (IsQuitting)
            return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled: IRIS is quitting.");
        if (_active || _questions > 0)
            return new ImportAttempt(ImportOutcome.Busy, "Another import is already running.");

        _active = true;
        var host = Host;
        var owner = host?.DialogOwner;
        try
        {
            host?.BeginBusy();

            var read = ConfigImporter.ParseForImport(text);
            if (!read.IsValid || read.Config is null)
            {
                Tell(owner, "Not imported:\n\n" + BulletList(read.Errors), MessageBoxImage.Warning, userJustActed);
                _services.Log.Write(_services.Clock.UtcNow, ConfigMessages.ImportRejected(read.Errors));
                return new ImportAttempt(ImportOutcome.Rejected, "Import failed: the config has problems.");
            }

            if (read.Warnings.Count > 0)
            {
                var answer = Ask(owner,
                    "The config has notes:\n\n" + BulletList(read.Warnings) + "\n\nImport anyway?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, userJustActed);
                if (answer != MessageBoxResult.Yes || IsQuitting)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
            }

            if (host is not null && host.HasUnsavedChanges(ignoreHostedUrl))
            {
                var answer = Ask(owner,
                    "Unsaved changes in Settings.\n\nYes: save, then import.\nNo: discard them and import.",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel, userJustActed);
                if ((answer != MessageBoxResult.Yes && answer != MessageBoxResult.No) || IsQuitting)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
                if (answer == MessageBoxResult.Yes && !await host.SaveChangesAsync(ignoreHostedUrl))
                    return new ImportAttempt(ImportOutcome.NotSaved, "Import cancelled: your changes were not saved.");
                if (IsQuitting)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled: IRIS is quitting.");
            }

            var hasExisting = File.Exists(_services.Paths.SocialListFile);
            var mode = ImportChoiceDialog.Ask(owner, read.Config, hasExisting, headline, userJustActed);
            if (mode is null || IsQuitting)
                return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");

            var result = _services.ApplyImport(read.Config, mode.Value);
            if (!result.Success)
            {
                Tell(owner, "Not imported: " + result.Error, MessageBoxImage.Error, userJustActed);
                return new ImportAttempt(ImportOutcome.WriteFailed, "Import failed: " + result.Error);
            }

            Host?.ReloadAfterImport(result.Summary);
            return new ImportAttempt(ImportOutcome.Imported, result.Summary);
        }
        finally
        {
            host?.EndBusy();
            _active = false;
        }
    }

    private static string BulletList(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines.Take(MaxListed))
            text.AppendLine("  - " + line);
        if (lines.Count > MaxListed)
            text.AppendLine($"  ...and {lines.Count - MaxListed} more.");
        return text.ToString().TrimEnd();
    }

    private static void Tell(Window? owner, string text, MessageBoxImage icon, bool userJustActed) =>
        Ask(owner, text, MessageBoxButton.OK, icon, MessageBoxResult.OK, userJustActed);

    private static MessageBoxResult Ask(
        Window? owner, string text, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult, bool userJustActed)
    {
        if (owner is not null && DialogHelper.PrepareOwner(owner))
            return MessageBox.Show(owner, text, "IRIS", buttons, icon, defaultResult);

        switch (buttons)
        {
            case MessageBoxButton.OK:
                PromptDialog.Tell(text, owner: null, userJustActed: userJustActed);
                return MessageBoxResult.OK;
            case MessageBoxButton.YesNo:
                return PromptDialog.Ask(text, owner: null, defaultYes: defaultResult == MessageBoxResult.Yes, userJustActed: userJustActed)
                    ? MessageBoxResult.Yes
                    : MessageBoxResult.No;
            default:
                return MessageBox.Show(text, "IRIS", buttons, icon, defaultResult);
        }
    }
}
