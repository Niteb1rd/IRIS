using System.Globalization;
using System.Text;

namespace Iris.Core;

public sealed class FileLog : ILog
{
    public const string FilePrefix = "iris-";
    public const string FileExtension = ".log";
    private const string DateFormat = "yyyy-MM-dd";
    private const string LineTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private string? _lastError;
    private volatile bool _enabled = true;

    public FileLog(string folder, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("The log folder must not be empty.", nameof(folder));
        Folder = folder;
        _clock = clock;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public string Folder { get; }

    public string CurrentFilePath => PathForDay(_clock.UtcNow);

    public string? LastError
    {
        get
        {
            lock (_gate)
                return _lastError;
        }
    }

    public void WriteBlankLine(DateTimeOffset utcTime)
    {
        if (!_enabled)
            return;
        try
        {
            var bytes = Utf8NoBom.GetBytes(Environment.NewLine);
            var path = PathForDay(utcTime);
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
            }
        }
        catch
        {
        }
    }

    public void Write(DateTimeOffset utcTime, string message)
    {
        if (!_enabled)
            return;

        try
        {
            var local = utcTime.ToLocalTime();
            var line = local.ToString(LineTimeFormat, CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine;
            var bytes = Utf8NoBom.GetBytes(line);
            var path = PathForDay(utcTime);

            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
            }
        }
        catch
        {
        }
    }

    public static int DeleteOlderThan(string folder, TimeSpan age, DateTimeOffset now)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(folder))
                return 0;

            var cutoff = now.ToLocalTime().Date - age;
            foreach (var file in Directory.EnumerateFiles(folder, FilePrefix + "*" + FileExtension))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var datePart = name.Substring(FilePrefix.Length);
                    if (!DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        continue;
                    if (day.Date >= cutoff)
                        continue;
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return deleted;
    }

    private string PathForDay(DateTimeOffset utcTime)
    {
        var day = utcTime.ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
        return Path.Combine(Folder, FilePrefix + day + FileExtension);
    }
}
