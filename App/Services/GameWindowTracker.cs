using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Services;

public sealed record GameWindowSnapshot(GameWindowState State, PixelRect Bounds, int ProcessId, IntPtr Handle, uint Style = 0, PixelRect Monitor = default, WindowMode? Mode = null)
{
    public static readonly GameWindowSnapshot None = new(GameWindowState.GameNotActive, default, 0, IntPtr.Zero);
}

public sealed class GameWindowTracker : IDisposable
{
    public const string GameProcessName = "StarCitizen";

    private const int ProcessNameCacheMaxEntries = 256;

    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<uint, (bool IsGame, long CheckedAtMs)> _processCache = new();
    private readonly Dictionary<uint, GameProcessInfo> _gameInfo = new();

    private const int GameInfoMaxEntries = 16;

    private readonly Dictionary<string, WindowMode?> _modeByFile = new(StringComparer.OrdinalIgnoreCase);

    public GameWindowTracker(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
        _timer = new DispatcherTimer { Interval = LiveSettings.GameCheckInterval };
        _timer.Tick += OnTick;
    }

    public GameWindowSnapshot Current { get; private set; } = GameWindowSnapshot.None;

    public event EventHandler<GameWindowSnapshot>? Changed;

    public event EventHandler<GameWindowSnapshot>? Checked;

    public void Start()
    {
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        GameWindowSnapshot snapshot;
        try
        {
            snapshot = Evaluate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            snapshot = GameWindowSnapshot.None;
        }

        var previous = Current;
        if (snapshot != previous)
        {
            Current = snapshot;
            Changed?.Invoke(this, snapshot);
        }

        Checked?.Invoke(this, Current);
    }

    private GameWindowSnapshot Evaluate()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return GameWindowSnapshot.None;
        if (NativeMethods.GetWindowThreadProcessId(foreground, out var processId) == 0 || processId == 0)
            return GameWindowSnapshot.None;
        if (!IsGameProcess(processId))
            return GameWindowSnapshot.None;

        if (NativeMethods.IsIconic(foreground)
            || !NativeMethods.TryGetWindowBounds(foreground, out var window)
            || !NativeMethods.TryGetMonitorBounds(foreground, out var monitor))
            return GameWindowSnapshot.None;

        var style = NativeMethods.GetStyle(foreground);
        var mode = ReadGameMode(processId);
        var state = GameWindowRules.Decide(true, mode);
        return new GameWindowSnapshot(state, window, (int)processId, foreground, style, monitor, mode);
    }

    private bool IsGameProcess(uint processId)
    {
        var now = Environment.TickCount64;
        if (_processCache.TryGetValue(processId, out var known) && now - known.CheckedAtMs < (known.IsGame ? LiveSettings.GameProcessCacheMs : LiveSettings.OtherProcessCacheMs))
            return known.IsGame;

        var isGame = false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            isGame = string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }

        if (_processCache.Count >= ProcessNameCacheMaxEntries)
            _processCache.Clear();
        _processCache[processId] = (isGame, now);
        if (isGame)
            RefreshGameInfo(processId);
        return isGame;
    }

    private sealed class GameProcessInfo
    {
        public GameProcessInfo(string? exePath) => ExePath = exePath;

        public string? ExePath { get; }
        public string? AttributesPath { get; set; }
        public bool Settled { get; set; }
        public int Attempts { get; set; }
        public long RetryAtMs { get; set; }
        public WindowMode? Mode { get; set; }
    }

    private GameProcessInfo RefreshGameInfo(uint processId)
    {
        var exe = NativeMethods.TryGetProcessImagePath(processId, out var path) ? path : null;
        if (_gameInfo.TryGetValue(processId, out var existing)
            && (exe is null || string.Equals(exe, existing.ExePath, StringComparison.OrdinalIgnoreCase)))
            return existing;

        if (_gameInfo.Count >= GameInfoMaxEntries)
            _gameInfo.Clear();
        var created = new GameProcessInfo(exe);
        _gameInfo[processId] = created;
        return created;
    }

    private WindowMode? ReadGameMode(uint processId)
    {
        if (!_gameInfo.TryGetValue(processId, out var info))
            info = RefreshGameInfo(processId);
        if (info.Settled)
            return info.Mode;

        var now = Environment.TickCount64;
        if (info.Attempts == 1 && now < info.RetryAtMs)
            return null;

        if (info.AttributesPath is null && info.ExePath is not null)
            info.AttributesPath = StarCitizenDisplaySettings.AttributesPathFor(info.ExePath, File.Exists, FindAttributesCandidates);
        if (info.AttributesPath is not null && _modeByFile.TryGetValue(info.AttributesPath, out var known))
            return Settle(info, known);

        info.Attempts++;
        var text = info.AttributesPath is null ? null : TryReadText(info.AttributesPath);
        var parsed = StarCitizenDisplaySettings.ParseWindowMode(text);
        if (parsed is not null)
        {
            _log.Write(_clock.UtcNow, $"Star Citizen display settings file: {info.AttributesPath}");
            _log.Write(_clock.UtcNow, $"Star Citizen display mode: {parsed}");
            _modeByFile[info.AttributesPath!] = parsed;
            return Settle(info, parsed);
        }

        if (info.Attempts == 1)
        {
            info.RetryAtMs = now + 1_000;
            return null;
        }

        _log.Write(_clock.UtcNow, "Failed to open attributes.xml, assuming Borderless mode");
        if (info.AttributesPath is not null)
            _modeByFile[info.AttributesPath] = WindowMode.Borderless;
        return Settle(info, WindowMode.Borderless);
    }

    private static WindowMode? Settle(GameProcessInfo info, WindowMode? mode)
    {
        info.Settled = true;
        info.Mode = mode;
        return mode;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<(string path, DateTime modifiedUtc)> FindAttributesCandidates(string clientDirectory)
    {
        var found = new List<(string path, DateTime modifiedUtc)>();
        try
        {
            if (!Directory.Exists(clientDirectory))
                return found;
            foreach (var profile in Directory.EnumerateDirectories(clientDirectory))
            {
                var file = Path.Combine(profile, "Profiles", "default", "attributes.xml");
                if (File.Exists(file))
                    found.Add((file, File.GetLastWriteTimeUtc(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return found;
    }

    public bool TryFindGameWindow(out PixelRect bounds)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(GameProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            bounds = default;
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                IntPtr handle;
                try
                {
                    handle = process.MainWindowHandle;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    continue;
                }
                if (handle != IntPtr.Zero && !NativeMethods.IsIconic(handle) && NativeMethods.TryGetWindowBounds(handle, out bounds))
                    return true;
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        bounds = default;
        return false;
    }
}
