using System.Text;

namespace Iris.Core.Config;

public sealed class SocialListLoadResult
{
    public SocialListLoadResult(ConfigLoadResult result, bool isNew)
    {
        Result = result;
        IsNew = isNew;
    }

    public ConfigLoadResult Result { get; }

    public bool IsNew { get; }

    public SocialConfig Config => Result.Config ?? SocialListStore.NewConfig();

    public IReadOnlyList<string> Errors => Result.Errors;

    public IReadOnlyList<string> Warnings => Result.Warnings;

    public bool IsValid => IsNew || Result.IsValid;
}

public sealed class SaveResult
{
    private SaveResult(bool success, string? error)
    {
        Success = success;
        Error = error;
    }

    public bool Success { get; }

    public string? Error { get; }

    public static SaveResult Ok() => new(true, null);

    public static SaveResult Failed(string error) => new(false, error);
}

public static class SocialListStore
{
    public static SocialConfig NewConfig()
    {
        var config = new SocialConfig();
        NoMatchRule.Ensure(config);
        return config;
    }

    public static SocialListLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new SocialListLoadResult(new ConfigLoadResult { Config = NewConfig() }, isNew: true);

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var unreadable = new ConfigLoadResult();
            unreadable.Errors.Add("The file could not be read: " + ex.Message);
            return new SocialListLoadResult(unreadable, isNew: false);
        }

        return new SocialListLoadResult(SocialConfigToml.Read(text), isNew: false);
    }

    public static SaveResult Save(string path, SocialConfig config, WatchedFile? watched = null)
    {
        var text = SocialConfigToml.Write(config);

        var check = SocialConfigToml.Read(text);
        if (!check.IsValid)
            return SaveResult.Failed("The list is not valid and was not saved: " + string.Join(" ", check.Errors));

        try
        {
            AtomicFile.WriteText(path, text, keepBackup: true);
            watched?.Wrote(text);
            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveResult.Failed("The list could not be saved: " + ex.Message);
        }
    }
}
