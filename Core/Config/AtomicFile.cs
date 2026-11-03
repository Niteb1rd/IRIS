using System.Text;

namespace Iris.Core.Config;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string BackupPathOf(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + ".bak" + Path.GetExtension(path));

    public static void WriteText(string path, string text, bool keepBackup)
    {
        var temp = path + ".tmp";
        var backup = BackupPathOf(path);
        var replacing = false;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            replacing = File.Exists(path);
            if (replacing)
                ReplaceExisting(temp, path, backup, keepBackup);
            else
                File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (keepBackup && replacing)
                TryRestoreFromBackup(path, backup);
            TryDelete(temp);
            throw;
        }
    }

    private static void ReplaceExisting(string temp, string path, string backup, bool keepBackup)
    {
        try
        {
            File.Replace(temp, path, keepBackup ? backup : null);
        }
        catch (PlatformNotSupportedException)
        {
            if (keepBackup)
                File.Copy(path, backup, overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
    }

    private static void TryRestoreFromBackup(string path, string backup)
    {
        try
        {
            if (!File.Exists(path) && File.Exists(backup))
                File.Copy(backup, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
