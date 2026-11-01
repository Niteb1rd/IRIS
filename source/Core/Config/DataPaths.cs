namespace Iris.Core.Config;

public sealed class DataPaths
{
    public const string ConfigFolderName = "Config";
    public const string DataFolderName = "Data";
    public const string LogsFolderName = "Logs";
    public const string SocialListFileName = "social-config.txt";
    public const string SettingsFileName = "settings.txt";
    public const string SocialListLastGoodFileName = "social-config.lastgood.txt";
    public const string SettingsLastGoodFileName = "settings.lastgood.txt";
    public const string CacheFileName = "cache.db";
    public const string ExclusionsFileName = "exclusions.txt";

    public static readonly IReadOnlyList<string> NativeLibraryNames = ["OpenCvSharpExtern.dll", "e_sqlite3.dll"];

    private DataPaths(string programFolder)
    {
        ProgramFolder = programFolder;
        ConfigFolder = Path.Combine(programFolder, ConfigFolderName);
        DataFolder = Path.Combine(programFolder, DataFolderName);
        LogsFolder = Path.Combine(programFolder, LogsFolderName);
    }

    public static DataPaths FromBaseDirectory(string exeDirectory)
    {
        if (string.IsNullOrWhiteSpace(exeDirectory))
            throw new ArgumentException("The program folder must not be empty.", nameof(exeDirectory));
        return new DataPaths(exeDirectory);
    }

    public string ProgramFolder { get; }

    public string ConfigFolder { get; }

    public string DataFolder { get; }

    public string LogsFolder { get; }

    public string SocialListFile => Path.Combine(ConfigFolder, SocialListFileName);

    public string SettingsFile => Path.Combine(ConfigFolder, SettingsFileName);

    public string SocialListLastGoodFile => Path.Combine(ConfigFolder, SocialListLastGoodFileName);

    public string SettingsLastGoodFile => Path.Combine(ConfigFolder, SettingsLastGoodFileName);

    public string ExclusionsFile => Path.Combine(ConfigFolder, ExclusionsFileName);

    public string CacheFile => Path.Combine(DataFolder, CacheFileName);

    public bool IsOwnConfigFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full, Path.GetFullPath(SocialListFile), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(full, Path.GetFullPath(SettingsFile), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigFolder);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogsFolder);
    }
}
