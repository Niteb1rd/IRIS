using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Iris.App.Services;

internal sealed class LayeredWindow : IDisposable
{
    private const string ClassName = "IrisOverlayLayeredWindow";

    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;

    private const uint WmMouseActivate = 0x0021;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly IntPtr MaNoActivate = new(3);

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private const uint DibRgbColors = 0;
    private const int ErrorClassAlreadyExists = 1410;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate WndProcInstance = WndProc;
    private static bool _classRegistered;

    private IntPtr _hwnd;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _stockBitmap;
    private IntPtr _bits;
    private int _width;
    private int _height;
    private bool _shown;
    private bool _disposed;

    public LayeredWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        RegisterClassOnce(instance);

        var exStyle = unchecked((uint)(NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)) | WsExTopmost;
        _hwnd = CreateWindowExW(exStyle, ClassName, "IRIS overlay", WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            throw new Win32Exception(error);
        }
    }

    public IntPtr Handle => _hwnd;

    public bool IsShown => _shown;

    public int Stride => _width * 4;

    public IntPtr BeginSurface(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
            return IntPtr.Zero;
        if (_bitmap != IntPtr.Zero && width == _width && height == _height)
            return _bits;

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)(width * height * 4),
            },
        };
        var bitmap = CreateDIBSection(_memoryDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            return IntPtr.Zero;
        }

        var previous = SelectObject(_memoryDc, bitmap);
        if (_bitmap == IntPtr.Zero)
            _stockBitmap = previous;
        else
            DeleteObject(_bitmap);
        _bitmap = bitmap;
        _bits = bits;
        _width = width;
        _height = height;
        return _bits;
    }

    public bool Commit(int x, int y, int width, int height)
    {
        if (_disposed || _hwnd == IntPtr.Zero || _bitmap == IntPtr.Zero || width <= 0 || height <= 0 || width > _width || height > _height)
            return false;

        var position = new NativePoint { X = x, Y = y };
        var size = new NativeSize { Cx = width, Cy = height };
        var source = new NativePoint { X = 0, Y = 0 };
        var blend = new BlendFunction { BlendOp = AcSrcOver, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            return UpdateLayeredWindow(_hwnd, screen, ref position, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (screen != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        ShowWindow(_hwnd, SwShowNoActivate);
        _shown = true;
    }

    public void Hide()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        ShowWindow(_hwnd, SwHide);
        _shown = false;
    }

    public void AssertTopmost()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shown = false;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_memoryDc != IntPtr.Zero)
        {
            if (_stockBitmap != IntPtr.Zero)
                SelectObject(_memoryDc, _stockBitmap);
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_bitmap != IntPtr.Zero)
        {
            DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
    }

    private static readonly object ClassGate = new();

    private static void RegisterClassOnce(IntPtr instance)
    {
        lock (ClassGate)
            RegisterClassLocked(instance);
    }

    private static void RegisterClassLocked(IntPtr instance)
    {
        if (_classRegistered)
            return;
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = 0,
            WndProc = Marshal.GetFunctionPointerForDelegate(WndProcInstance),
            ClassExtra = 0,
            WindowExtra = 0,
            Instance = instance,
            Icon = IntPtr.Zero,
            Cursor = IntPtr.Zero,
            Background = IntPtr.Zero,
            MenuName = null,
            ClassName = ClassName,
            IconSmall = IntPtr.Zero,
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
                throw new Win32Exception(error);
        }

        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmNcHitTest:
                    return HtTransparent;
                case WmMouseActivate:
                    return MaNoActivate;
                case WmEraseBkgnd:
                    return new IntPtr(1);
            }
        }
        catch (Exception)
        {
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public IntPtr IconSmall;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr destinationDc, ref NativePoint destinationPosition, ref NativeSize size, IntPtr sourceDc, ref NativePoint sourcePosition,
        uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
