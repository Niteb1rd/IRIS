using System.Text;

namespace Iris.Core.Config;

public enum FileChangeKind
{
    Unchanged,

    Changed,

    Missing,

    Locked,
}

public sealed class FileChange
{
    private FileChange(FileChangeKind kind, string? text, string? hash)
    {
        Kind = kind;
        Text = text;
        Hash = hash;
    }

    public FileChangeKind Kind { get; }

    public string? Text { get; }

    public string? Hash { get; }

    internal static readonly FileChange NoChange = new(FileChangeKind.Unchanged, null, null);
    internal static readonly FileChange Gone = new(FileChangeKind.Missing, null, null);
    internal static readonly FileChange InUse = new(FileChangeKind.Locked, null, null);

    internal static FileChange Modified(string text, string hash) => new(FileChangeKind.Changed, text, hash);
}

public sealed class FileChangeDetector
{
    public const int RetryCount = 3;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<string, byte[]> _readBytes;

    private DateTime? _writeTime;
    private long _length;
    private string? _hash;
    private bool _missingReported;

    private long _generation;

    public FileChangeDetector(string path, Action<TimeSpan>? sleep = null, Func<string, byte[]>? readBytes = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path must not be empty.", nameof(path));
        Path = path;
        _sleep = sleep ?? (span => Thread.Sleep(span));
        _readBytes = readBytes ?? (file => ReadSharedBytes(file));
    }

    public string Path { get; }

    public string? KnownHash
    {
        get
        {
            lock (_gate)
                return _hash;
        }
    }

    public static string HashOf(string text) => HostedConfigClient.Sha256Hex(Utf8NoBom.GetBytes(text));

    public FileChange Check()
    {
        DateTime time;
        long length;
        long generation;
        lock (_gate)
        {
            if (!TryStat(out var exists, out time, out length))
                return FileChange.InUse;

            if (!exists)
            {
                if (_missingReported)
                    return FileChange.NoChange;
                _missingReported = true;
                return FileChange.Gone;
            }

            _missingReported = false;
            if (_writeTime == time && _length == length)
                return FileChange.NoChange;

            generation = _generation;
        }

        return ReadAndRemember(time, length, generation, forced: false);
    }

    public FileChange Load()
    {
        DateTime time;
        long length;
        long generation;
        lock (_gate)
        {
            if (!TryStat(out var exists, out time, out length))
                return FileChange.InUse;

            if (!exists)
            {
                _writeTime = null;
                _length = 0;
                _hash = null;
                _missingReported = true;
                _generation++;
                return FileChange.Gone;
            }

            _missingReported = false;
            generation = _generation;
        }

        return ReadAndRemember(time, length, generation, forced: true);
    }

    public void RememberWritten(string text) => Remember(HashOf(text));

    private void Remember(string hash)
    {
        lock (_gate)
        {
            _generation++;
            _hash = hash;
            _missingReported = false;
            if (TryStat(out var exists, out var time, out var length) && exists)
            {
                _writeTime = time;
                _length = length;
            }
            else
            {
                _writeTime = null;
            }
        }
    }

    private FileChange ReadAndRemember(DateTime time, long length, long generation, bool forced)
    {
        var bytes = ReadWithRetries();
        if (bytes is null)
            return FileChange.InUse;

        var hash = HostedConfigClient.Sha256Hex(bytes);
        lock (_gate)
        {
            if (generation != _generation)
                return FileChange.NoChange;

            _writeTime = time;
            _length = length;
            if (!forced && _hash == hash)
                return FileChange.NoChange;

            _hash = hash;
            return FileChange.Modified(ConfigImporter.DecodeUtf8(bytes), hash);
        }
    }

    private bool TryStat(out bool exists, out DateTime time, out long length)
    {
        exists = false;
        time = default;
        length = 0;
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
                return true;
            exists = true;
            time = info.LastWriteTimeUtc;
            length = info.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private byte[]? ReadWithRetries()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _readBytes(Path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= RetryCount)
                    return null;
                _sleep(RetryDelay);
            }
        }
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
