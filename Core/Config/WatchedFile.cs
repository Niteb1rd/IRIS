using System.Text;

namespace Iris.Core.Config;

public sealed record LastGoodCopy(string Text, DateTimeOffset WrittenUtc, string Hash);

public sealed class WatchedFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IClock _clock;
    private readonly ILog _log;

    private readonly object _lastGoodGate = new();

    private readonly object _hashGate = new();
    private string? _appliedHash;
    private string? _ownWriteHash;

    public WatchedFile(string path, string lastGoodPath, IClock clock, ILog? log = null, FileChangeDetector? detector = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path must not be empty.", nameof(path));
        if (string.IsNullOrWhiteSpace(lastGoodPath))
            throw new ArgumentException("The last-good path must not be empty.", nameof(lastGoodPath));
        Path = path;
        LastGoodPath = lastGoodPath;
        _clock = clock;
        _log = log ?? NullLog.Instance;
        Detector = detector ?? new FileChangeDetector(path);
    }

    public string Path { get; }

    public string LastGoodPath { get; }

    public FileChangeDetector Detector { get; }

    public string? AppliedHash
    {
        get
        {
            lock (_hashGate)
                return _appliedHash;
        }
        set
        {
            lock (_hashGate)
                _appliedHash = value;
        }
    }

    public string? OwnWriteHash
    {
        get
        {
            lock (_hashGate)
                return _ownWriteHash;
        }
    }

    public void Wrote(string text)
    {
        Detector.RememberWritten(text);
        var hash = FileChangeDetector.HashOf(text);
        lock (_hashGate)
        {
            _ownWriteHash = hash;
            _appliedHash = hash;
        }
        WriteLastGood(text);
    }

    public void WriteLastGood(string text)
    {
        lock (_lastGoodGate)
        {
            try
            {
                if (File.Exists(LastGoodPath) && string.Equals(File.ReadAllText(LastGoodPath, Utf8NoBom), text, StringComparison.Ordinal))
                    return;
                AtomicFile.WriteText(LastGoodPath, text, keepBackup: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(_clock.UtcNow, "Could not write " + System.IO.Path.GetFileName(LastGoodPath) + ": " + ex.Message);
            }
        }
    }

    public LastGoodCopy? ReadLastGood()
    {
        try
        {
            if (!File.Exists(LastGoodPath))
                return null;
            var bytes = File.ReadAllBytes(LastGoodPath);
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(LastGoodPath), TimeSpan.Zero);
            return new LastGoodCopy(ConfigImporter.DecodeUtf8(bytes), written, HostedConfigClient.Sha256Hex(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(_clock.UtcNow, "Could not read " + System.IO.Path.GetFileName(LastGoodPath) + ": " + ex.Message);
            return null;
        }
    }

    internal void Log(string message) => _log.Write(_clock.UtcNow, message);
}
