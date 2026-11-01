using System.IO.Compression;

namespace Iris.Core.Updates;

public sealed record UpdateInstallResult(bool Success, string? Error, int Files)
{
    public static UpdateInstallResult Failed(string error) => new(false, error, 0);
}

public static class UpdateInstaller
{
    public const string ZipRoot = "IRIS";

    public const string OldSuffix = ".old";

    private const string CachePrefix = "cache.db";

    private const string DataFolder = "Data";

    public static readonly IReadOnlyList<string> KeptFolders = new[] { "Config", "Logs" };

    public static string? Unpack(string zipPath, string extractFolder, string exeName, out string? error)
    {
        try
        {
            if (Directory.Exists(extractFolder))
                Directory.Delete(extractFolder, recursive: true);
            Directory.CreateDirectory(extractFolder);
            ZipFile.ExtractToDirectory(zipPath, extractFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            error = "the zip could not be unpacked: " + ex.Message;
            return null;
        }

        var root = Path.Combine(extractFolder, ZipRoot);
        if (!File.Exists(Path.Combine(root, exeName)))
        {
            error = $"the zip does not hold {ZipRoot}\\{exeName}";
            return null;
        }
        error = null;
        return root;
    }

    public static IReadOnlyList<string> FilesToInstall(string newRoot)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(newRoot, file);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (KeptFolders.Any(k => string.Equals(k, first, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (Path.GetFileName(relative).StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            files.Add(relative);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static UpdateInstallResult Install(string newRoot, string installFolder)
    {
        var files = FilesToInstall(newRoot);
        if (files.Count == 0)
            return UpdateInstallResult.Failed("the update holds no files");

        var renamed = new List<string>();
        var copied = new List<string>();
        try
        {
            foreach (var relative in files)
            {
                var target = Path.Combine(installFolder, relative);
                var old = target + OldSuffix;
                if (File.Exists(old))
                    File.Delete(old);
                if (File.Exists(target))
                {
                    File.Move(target, old);
                    renamed.Add(target);
                }
            }

            foreach (var relative in files)
            {
                var target = Path.Combine(installFolder, relative);
                var folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                File.Copy(Path.Combine(newRoot, relative), target, overwrite: false);
                copied.Add(target);
            }
            return new UpdateInstallResult(true, null, files.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restoreProblem = RollBack(renamed, copied);
            return UpdateInstallResult.Failed(ex.Message + (restoreProblem is null ? " (nothing was changed)" : " (" + restoreProblem + ")"));
        }
    }

    private static string? RollBack(List<string> renamed, List<string> copied)
    {
        string? problem = null;
        foreach (var target in copied)
        {
            try
            {
                File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem ??= "could not remove " + Path.GetFileName(target) + ": " + ex.Message;
            }
        }
        foreach (var target in renamed)
        {
            try
            {
                if (!File.Exists(target))
                    File.Move(target + OldSuffix, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem ??= "could not restore " + Path.GetFileName(target) + ": " + ex.Message;
            }
        }
        return problem;
    }

    public static int RemoveLeftovers(string installFolder, string stagingFolder)
    {
        var removed = 0;
        try
        {
            if (Directory.Exists(stagingFolder))
                Directory.Delete(stagingFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var leftovers = new List<string>();
        foreach (var folder in new[] { installFolder, Path.Combine(installFolder, DataFolder) })
        {
            try
            {
                if (Directory.Exists(folder))
                    leftovers.AddRange(Directory.EnumerateFiles(folder, "*" + OldSuffix, SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var file in leftovers)
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }
}
