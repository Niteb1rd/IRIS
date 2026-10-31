using Iris.Core.Config;

namespace Iris.Core.Detection;

public sealed class ExclusionList
{
    private readonly string _path;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly FileChangeDetector _detector;
    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    private bool _problemLogged;

    private bool _changePending;

    private ExclusionList(string path, IClock clock, ILog log)
    {
        _path = path;
        _clock = clock;
        _log = log;
        _detector = new FileChangeDetector(path);
    }

    public static ExclusionList Load(string path, IClock clock, ILog log)
    {
        var list = new ExclusionList(path, clock, log);
        if (!File.Exists(path))
            list.LogProblemOnce("not found; no names are excluded until the file is there");
        list.ReloadIfChanged();
        return list;
    }

    public bool IsExcluded(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        lock (_gate)
            return _names.Contains(trimmed);
    }

    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_gate)
                return _names.ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _names.Count;
        }
    }

    public bool ReloadIfChanged()
    {
        lock (_gate)
        {
            ReloadCore();
            var changed = _changePending;
            _changePending = false;
            return changed;
        }
    }

    private bool ReloadCore()
    {
        var change = _detector.Check();
        switch (change.Kind)
        {
            case FileChangeKind.Changed:
                SetNames(ParseNames(change.Text!));
                _problemLogged = false;
                _changePending = true;
                return true;

            case FileChangeKind.Missing:
                LogProblemOnce("the file is missing; keeping the list from before");
                return false;

            case FileChangeKind.Locked:
                LogProblemOnce("could not read the file (another program has it open); keeping the list from before");
                return false;

            default:
                _problemLogged = false;
                return false;
        }
    }

    private void SetNames(HashSet<string> names)
    {
        _names = names;
    }

    private static HashSet<string> ParseNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            names.Add(trimmed);
        }
        return names;
    }

    private void LogProblemOnce(string what)
    {
        if (_problemLogged)
            return;
        _problemLogged = true;
        _log.Write(_clock.UtcNow, "Exclusion list " + _path + ": " + what + ".");
    }
}
