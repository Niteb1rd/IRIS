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
                Tell(owner, "This config cannot be imported. Nothing was changed.\n\n" + BulletList(read.Errors), MessageBoxImage.Warning,
                    headline, userJustActed);
                _services.Log.Write(_services.Clock.UtcNow, ConfigMessages.ImportRejected(read.Errors));
                return new ImportAttempt(ImportOutcome.Rejected, "Import failed: the config has problems.");
            }

            if (read.Warnings.Count > 0)
            {
                var answer = Ask(owner,
                    "The config has some notes:\n\n" + BulletList(read.Warnings) + "\n\nContinue with the import?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, headline, userJustActed);
                if (answer != MessageBoxResult.Yes)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
            }

            if (host is not null && host.HasUnsavedChanges(ignoreHostedUrl))
            {
                var answer = Ask(owner,
                    "You have unsaved changes in the Settings window.\n\n"
                    + "Yes: save them, then import.\n"
                    + "No: discard your changes and import.\n"
                    + "Cancel: stop; nothing is imported.",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel, headline, userJustActed);
                if (answer != MessageBoxResult.Yes && answer != MessageBoxResult.No)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
                if (answer == MessageBoxResult.Yes && !await host.SaveChangesAsync(ignoreHostedUrl))
                    return new ImportAttempt(ImportOutcome.NotSaved, "Import cancelled: your changes were not saved.");
            }

            var hasExisting = File.Exists(_services.Paths.SocialListFile);
            var mode = ImportChoiceDialog.Ask(owner, read.Config, hasExisting, headline, _services.Notify, userJustActed);
            if (mode is null)
                return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");

            var result = _services.ApplyImport(read.Config, mode.Value);
            if (!result.Success)
            {
                Tell(owner, "The config was not imported. Nothing was changed.\n\n" + result.Error, MessageBoxImage.Error, headline, userJustActed);
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

    private void Tell(Window? owner, string text, MessageBoxImage icon, string balloonText, bool userJustActed) =>
        Ask(owner, text, MessageBoxButton.OK, icon, MessageBoxResult.OK, balloonText, userJustActed);

    private MessageBoxResult Ask(
        Window? owner, string text, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult,
        string balloonText, bool userJustActed)
    {
        if (owner is not null && DialogHelper.PrepareOwner(owner))
            return MessageBox.Show(owner, text, "IRIS", buttons, icon, defaultResult);

        switch (buttons)
        {
            case MessageBoxButton.OK:
                PromptDialog.Tell(text, balloonText, _services.Notify, owner: null, userJustActed: userJustActed);
                return MessageBoxResult.OK;
            case MessageBoxButton.YesNo:
                return PromptDialog.Ask(
                    text, balloonText, _services.Notify, owner: null, defaultYes: defaultResult == MessageBoxResult.Yes, userJustActed: userJustActed)
                    ? MessageBoxResult.Yes
                    : MessageBoxResult.No;
            default:
                return MessageBox.Show(text, "IRIS", buttons, icon, defaultResult);
        }
    }
}
