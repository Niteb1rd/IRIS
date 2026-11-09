using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Config;

namespace Iris.App.Services;

public enum HotkeyAction
{
    ToggleOverlay,
    TimedOverlay,
    InteractiveOverlay,
}

public sealed class HotkeyManager : IDisposable
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int ThreadStopTimeoutMs = 2000;
    private const int ThreadStartTimeoutMs = 2000;

    public static volatile bool CaptureInProgress;

    private enum HookState
    {
        Starting,
        Running,
        Failed,
    }

    private static readonly HotkeyAction[] Actions = { HotkeyAction.ToggleOverlay, HotkeyAction.TimedOverlay, HotkeyAction.InteractiveOverlay };

    public static string NameOf(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOverlay => "Toggle IRIS On/Off",
        HotkeyAction.TimedOverlay => "Activate IRIS for X Seconds",
        HotkeyAction.InteractiveOverlay => "Toggle Interactive Overlay",
        _ => action.ToString(),
    };

    public static string TextOf(AppSettings settings, HotkeyAction action) => HotkeyText.Normalize(action switch
    {
        HotkeyAction.ToggleOverlay => settings.HotkeyToggleOverlay,
        HotkeyAction.TimedOverlay => settings.HotkeyTimedOverlay,
        HotkeyAction.InteractiveOverlay => settings.HotkeyInteractiveOverlay,
        _ => string.Empty,
    });

    private sealed record HookConfig(Hotkey?[] Keys)
    {
        public static readonly HookConfig None = new(new Hotkey?[Actions.Length]);
    }

    private sealed class HookSession
    {
        public Thread? Thread;
        public readonly ManualResetEventSlim Ready = new(false);
        public volatile uint ThreadId;
        public volatile bool Stopping;
        public volatile HookState State = HookState.Starting;
    }

    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;

    private HookSession? _session;
    private readonly string[] _texts = Enumerable.Repeat(string.Empty, Actions.Length).ToArray();
    private readonly string?[] _errors = new string?[Actions.Length];
    private bool _applied;
    private volatile bool _disposed;

    private volatile HookConfig _config = HookConfig.None;

    private HookConfig? _seenConfig;
    private readonly bool[] _held = new bool[Actions.Length];

    public HotkeyManager(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = HookCallback;
    }

    public event EventHandler<HotkeyAction>? Pressed;

    public event EventHandler? StatusChanged;

    public string? ErrorOf(HotkeyAction action) => _errors[(int)action];

    public void Apply(AppSettings settings)
    {
        if (_disposed)
            return;

        var texts = Array.ConvertAll(Actions, a => TextOf(settings, a));
        if (_applied && texts.SequenceEqual(_texts))
        {
            if (_session is { State: HookState.Failed })
                EnsureHooked();
            return;
        }

        var oldErrors = (string?[])_errors.Clone();
        _applied = true;
        texts.CopyTo(_texts, 0);

        var keys = new Hotkey?[Actions.Length];
        var parts = new string[Actions.Length];
        foreach (var action in Actions)
        {
            var i = (int)action;
            var text = texts[i];
            _errors[i] = null;
            foreach (var other in Actions)
            {
                if (other != action && text.Length > 0 && texts[(int)other] == text)
                {
                    _errors[i] = $"{text} is also the {NameOf(other)} hotkey. Choose different ones; neither is active now.";
                    break;
                }
            }

            if (_errors[i] is null && HotkeyText.TryParse(text, out var parsed, out _))
                keys[i] = parsed;
            parts[i] = $"{NameOf(action)} = {(text.Length == 0 ? "none" : text)}{(_errors[i] is null ? string.Empty : " (not active: same as another)")}";
        }

        _log.Write(_clock.UtcNow, "Hotkeys: " + string.Join(", ", parts));

        _config = new HookConfig(keys);

        if (Array.TrueForAll(keys, k => k is null))
            StopHookThread();
        else
            EnsureHooked();

        if (!oldErrors.SequenceEqual(_errors))
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureHooked()
    {
        if (_session is { } existing)
        {
            if (existing.State != HookState.Failed)
                return;
            StopHookThread();
        }

        var session = new HookSession();
        var thread = new Thread(() => HookThreadMain(session))
        {
            Name = "IRIS keyboard hook",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        session.Thread = thread;
        _session = session;
        thread.Start();

        session.Ready.Wait(ThreadStartTimeoutMs);
    }

    private void StopHookThread()
    {
        var session = _session;
        if (session is null)
            return;
        _session = null;
        session.Stopping = true;

        var threadId = session.ThreadId;
        if (threadId != 0 && session.State != HookState.Failed)
            NativeMethods.PostThreadMessage(threadId, NativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero);

        if (session.Thread is { } thread && !thread.Join(ThreadStopTimeoutMs))
            _log.Write(_clock.UtcNow, "Hotkeys: the keyboard hook thread did not stop within 2 seconds (it is a background thread and ends with IRIS)");
    }

    private void HookThreadMain(HookSession session)
    {
        var hook = IntPtr.Zero;
        try
        {
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PmNoRemove);
            session.ThreadId = NativeMethods.GetCurrentThreadId();
            session.Ready.Set();

            hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                session.State = HookState.Failed;
                PostLog($"Hotkeys: the keyboard hook could not be installed (Windows error {code}: {new Win32Exception(code).Message}); hotkeys do not work");
                return;
            }

            session.State = HookState.Running;
            while (!session.Stopping)
            {
                var result = NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0);
                if (result > 0)
                    continue;
                if (result < 0 && !session.Stopping)
                {
                    var code = Marshal.GetLastWin32Error();
                    session.State = HookState.Failed;
                    PostLog($"Hotkeys: the keyboard hook thread stopped (message loop error {code}); hotkeys do not work");
                }
                break;
            }
        }
        catch (Exception ex)
        {
            session.State = HookState.Failed;
            PostLog("Hotkeys: the keyboard hook thread failed: " + ex.Message);
        }
        finally
        {
            if (hook != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(hook);
            session.Ready.Set();
        }
    }

    private void PostLog(string text)
    {
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                    _log.Write(_clock.UtcNow, text);
            }));
        }
        catch (Exception)
        {
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                var key = Marshal.ReadInt32(lParam);
                if (message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown)
                    OnKeyDown(key);
                else if (message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp)
                    OnKeyUp(key);
            }
            catch (Exception)
            {
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private HookConfig CurrentConfig()
    {
        var config = _config;
        if (!ReferenceEquals(config, _seenConfig))
        {
            _seenConfig = config;
            Array.Clear(_held);
        }
        return config;
    }

    private void OnKeyDown(int key)
    {
        var config = CurrentConfig();

        for (var i = 0; i < config.Keys.Length; i++)
        {
            if (config.Keys[i] is not { } hotkey || key != hotkey.VirtualKey)
                continue;
            if (_held[i] && !IsDown(hotkey.VirtualKey))
                _held[i] = false;
            if (!CaptureInProgress && !_held[i] && !WindowsKeyHeld() && HeldModifiers() == hotkey.Modifiers)
            {
                _held[i] = true;
                Post((HotkeyAction)i);
            }
        }
    }

    private void OnKeyUp(int key)
    {
        var config = CurrentConfig();
        for (var i = 0; i < config.Keys.Length; i++)
            if (config.Keys[i] is { } hotkey && key == hotkey.VirtualKey)
                _held[i] = false;
    }

    private static HotkeyModifiers HeldModifiers()
    {
        var held = HotkeyModifiers.None;
        if (IsDown(VkControl))
            held |= HotkeyModifiers.Control;
        if (IsDown(VkMenu))
            held |= HotkeyModifiers.Alt;
        if (IsDown(VkShift))
            held |= HotkeyModifiers.Shift;
        return held;
    }

    private static bool WindowsKeyHeld() => IsDown(VkLWin) || IsDown(VkRWin);

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void Post(HotkeyAction action) =>
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
                Pressed?.Invoke(this, action);
        }));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopHookThread();
    }
}
