using System.Globalization;
using System.Text;
using Iris.Core.Lookup;

namespace Iris.Core.Config;

public enum ImportMode
{
    BackupExisting,

    Merge,

    Replace,
}

public sealed class ImportResult
{
    private ImportResult()
    {
    }

    public bool Success { get; private init; }

    public string? Error { get; private init; }

    public ImportMode Mode { get; private init; }

    public bool HadExistingFile { get; private init; }

    public SocialConfig? Config { get; private init; }

    public int PlayersAdded { get; private init; }
    public int OrgsAdded { get; private init; }
    public int RulesAdded { get; private init; }

    public int DuplicatesSkipped { get; private init; }

    public string? BackupPath { get; private init; }

    public string Summary
    {
        get
        {
            if (!Success)
                return Error ?? "Import failed.";

            var counts = $"{Count(PlayersAdded, "player")}, {Count(OrgsAdded, "org")}, {Count(RulesAdded, "rule")}";
            if (!HadExistingFile || Mode == ImportMode.BackupExisting)
                return "Imported: " + counts
                    + (BackupPath is null ? string.Empty : " (old file kept as " + Path.GetFileName(BackupPath) + ")");
            if (Mode == ImportMode.Replace)
                return "Imported: " + counts + " (your lists were replaced)";
            return "Imported: " + counts + " added" + (DuplicatesSkipped > 0 ? $", {Count(DuplicatesSkipped, "duplicate")} skipped" : string.Empty);
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    internal static ImportResult Fail(ImportMode mode, string error) => new() { Success = false, Error = error, Mode = mode };

    internal static ImportResult Done(
        ImportMode mode, bool hadExisting, SocialConfig config, int players, int orgs, int rules, int skipped, string? backupPath) =>
        new()
        {
            Success = true,
            Mode = mode,
            HadExistingFile = hadExisting,
            Config = config,
            PlayersAdded = players,
            OrgsAdded = orgs,
            RulesAdded = rules,
            DuplicatesSkipped = skipped,
            BackupPath = backupPath,
        };
}

public sealed class ImportTextResult
{
    private ImportTextResult(string? text, string? error)
    {
        Text = text;
        Error = error;
    }

    public bool Success => Error is null;
    public string? Text { get; }
    public string? Error { get; }

    internal static ImportTextResult Ok(string text) => new(text, null);
    internal static ImportTextResult Failed(string error) => new(null, error);
}

public static class ConfigImporter
{
    private const string BackupTimeFormat = "yyyyMMdd-HHmmss";

    public static ImportResult Import(string listFilePath, SocialConfig imported, ImportMode mode, IClock clock, WatchedFile? watched = null)
    {
        imported = NoMatchRule.Normalized(imported);

        if (!File.Exists(listFilePath))
        {
            var created = SocialListStore.Save(listFilePath, imported, watched);
            return created.Success
                ? ImportResult.Done(mode, hadExisting: false, imported, imported.Players.Count, imported.Orgs.Count, imported.UserRuleCount, 0, null)
                : ImportResult.Fail(mode, created.Error ?? "The imported config could not be saved.");
        }

        switch (mode)
        {
            case ImportMode.Replace:
            {
                var saved = SocialListStore.Save(listFilePath, imported, watched);
                return saved.Success
                    ? ImportResult.Done(mode, true, imported, imported.Players.Count, imported.Orgs.Count, imported.UserRuleCount, 0, null)
                    : ImportResult.Fail(mode, saved.Error ?? "The imported config could not be saved.");
            }

            case ImportMode.Merge:
            {
                var current = SocialListStore.Load(listFilePath);
                if (!current.IsValid)
                    return ImportResult.Fail(mode,
                        "Your current list file has problems, so it cannot be merged safely. Choose \"Backup existing config\" or \"Replace existing config\" instead.");

                var merged = SocialConfigMerger.Merge(current.Config, imported);
                var saved = SocialListStore.Save(listFilePath, merged.Config, watched);
                return saved.Success
                    ? ImportResult.Done(mode, true, merged.Config, merged.AddedPlayers, merged.AddedOrgs, merged.AddedRules, merged.SkippedDuplicates, null)
                    : ImportResult.Fail(mode, saved.Error ?? "The merged config could not be saved.");
            }

            case ImportMode.BackupExisting:
                return ImportWithBackup(listFilePath, imported, clock, watched);

            default:
                return ImportResult.Fail(mode, "Unknown import mode.");
        }
    }

    private static ImportResult ImportWithBackup(string listFilePath, SocialConfig imported, IClock clock, WatchedFile? watched)
    {
        const ImportMode mode = ImportMode.BackupExisting;
        string backupPath;
        try
        {
            backupPath = UniqueBackupPath(listFilePath, clock.UtcNow);
            File.Move(listFilePath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImportResult.Fail(mode, "The current list file could not be backed up, so nothing was changed: " + ex.Message);
        }

        var saved = SocialListStore.Save(listFilePath, imported, watched);
        if (!saved.Success)
        {
            var error = saved.Error ?? "The imported config could not be saved.";
            var safeNote = " Your previous list is safe as " + Path.GetFileName(backupPath) + ".";

            if (File.Exists(listFilePath))
                return ImportResult.Fail(mode, error + safeNote);
            try
            {
                File.Move(backupPath, listFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ImportResult.Fail(mode, error + safeNote);
            }
            return ImportResult.Fail(mode, error);
        }

        return ImportResult.Done(mode, true, imported, imported.Players.Count, imported.Orgs.Count, imported.UserRuleCount, 0, backupPath);
    }

    private static string UniqueBackupPath(string listFilePath, DateTimeOffset utcNow)
    {
        var fullPath = Path.GetFullPath(listFilePath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var stamp = utcNow.ToLocalTime().ToString(BackupTimeFormat, CultureInfo.InvariantCulture);

        var candidate = Path.Combine(directory, $"{baseName}.backup-{stamp}{extension}");
        for (int n = 2; File.Exists(candidate) && n < 1000; n++)
            candidate = Path.Combine(directory, $"{baseName}.backup-{stamp}-{n}{extension}");
        return candidate;
    }

    public static ImportTextResult ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return ImportTextResult.Failed("The file does not exist.");
            if (info.Length > CacheSettings.ConfigMaxFileBytes)
                return ImportTextResult.Failed("The file is too large (over 1 MB). A config file is normally a few KB.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > CacheSettings.ConfigMaxFileBytes)
                    return ImportTextResult.Failed("The file is too large (over 1 MB). A config file is normally a few KB.");
            }
            return ImportTextResult.Ok(DecodeUtf8(buffer.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ImportTextResult.Failed("The file could not be read: " + ex.Message);
        }
    }

    public static SaveResult Export(string path, SocialConfig config)
    {
        try
        {
            var text = SocialConfigToml.Write(config);
            var check = SocialConfigToml.Read(text);
            if (!check.IsValid)
                return SaveResult.Failed("The list is not valid and was not exported: " + string.Join(" ", check.Errors));

            AtomicFile.WriteText(path, text, keepBackup: false);
            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return SaveResult.Failed("The file could not be written: " + ex.Message);
        }
    }

    public static ConfigLoadResult ParseForImport(string text)
    {
        var read = SocialConfigToml.Read(text);
        if (read.Config is not null)
            read.Errors.AddRange(OrgRuleValues.Check(read.Config));
        return read;
    }

    internal static string DecodeUtf8(byte[] bytes)
    {
        var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }
}
