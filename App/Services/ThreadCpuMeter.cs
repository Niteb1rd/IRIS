using System;
using System.Runtime.InteropServices;

namespace Iris.App.Services;

internal sealed class ThreadCpuMeter : IDisposable
{
    private const uint ThreadQueryLimitedInformation = 0x0800;

    private IntPtr _handle;
    private long _lastTicks;

    private ThreadCpuMeter(IntPtr handle)
    {
        _handle = handle;
        _lastTicks = Read(handle);
    }

    public static ThreadCpuMeter? ForCurrentThread()
    {
        try
        {
            var handle = OpenThread(ThreadQueryLimitedInformation, false, GetCurrentThreadId());
            return handle == IntPtr.Zero ? null : new ThreadCpuMeter(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public double TakeDeltaMs()
    {
        var handle = _handle;
        if (handle == IntPtr.Zero)
            return 0.0;
        var now = Read(handle);
        var delta = Math.Max(0L, now - _lastTicks);
        _lastTicks = now;
        return delta / 10_000.0;
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    private static long Read(IntPtr handle)
    {
        if (!GetThreadTimes(handle, out _, out _, out var kernel, out var user))
            return 0L;
        return ToTicks(kernel) + ToTicks(user);
    }

    private static long ToTicks(FileTime time) => ((long)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
