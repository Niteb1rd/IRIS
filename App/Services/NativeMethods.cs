using System;
using System.Runtime.InteropServices;
using System.Text;
using Iris.Core.Overlay;

namespace Iris.App.Services;

internal static class NativeMethods
{
    public const int GwlStyle = -16;

    public const long WsExTransparent = 0x00000020L;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExLayered = 0x00080000L;
    public const long WsExNoActivate = 0x08000000L;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    public static readonly IntPtr HwndTopmost = new IntPtr(-1);

    public const uint MonitorDefaultToPrimary = 1;
    public const uint MonitorDefaultToNearest = 2;
    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    public const uint WdaExcludeFromCapture = 0x00000011;

    public const uint WdaNone = 0x00000000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public const int WhKeyboardLl = 13;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    public const uint WmQuit = 0x0012;
    public const uint PmNoRemove = 0x0000;
    public const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    public struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DwmTimingInfo
    {
        public uint CbSize;
        public UnsignedRatio RateRefresh;
        public ulong QpcRefreshPeriod;
        public UnsignedRatio RateCompose;
        public ulong QpcVBlank;
        public ulong CRefresh;
        public uint CDxRefresh;
        public ulong QpcCompose;
        public ulong CFrame;
        public uint CDxPresent;
        public ulong CRefreshFrame;
        public ulong CFrameSubmitted;
        public uint CDxPresentSubmitted;
        public ulong CFrameConfirmed;
        public uint CDxPresentConfirmed;
        public ulong CRefreshConfirmed;
        public uint CDxRefreshConfirmed;
        public ulong CFramesLate;
        public uint CFramesOutstanding;
        public ulong CFrameDisplayed;
        public ulong QpcFrameDisplayed;
        public ulong CRefreshFrameDisplayed;
        public ulong CFrameComplete;
        public ulong QpcFrameComplete;
        public ulong CFramePending;
        public ulong QpcFramePending;
        public ulong CFramesDisplayed;
        public ulong CFramesComplete;
        public ulong CFramesPending;
        public ulong CFramesAvailable;
        public ulong CFramesDropped;
        public ulong CFramesMissed;
        public ulong CRefreshNextDisplayed;
        public ulong CRefreshNextPresented;
        public ulong CRefreshesDisplayed;
        public ulong CRefreshesPresented;
        public ulong CRefreshStarted;
        public ulong CPixelsReceived;
        public ulong CPixelsDrawn;
        public ulong CBuffersEmpty;
    }

    public const int DwmTimingInfoBytes = 292;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtOpenAdapterFromHdc
    {
        public IntPtr HDc;
        public uint HAdapter;
        public Luid AdapterLuid;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtWaitForVerticalBlankEvent
    {
        public uint HAdapter;
        public uint HDevice;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtCloseAdapter
    {
        public uint HAdapter;
    }

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTOpenAdapterFromHdc(ref D3dkmtOpenAdapterFromHdc data);

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTWaitForVerticalBlankEvent(ref D3dkmtWaitForVerticalBlankEvent data);

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter data);

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDC(string? driver, string? device, string? port, IntPtr devMode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    public const uint CreateWaitableTimerHighResolution = 0x00000002;
    public const uint TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelWaitableTimer(IntPtr timer);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    public const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out NativeRect value, int size);

    public static uint GetStyle(IntPtr hwnd) => unchecked((uint)GetWindowLongPtr(hwnd, GwlStyle).ToInt64());

    public static bool TryGetWindowBounds(IntPtr hwnd, out PixelRect bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) == 0 && IsUsable(frame))
        {
            bounds = ToPixelRect(frame);
            return true;
        }
        if (GetWindowRect(hwnd, out var rect) && IsUsable(rect))
        {
            bounds = ToPixelRect(rect);
            return true;
        }
        bounds = default;
        return false;
    }

    public static bool TryGetClientOffset(IntPtr hwnd, out int offsetX, out int offsetY)
    {
        offsetX = 0;
        offsetY = 0;
        var origin = new NativePoint();
        if (!TryGetWindowBounds(hwnd, out var frame) || !GetClientRect(hwnd, out var client) || !IsUsable(client) || !ClientToScreen(hwnd, ref origin))
            return false;
        offsetX = origin.X - (int)Math.Round(frame.X);
        offsetY = origin.Y - (int)Math.Round(frame.Y);
        return true;
    }

    public static bool TryGetMonitorBounds(IntPtr hwnd, out PixelRect bounds)
    {
        var monitor = MonitorFromWindow(hwnd, hwnd == IntPtr.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && IsUsable(info.Monitor))
            {
                bounds = ToPixelRect(info.Monitor);
                return true;
            }
        }
        bounds = default;
        return false;
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint milliseconds);

    public static bool TryGetProcessImagePath(uint processId, out string path)
    {
        path = string.Empty;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            foreach (var capacity in new[] { 1024u, 8192u, 32768u })
            {
                var buffer = new StringBuilder((int)capacity);
                var size = capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    path = buffer.ToString();
                    return path.Length > 0;
                }
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return false;
            }
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsUsable(NativeRect r) => r.Right > r.Left && r.Bottom > r.Top;

    private static PixelRect ToPixelRect(NativeRect r) => PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
}
