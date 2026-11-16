using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Iris.App.Services;

internal sealed class HighResolutionTimer : IDisposable
{
    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    private readonly TimerHandle _timer;
    private bool _periodRaised;
    private bool _disposed;

    private HighResolutionTimer(IntPtr handle, bool periodRaised)
    {
        _timer = new TimerHandle(handle);
        _periodRaised = periodRaised;
    }

    public WaitHandle Handle => _timer;

    public static HighResolutionTimer? TryCreate(bool plainWithRaisedPeriod)
    {
        var handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, NativeMethods.CreateWaitableTimerHighResolution, NativeMethods.TimerAllAccess);
        if (handle != IntPtr.Zero)
            return new HighResolutionTimer(handle, false);
        if (!plainWithRaisedPeriod)
            return null;
        handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, 0, NativeMethods.TimerAllAccess);
        if (handle == IntPtr.Zero)
            return null;
        var timer = new HighResolutionTimer(handle, false);
        try
        {
            timer._periodRaised = NativeMethods.TimeBeginPeriod(1) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            timer.Dispose();
            return null;
        }

        return timer;
    }

    public bool ArmIn(long dueIn100ns)
    {
        var due = -Math.Max(1L, dueIn100ns);
        return NativeMethods.SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
    }

    public void Cancel() => NativeMethods.CancelWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle());

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _timer.Dispose();
        }
        catch (Exception)
        {
        }

        if (!_periodRaised)
            return;
        _periodRaised = false;
        try
        {
            NativeMethods.TimeEndPeriod(1);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
