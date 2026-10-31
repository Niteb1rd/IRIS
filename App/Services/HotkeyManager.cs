using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Config;

namespace Iris.App.Services;

public enum HotkeyAction
{
    ToggleOverlay,
    TogglePanel,
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

    private sealed record HookConfig(Hotkey? Overlay, Hotkey? Panel)
    {
        public static readonly HookConfig None = new(null, null);
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
    private string _overlayText = string.Empty;
    private string _panelText = string.Empty;
    private bool _applied;
    private volatile bool _disposed;

    private volatile HookConfig _config = HookConfig.None;

    private HookConfig? _seenConfig;
    private bool _overlayHeld;
    private bool _panelHeld;

    public HotkeyManager(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = HookCallback;
    }

    public event EventHandler<HotkeyAction>? Pressed;

    public event EventHandler? StatusChanged;

    public string? OverlayError { get; private set; }

    public string? PanelError { get; private set; }

    public void Apply(AppSettings settings)
    {
        if (_disposed)
            return;

        var overlayText = HotkeyText.Normalize(settings.HotkeyToggleOverlay);
        var panelText = HotkeyText.Normalize(settings.HotkeyTogglePanel);
        if (_applied && overlayText == _overlayText && panelText == _panelText)
        {
            if (_session is { State: HookState.Failed })
                EnsureHooked();
            return;
        }

        var oldOverlayError = OverlayError;
        var oldPanelError = PanelError;
        _applied = true;
        _overlayText = overlayText;
        _panelText = panelText;
        OverlayError = null;
        PanelError = null;

        Hotkey? overlayKey = null;
        Hotkey? panelKey = null;
        if (overlayText.Length > 0 && overlayText == panelText)
        {
            OverlayError = PanelError = $"Both hotkeys are {overlayText}. Choose two different ones; neither is active now.";
            _log.Write(_clock.UtcNow, "Hotkeys: both are " + overlayText + "; neither is active");
        }
        else
        {
            if (HotkeyText.TryParse(overlayText, out var parsedOverlay, out _))
                overlayKey = parsedOverlay;
            if (HotkeyText.TryParse(panelText, out var parsedPanel, out _))
                panelKey = parsedPanel;
            _log.Write(_clock.UtcNow,
                $"Hotkeys: Toggle Overlay = {(overlayText.Length == 0 ? "none" : overlayText)}, Toggle Info Panel = {(panelText.Length == 0 ? "none" : panelText)}");
        }

        _config = new HookConfig(overlayKey, panelKey);

        if (overlayKey is null && panelKey is null)
            StopHookThread();
        else
            EnsureHooked();

        if (OverlayError != oldOverlayError || PanelError != oldPanelError)
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
            _overlayHeld = false;
            _panelHeld = false;
        }
        return config;
    }

    private void OnKeyDown(int key)
    {
        var config = CurrentConfig();

        if (config.Overlay is { } overlay && key == overlay.VirtualKey)
        {
            if (_overlayHeld && !IsDown(overlay.VirtualKey))
                _overlayHeld = false;
            if (!CaptureInProgress && !_overlayHeld && !WindowsKeyHeld() && HeldModifiers() == overlay.Modifiers)
            {
                _overlayHeld = true;
                Post(HotkeyAction.ToggleOverlay);
            }
        }

        if (config.Panel is { } panel && key == panel.VirtualKey)
        {
            if (_panelHeld && !IsDown(panel.VirtualKey))
                _panelHeld = false;
            if (!CaptureInProgress && !_panelHeld && !WindowsKeyHeld() && HeldModifiers() == panel.Modifiers)
            {
                _panelHeld = true;
                Post(HotkeyAction.TogglePanel);
            }
        }
    }

    private void OnKeyUp(int key)
    {
        var config = CurrentConfig();
        if (config.Overlay is { } overlay && key == overlay.VirtualKey)
            _overlayHeld = false;
        if (config.Panel is { } panel && key == panel.VirtualKey)
            _panelHeld = false;
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
