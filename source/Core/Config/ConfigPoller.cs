using System.Globalization;
using Iris.Core.Detection;

namespace Iris.Core.Config;

public enum PollKind
{
    Nothing,

    Applied,

    Restored,

    Rejected,

    Missing,
}

public sealed class SocialListPoll
{
    internal SocialListPoll(PollKind kind, ConfigLoadResult? load = null, string? hash = null, IReadOnlyList<string>? errors = null)
    {
        Kind = kind;
        Load = load;
        Hash = hash;
        Errors = errors ?? Array.Empty<string>();
    }

    public PollKind Kind { get; }

    public ConfigLoadResult? Load { get; }

    public SocialConfig? Config => Kind == PollKind.Applied ? Load?.Config : null;

    public string? Hash { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings => Load?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>();
}

public sealed class SettingsPoll
{
    internal SettingsPoll(PollKind kind, AppSettingsLoadResult? load = null, string? hash = null, IReadOnlyList<string>? errors = null)
    {
        Kind = kind;
        Load = load;
        Hash = hash;
        Errors = errors ?? Array.Empty<string>();
    }

    public PollKind Kind { get; }

    public AppSettingsLoadResult? Load { get; }

    public AppSettings? Settings => Kind == PollKind.Applied ? Load?.Settings : null;

    public string? Hash { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings => Load?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>();
}

public sealed class ConfigPoll
{
    public ConfigPoll(SocialListPoll list, SettingsPoll settings, bool exclusionsReloaded, int exclusionCount)
    {
        List = list;
        Settings = settings;
        ExclusionsReloaded = exclusionsReloaded;
        ExclusionCount = exclusionCount;
    }

    public SocialListPoll List { get; }

    public SettingsPoll Settings { get; }

    public bool ExclusionsReloaded { get; }

    public int ExclusionCount { get; }

    public bool IsEmpty =>
        List.Kind == PollKind.Nothing && Settings.Kind == PollKind.Nothing && !ExclusionsReloaded;
}

public static class ConfigPoller
{
    public static ConfigPoll PollAll(WatchedFile listFile, WatchedFile settingsFile, ExclusionList exclusions)
    {
        var list = PollSocialList(listFile);
        var settings = PollSettings(settingsFile);
        var reloaded = exclusions.ReloadIfChanged();
        return new ConfigPoll(list, settings, reloaded, exclusions.Count);
    }

    public static ConfigPoll DropOutdated(ConfigPoll poll, WatchedFile listFile, WatchedFile settingsFile)
    {
        var list = poll.List;
        if (list.Hash is not null && list.Hash != listFile.Detector.KnownHash)
            list = new SocialListPoll(PollKind.Nothing);

        var settings = poll.Settings;
        if (settings.Hash is not null && settings.Hash != settingsFile.Detector.KnownHash)
            settings = new SettingsPoll(PollKind.Nothing);

        if (ReferenceEquals(list, poll.List) && ReferenceEquals(settings, poll.Settings))
            return poll;
        return new ConfigPoll(list, settings, poll.ExclusionsReloaded, poll.ExclusionCount);
    }

    public static SocialListPoll PollSocialList(WatchedFile file)
    {
        var change = file.Detector.Check();
        switch (change.Kind)
        {
            case FileChangeKind.Missing:
                return new SocialListPoll(PollKind.Missing);

            case FileChangeKind.Changed:
                var parsed = SocialConfigToml.Read(change.Text!);
                if (!parsed.IsValid)
                    return new SocialListPoll(PollKind.Rejected, parsed, change.Hash, parsed.Errors.ToList());

                if (change.Hash == file.OwnWriteHash || change.Hash == file.AppliedHash)
                    return new SocialListPoll(PollKind.Restored, parsed, change.Hash);

                file.WriteLastGood(change.Text!);
                file.AppliedHash = change.Hash;
                return new SocialListPoll(PollKind.Applied, parsed, change.Hash);

            default:
                return new SocialListPoll(PollKind.Nothing);
        }
    }

    public static SettingsPoll PollSettings(WatchedFile file)
    {
        var change = file.Detector.Check();
        switch (change.Kind)
        {
            case FileChangeKind.Missing:
                return new SettingsPoll(PollKind.Missing);

            case FileChangeKind.Changed:
                var parsed = AppSettingsToml.Read(change.Text!);
                if (!parsed.IsValid)
                    return new SettingsPoll(PollKind.Rejected, parsed, change.Hash, parsed.Errors.ToList());

                if (change.Hash == file.OwnWriteHash || change.Hash == file.AppliedHash)
                    return new SettingsPoll(PollKind.Restored, parsed, change.Hash);

                file.WriteLastGood(change.Text!);
                file.AppliedHash = change.Hash;
                return new SettingsPoll(PollKind.Applied, parsed, change.Hash);

            default:
                return new SettingsPoll(PollKind.Nothing);
        }
    }
}

public static class ConfigMessages
{
    public static string NewConfigLoaded(SocialConfig config) =>
        $"New config loaded: {Count(config.Players.Count, "player")}, {Count(config.Orgs.Count, "org")}, {Count(config.UserRuleCount, "rule")}";

    public const string NewSettingsLoaded = "New settings loaded";

    public static string ExclusionsReloaded(int names) => $"Exclusion list reloaded ({Count(names, "name")})";

    public static string StillUsingPrevious(string fileName) =>
        $"{fileName} has errors — still using the previous version. Open the config window for details.";

    public static string UsingLastGood(string fileName, DateTimeOffset lastGoodUtc) =>
        $"{fileName} has errors — using the last good version (from {lastGoodUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})";

    public static string ListFileMissing(string fileName) =>
        $"{fileName} is missing — IRIS keeps using the list it has. Saving writes the file again.";

    public static string ImportRejected(IReadOnlyList<string> problems) =>
        problems.Count == 0
            ? "Import failed: the config has problems."
            : "Import failed: the config has problems: " + string.Join("; ", problems);

    public const string HostedConfigUnchanged = "Your hosted config has not changed.";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
