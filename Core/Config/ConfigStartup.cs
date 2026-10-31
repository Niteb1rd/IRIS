
namespace Iris.Core.Config;

public sealed class SocialListStartup
{
    public SocialListStartup(
        SocialListLoadResult load, bool usedLastGood, DateTimeOffset? lastGoodTime, IReadOnlyList<string> mainFileErrors, string? mainFileHash)
    {
        Load = load;
        UsedLastGood = usedLastGood;
        LastGoodTime = lastGoodTime;
        MainFileErrors = mainFileErrors;
        MainFileHash = mainFileHash;
    }

    public SocialListLoadResult Load { get; }

    public bool UsedLastGood { get; }

    public DateTimeOffset? LastGoodTime { get; }

    public IReadOnlyList<string> MainFileErrors { get; }

    public string? MainFileHash { get; }

    public bool MainFileHasErrors => MainFileErrors.Count > 0;
}

public sealed class SettingsStartup
{
    public SettingsStartup(
        AppSettingsLoadResult load, bool usedLastGood, DateTimeOffset? lastGoodTime, IReadOnlyList<string> mainFileErrors, string? mainFileHash)
    {
        Load = load;
        UsedLastGood = usedLastGood;
        LastGoodTime = lastGoodTime;
        MainFileErrors = mainFileErrors;
        MainFileHash = mainFileHash;
    }

    public AppSettingsLoadResult Load { get; }

    public bool UsedLastGood { get; }

    public DateTimeOffset? LastGoodTime { get; }

    public IReadOnlyList<string> MainFileErrors { get; }

    public string? MainFileHash { get; }

    public bool MainFileHasErrors => MainFileErrors.Count > 0;
}

public static class ConfigStartup
{
    public const string FileInUseMessage = "The file could not be read (another program may have it open).";

    public static SocialListStartup LoadSocialList(WatchedFile file)
    {
        var read = file.Detector.Load();
        if (read.Kind == FileChangeKind.Missing)
        {
            file.AppliedHash = null;
            return new SocialListStartup(
                new SocialListLoadResult(new ConfigLoadResult { Config = SocialListStore.NewConfig() }, isNew: true),
                usedLastGood: false, lastGoodTime: null, Array.Empty<string>(), mainFileHash: null);
        }

        ConfigLoadResult parsed;
        if (read.Kind == FileChangeKind.Changed)
        {
            parsed = SocialConfigToml.Read(read.Text!);
            if (parsed.IsValid)
            {
                file.WriteLastGood(read.Text!);
                file.AppliedHash = read.Hash;
                return new SocialListStartup(new SocialListLoadResult(parsed, isNew: false), false, null, Array.Empty<string>(), null);
            }
        }
        else
        {
            parsed = new ConfigLoadResult();
            parsed.Errors.Add(FileInUseMessage);
        }

        var errors = parsed.Errors.ToList();
        var hash = read.Hash;
        file.Log($"{Path.GetFileName(file.Path)} has errors ({errors.Count}); first: {(errors.Count > 0 ? errors[0] : "unknown problem")}");

        var lastGood = file.ReadLastGood();
        if (lastGood is not null)
        {
            var fallback = SocialConfigToml.Read(lastGood.Text);
            if (fallback.IsValid)
            {
                file.AppliedHash = lastGood.Hash;
                file.Log($"Using {Path.GetFileName(file.LastGoodPath)} (from {lastGood.WrittenUtc:yyyy-MM-dd HH:mm} UTC) instead; {Path.GetFileName(file.Path)} was left as it is.");
                return new SocialListStartup(new SocialListLoadResult(fallback, isNew: false), true, lastGood.WrittenUtc, errors, hash);
            }
        }

        return new SocialListStartup(new SocialListLoadResult(parsed, isNew: false), false, null, errors, hash);
    }

    public static SettingsStartup LoadSettings(WatchedFile file)
    {
        var read = file.Detector.Load();
        if (read.Kind == FileChangeKind.Missing)
        {
            var text = DefaultSettings.Text;
            try
            {
                AtomicFile.WriteText(file.Path, text, keepBackup: false);
                file.Wrote(text);
                file.Log($"{Path.GetFileName(file.Path)} was missing; the default settings were written.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                file.AppliedHash = null;
                file.Log($"{Path.GetFileName(file.Path)} was missing and the default settings could not be written ({ex.Message}); they are used anyway.");
            }
            return new SettingsStartup(new AppSettingsLoadResult(AppSettings.Defaults, isNew: true), false, null, Array.Empty<string>(), null);
        }

        AppSettingsLoadResult parsed;
        if (read.Kind == FileChangeKind.Changed)
        {
            parsed = AppSettingsToml.Read(read.Text!);
            if (parsed.IsValid)
            {
                file.WriteLastGood(read.Text!);
                file.AppliedHash = read.Hash;
                return new SettingsStartup(parsed, false, null, Array.Empty<string>(), null);
            }
        }
        else
        {
            parsed = new AppSettingsLoadResult(AppSettings.Defaults, isNew: false);
            parsed.Errors.Add(FileInUseMessage);
        }

        var errors = parsed.Errors.ToList();
        var hash = read.Hash;
        file.Log($"{Path.GetFileName(file.Path)} has errors ({errors.Count}); first: {(errors.Count > 0 ? errors[0] : "unknown problem")}");

        var lastGood = file.ReadLastGood();
        if (lastGood is not null)
        {
            var fallback = AppSettingsToml.Read(lastGood.Text);
            if (fallback.IsValid)
            {
                file.AppliedHash = lastGood.Hash;
                file.Log($"Using {Path.GetFileName(file.LastGoodPath)} (from {lastGood.WrittenUtc:yyyy-MM-dd HH:mm} UTC) instead; {Path.GetFileName(file.Path)} was left as it is.");
                return new SettingsStartup(fallback, true, lastGood.WrittenUtc, errors, hash);
            }
        }

        return new SettingsStartup(parsed, false, null, errors, hash);
    }
}
