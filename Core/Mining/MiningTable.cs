using System.Globalization;
using System.Reflection;

namespace Iris.Core.Mining;

public sealed record MiningResource(int Base, string Name, int MaxMultiple);

public sealed class MiningTable
{
    public const string ResourceName = "Iris.Core.Mining.mining.txt";
    public const string VersionPrefix = "# IRIS mining data ";

    private readonly Dictionary<int, string> _labels;

    private MiningTable(IReadOnlyList<MiningResource> resources, IReadOnlyList<string> errors, string? version)
    {
        Resources = resources;
        Errors = errors;
        Version = version;
        var labels = new Dictionary<int, List<string>>();
        foreach (var resource in resources)
        {
            for (var n = 1; n <= resource.MaxMultiple; n++)
            {
                var value = (long)resource.Base * n;
                if (value > int.MaxValue)
                    break;
                if (!labels.TryGetValue((int)value, out var list))
                    labels[(int)value] = list = new List<string>();
                list.Add(string.Create(CultureInfo.InvariantCulture, $"{resource.Name} {n}x"));
            }
        }

        _labels = labels.ToDictionary(p => p.Key, p => string.Join(" / ", p.Value));
    }

    public static MiningTable Empty { get; } = new(Array.Empty<MiningResource>(), Array.Empty<string>(), null);

    public IReadOnlyList<MiningResource> Resources { get; }

    public IReadOnlyList<string> Errors { get; }

    public string? Version { get; }

    public string? Lookup(int value) => _labels.TryGetValue(value, out var label) ? label : null;

    public bool Contains(int value) => _labels.ContainsKey(value);

    public static MiningTable Parse(string text)
    {
        var resources = new List<MiningResource>();
        var errors = new List<string>();
        string? version = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(VersionPrefix, StringComparison.Ordinal))
                version ??= line[VersionPrefix.Length..].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('/');
            if (parts.Length != 3)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"line {i + 1}: expected base signal/name/most multiples"));
                continue;
            }

            var number = parts[0].Trim().Replace(",", string.Empty, StringComparison.Ordinal);
            var name = parts[1].Trim();
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var baseValue) || baseValue <= 0)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"line {i + 1}: \"{parts[0].Trim()}\" is not a signal value"));
                continue;
            }

            if (name.Length == 0)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"line {i + 1}: the name is empty"));
                continue;
            }

            if (!int.TryParse(parts[2].Trim().TrimEnd('x', 'X'), NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max < 1 || max > 99)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"line {i + 1}: \"{parts[2].Trim()}\" is not a number of multiples (1-99)"));
                continue;
            }

            resources.Add(new MiningResource(baseValue, name, max));
        }

        return new MiningTable(resources, errors, version);
    }

    public static string EmbeddedText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The shipped mining table is missing from Iris.Core (" + ResourceName + ").");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly Lazy<MiningTable> EmbeddedTable = new(() => Parse(EmbeddedText()));

    public static MiningTable Embedded => EmbeddedTable.Value;

    public static bool IsOutdated(string? fileText) =>
        fileText is not null && Parse(fileText).Version is { } version && Embedded.Version is { } shipped && !string.Equals(version, shipped, StringComparison.Ordinal);
}

public sealed class MiningTableSource
{
    private const double CheckEveryMs = 1000.0;

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private MiningTable _table = MiningTable.Embedded;
    private DateTime? _stamp;
    private double _checkedMs = double.NegativeInfinity;

    public MiningTableSource(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
    }

    public string FilePath => _path;

    public MiningTable Current(double nowMs)
    {
        lock (_gate)
        {
            if (nowMs - _checkedMs < CheckEveryMs)
                return _table;
            _checkedMs = nowMs;
            DateTime? stamp;
            try
            {
                stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return _table;
            }

            if (stamp == _stamp)
                return _table;
            _stamp = stamp;
            if (stamp is null)
            {
                _table = MiningTable.Embedded;
                return _table;
            }

            try
            {
                var table = MiningTable.Parse(File.ReadAllText(_path));
                _table = table;
                _log?.Invoke(table.Errors.Count == 0
                    ? $"Mining: using Config\\mining.txt ({table.Resources.Count} resources)"
                    : $"Mining: using Config\\mining.txt ({table.Resources.Count} resources); lines left out: " + string.Join("; ", table.Errors.Take(5)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _stamp = null;
                _log?.Invoke("Mining: Config\\mining.txt could not be read (" + ex.Message + "); the previous table stays in use");
            }

            return _table;
        }
    }

    public bool EnsureFile(out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(_path))
                return true;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, MiningTable.EmbeddedText());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
