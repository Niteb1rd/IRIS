
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core;
using Iris.Core.Lookup;
using Iris.Core.Config;
using Iris.Core.Detection;

namespace Iris.App.Services;

public sealed partial class AppServices
{
    public ConfigPoll PollConfigFiles()
    {
        lock (_pollGate)
            return ConfigPoller.PollAll(ListFile, SettingsFile, Exclusions);
    }

    public ConfigChanges ApplyPoll(ConfigPoll poll)
    {
        var current = ConfigPoller.DropOutdated(poll, ListFile, SettingsFile);
        if (!ReferenceEquals(current, poll))
            Log.Write(Clock.UtcNow, "A check of the config files was overtaken by a save or import; the outdated part was not applied.");
        poll = current;

        var messages = new List<string>();
        var listChanged = false;
        var settingsChanged = false;
        var errorsChanged = false;

        var list = poll.List;
        switch (list.Kind)
        {
            case PollKind.Applied when list.Hash != ListFile.OwnWriteHash:
                CurrentConfig = list.Config!;
                ListLoad = new SocialListLoadResult(list.Load!, isNew: false);
                errorsChanged |= ClearListErrors();
                errorsChanged |= ClearListMissing();
                listChanged = true;
                messages.Add(ConfigMessages.NewConfigLoaded(CurrentConfig));
                Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName} was changed from outside and has been loaded.");
                foreach (var warning in list.Warnings.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName}: {warning}");
                break;

            case PollKind.Applied:
            case PollKind.Restored:
                errorsChanged |= ClearListErrors();
                errorsChanged |= ClearListMissing();
                break;

            case PollKind.Rejected:
                SetListErrors(list.Errors, list.Hash, "IRIS is still using the previous version.");
                ClearListMissing();
                errorsChanged = true;
                messages.Add(ConfigMessages.StillUsingPrevious(DataPaths.SocialListFileName));
                Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName} was changed from outside but has {list.Errors.Count} error(s); still using the previous version.");
                foreach (var error in list.Errors.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName}: {error}");
                break;

            case PollKind.Missing:
                ListFileMissing = true;
                errorsChanged = true;
                messages.Add(ConfigMessages.ListFileMissing(DataPaths.SocialListFileName));
                Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName} is missing; the list in memory stays in use (saving writes the file again).");
                break;
        }

        var settings = poll.Settings;
        switch (settings.Kind)
        {
            case PollKind.Applied when settings.Hash != SettingsFile.OwnWriteHash:
                ApplySettings(settings.Settings!);
                errorsChanged |= ClearSettingsErrors();
                settingsChanged = true;
                messages.Add(ConfigMessages.NewSettingsLoaded);
                Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName} was changed from outside and has been loaded.");
                foreach (var warning in settings.Warnings.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName}: {warning}");
                break;

            case PollKind.Applied:
            case PollKind.Restored:
                errorsChanged |= ClearSettingsErrors();
                break;

            case PollKind.Rejected:
                SetSettingsErrors(settings.Errors, settings.Hash, "IRIS is still using the previous settings.");
                errorsChanged = true;
                messages.Add(ConfigMessages.StillUsingPrevious(DataPaths.SettingsFileName));
                Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName} was changed from outside but has {settings.Errors.Count} error(s); still using the previous settings.");
                foreach (var error in settings.Errors.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName}: {error}");
                break;

            case PollKind.Missing:
                Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName} is missing; the settings in memory stay in use (saving writes the file again).");
                break;
        }

        var exclusionsChanged = poll.ExclusionsReloaded;
        if (exclusionsChanged)
        {
            messages.Add(ConfigMessages.ExclusionsReloaded(poll.ExclusionCount));
            Log.Write(Clock.UtcNow, $"Exclusion list reloaded from {DataPaths.ExclusionsFileName} ({poll.ExclusionCount} names).");
        }

        var changes = new ConfigChanges(listChanged, settingsChanged, exclusionsChanged, errorsChanged, messages);
        if (changes.Message is { } text)
            Notify?.Invoke(text);
        if (listChanged)
            ListApplied?.Invoke(this, EventArgs.Empty);
        return changes;
    }

    private void SetListErrors(IReadOnlyList<string> errors, string? hash, string note)
    {
        ListFileErrors = errors;
        ListFileErrorHash = hash;
        ListFileNote = note;
    }

    private bool ClearListErrors()
    {
        var had = ListFileHasErrors;
        ListFileErrors = Array.Empty<string>();
        ListFileErrorHash = null;
        ListFileNote = string.Empty;
        return had;
    }

    private bool ClearListMissing()
    {
        var had = ListFileMissing;
        ListFileMissing = false;
        return had;
    }

    private void SetSettingsErrors(IReadOnlyList<string> errors, string? hash, string note)
    {
        SettingsFileErrors = errors;
        SettingsFileErrorHash = hash;
        SettingsFileNote = note;
    }

    private bool ClearSettingsErrors()
    {
        var had = SettingsFileHasErrors;
        SettingsFileErrors = Array.Empty<string>();
        SettingsFileErrorHash = null;
        SettingsFileNote = string.Empty;
        return had;
    }

    private static string FormatTime(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
