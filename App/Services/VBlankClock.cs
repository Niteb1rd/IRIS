using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Pipeline;
using Microsoft.Win32.SafeHandles;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

internal sealed class VBlankClock : IDisposable
{
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    private readonly Func<int, bool> _sleep;
    private readonly WaitHandle? _wake;
    private TimerWaitHandle? _timer;
    private WaitHandle[]? _waitSet;
    private bool _timerTried;
    private bool _periodRaised;
    private long _dwmTimerOffUntil;
    private long _lastVblankQpc;
    private uint _timingBytes = NativeMethods.DwmTimingInfoBytes;
    private IntPtr _monitor;
    private Vx.IDXGIOutput? _output;
    private long _resolveAtTick;
    private long _vblankOffUntil;
    private long _dwmOffUntil;
    private int _vblankInstant;
    private int _dwmInstant;
    private KernelWaiter? _kernel;
    private bool _kernelPending;
    private bool _kernelStalled;
    private long _kernelOffUntil;
    private int _kernelInstant;

    public VBlankClock(Func<int, bool> sleep, WaitHandle? wake = null)
    {
        _sleep = sleep;
        _wake = wake;
    }

    public RenderClockKind Kind { get; private set; } = RenderClockKind.Timer;

    public bool LastWaitLate { get; private set; }

    public void WaitNextFrame(IntPtr gameWindow, double periodMs)
    {
        LastWaitLate = false;
        var now = Environment.TickCount64;
        var monitor = IntPtr.Zero;
        try
        {
            monitor = NativeMethods.MonitorFromWindow(
                gameWindow, gameWindow == IntPtr.Zero ? NativeMethods.MonitorDefaultToPrimary : NativeMethods.MonitorDefaultToNearest);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        if (monitor != _monitor)
        {
            _monitor = monitor;
            ReleaseOutput();
            CloseKernel();
            _resolveAtTick = 0;
            _vblankOffUntil = 0;
            _vblankInstant = 0;
            _kernelOffUntil = 0;
            _kernelInstant = 0;
        }

        if (monitor != IntPtr.Zero && now >= _kernelOffUntil)
        {
            if (OpenKernel(monitor))
            {
                var started = Stopwatch.GetTimestamp();
                var result = WaitKernelVBlank(periodMs);
                if (result == KernelWait.Woken)
                {
                    Kind = RenderClockKind.KernelVblank;
                    return;
                }

                if (result == KernelWait.Blank)
                {
                    var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    if (!IsInstant(started, periodMs, ref _kernelInstant) || _kernelInstant < LiveSettings.VBlankInstantLimit)
                    {
                        Kind = RenderClockKind.KernelVblank;
                        LastWaitLate = elapsedMs > 1.5 * periodMs;
                        return;
                    }

                    _kernelInstant = 0;
                }

                _kernelOffUntil = Math.Max(_kernelOffUntil, now + LiveSettings.VBlankGiveUpMs);
                if (result != KernelWait.TimedOut)
                    CloseKernel();
            }
            else
            {
                _kernelOffUntil = Math.Max(_kernelOffUntil, now + LiveSettings.VBlankResolveRetryMs);
            }
        }

        if (now >= _dwmTimerOffUntil)
        {
            if (WaitDwmTimer())
            {
                Kind = RenderClockKind.DwmTimer;
                return;
            }

            _dwmTimerOffUntil = now + LiveSettings.VBlankGiveUpMs;
        }

        if (_output is null && monitor != IntPtr.Zero && now >= _resolveAtTick && now >= _vblankOffUntil)
        {
            _output = FindOutput(monitor);
            if (_output is null)
                _resolveAtTick = now + LiveSettings.VBlankResolveRetryMs;
        }

        if (_output is not null && now >= _vblankOffUntil)
        {
            var started = Stopwatch.GetTimestamp();
            if (WaitForVBlank())
            {
                if (!IsInstant(started, periodMs, ref _vblankInstant))
                {
                    Kind = RenderClockKind.Vblank;
                    return;
                }

                if (_vblankInstant < LiveSettings.VBlankInstantLimit)
                {
                    Kind = RenderClockKind.Vblank;
                    return;
                }

                _vblankInstant = 0;
            }

            _vblankOffUntil = now + LiveSettings.VBlankGiveUpMs;
            ReleaseOutput();
        }

        if (now >= _dwmOffUntil)
        {
            var started = Stopwatch.GetTimestamp();
            if (DwmFlush())
            {
                if (!IsInstant(started, periodMs, ref _dwmInstant) || _dwmInstant < LiveSettings.VBlankInstantLimit)
                {
                    Kind = RenderClockKind.Dwm;
                    return;
                }

                _dwmInstant = 0;
            }

            _dwmOffUntil = now + LiveSettings.VBlankGiveUpMs;
        }

        Kind = RenderClockKind.Timer;
        _sleep((int)Math.Max(1.0, Math.Round(periodMs)));
    }

    public void Dispose()
    {
        ReleaseOutput();
        CloseKernel();
        var timer = _timer;
        _timer = null;
        _waitSet = null;
        try
        {
            timer?.Dispose();
        }
        catch (Exception)
        {
        }

        if (_periodRaised)
        {
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

    private bool WaitDwmTimer()
    {
        try
        {
            if (!EnsureTimer())
                return false;
            var info = new NativeMethods.DwmTimingInfo { CbSize = _timingBytes };
            if (NativeMethods.DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0)
            {
                if (_timingBytes != NativeMethods.DwmTimingInfoBytes)
                    return false;
                info = new NativeMethods.DwmTimingInfo { CbSize = _timingBytes = NativeMethods.DwmTimingInfoBytes - 4 };
                if (NativeMethods.DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0)
                {
                    _timingBytes = NativeMethods.DwmTimingInfoBytes;
                    return false;
                }
            }

            var frequency = (double)Stopwatch.Frequency;
            var period = (long)info.QpcRefreshPeriod;
            if (period <= 0 && info.RateRefresh.Numerator != 0 && info.RateRefresh.Denominator != 0)
                period = (long)Math.Round(frequency * info.RateRefresh.Denominator / info.RateRefresh.Numerator);
            var periodMs = period * 1000.0 / frequency;
            if (period <= 0 || !(periodMs >= 1.0 && periodMs <= LiveSettings.VBlankMaxPeriodMs) || info.QpcVBlank == 0)
                return false;

            var now = Stopwatch.GetTimestamp();
            var next = (long)info.QpcVBlank + ((long)Math.Floor((now - (long)info.QpcVBlank) / (double)period) + 1) * period;
            for (var i = 0; i < 4 && _lastVblankQpc != 0 && next < _lastVblankQpc + period / 2; i++)
                next += period;

            var dueTicks = next - (long)(LiveSettings.VBlankLeadMs * frequency / 1000.0) - now;
            if (dueTicks > 0)
            {
                var due = -(long)Math.Ceiling(dueTicks * 10_000_000.0 / frequency);
                if (!NativeMethods.SetWaitableTimer(_timer!.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    return false;
                var timeoutMs = (int)Math.Ceiling(dueTicks * 1000.0 / frequency) + 20;
                var woken = _waitSet is { Length: 2 } set ? WaitHandle.WaitAny(set, timeoutMs) : WaitHandle.WaitAny(new WaitHandle[] { _timer }, timeoutMs);
                if (woken == 1)
                {
                    NativeMethods.CancelWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle());
                    return true;
                }
            }

            _lastVblankQpc = next;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _dwmTimerOffUntil = long.MaxValue;
            return false;
        }
    }

    private bool EnsureTimer()
    {
        if (_timer is not null)
            return true;
        if (_timerTried || Marshal.SizeOf<NativeMethods.DwmTimingInfo>() != NativeMethods.DwmTimingInfoBytes)
            return false;
        _timerTried = true;
        var handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, NativeMethods.CreateWaitableTimerHighResolution, NativeMethods.TimerAllAccess);
        if (handle == IntPtr.Zero)
        {
            handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, 0, NativeMethods.TimerAllAccess);
            if (handle == IntPtr.Zero)
                return false;
            _periodRaised = NativeMethods.TimeBeginPeriod(1) == 0;
        }

        _timer = new TimerWaitHandle(handle);
        _waitSet = _wake is null ? null : new WaitHandle[] { _timer, _wake };
        return true;
    }

    private static bool IsInstant(long startedTicks, double periodMs, ref int counter)
    {
        var elapsedMs = (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= periodMs * LiveSettings.VBlankInstantFraction)
        {
            counter = 0;
            return false;
        }

        counter++;
        return true;
    }

    private enum KernelWait
    {
        Blank,
        Woken,
        TimedOut,
        Failed,
    }

    private sealed class KernelWaiter
    {
        private readonly uint _adapter;
        private readonly uint _source;
        private readonly AutoResetEvent _request = new(false);
        private volatile bool _stop;

        public KernelWaiter(uint adapter, uint source, WaitHandle? wake)
        {
            _adapter = adapter;
            _source = source;
            Handles = wake is null ? new WaitHandle[] { Tick } : new WaitHandle[] { Tick, wake };
            new Thread(Run) { Name = "IRIS vblank", IsBackground = true, Priority = ThreadPriority.Highest }.Start();
        }

        public AutoResetEvent Tick { get; } = new(false);

        public WaitHandle[] Handles { get; }

        public bool Failed { get; private set; }

        public void Request() => _request.Set();

        public void Stop()
        {
            _stop = true;
            _request.Set();
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    _request.WaitOne();
                    if (_stop)
                        return;
                    int status;
                    try
                    {
                        var wait = new NativeMethods.D3dkmtWaitForVerticalBlankEvent { HAdapter = _adapter, HDevice = 0, VidPnSourceId = _source };
                        status = NativeMethods.D3DKMTWaitForVerticalBlankEvent(ref wait);
                    }
                    catch (Exception)
                    {
                        status = -1;
                    }

                    if (status != 0)
                        Failed = true;
                    Tick.Set();
                    if (Failed || _stop)
                        return;
                }
            }
            finally
            {
                try
                {
                    var close = new NativeMethods.D3dkmtCloseAdapter { HAdapter = _adapter };
                    NativeMethods.D3DKMTCloseAdapter(ref close);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private bool OpenKernel(IntPtr monitor)
    {
        if (_kernel is not null)
            return true;
        try
        {
            var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
            if (!NativeMethods.GetMonitorInfoEx(monitor, ref info) || string.IsNullOrEmpty(info.Device))
                return false;
            var dc = NativeMethods.CreateDC(info.Device, null, null, IntPtr.Zero);
            if (dc == IntPtr.Zero)
                return false;
            uint adapter;
            uint source;
            try
            {
                var open = new NativeMethods.D3dkmtOpenAdapterFromHdc { HDc = dc };
                if (NativeMethods.D3DKMTOpenAdapterFromHdc(ref open) != 0 || open.HAdapter == 0)
                    return false;
                adapter = open.HAdapter;
                source = open.VidPnSourceId;
            }
            finally
            {
                NativeMethods.DeleteDC(dc);
            }

            try
            {
                _kernel = new KernelWaiter(adapter, source, _wake);
            }
            catch (Exception ex) when (ex is ThreadStartException or OutOfMemoryException)
            {
                var close = new NativeMethods.D3dkmtCloseAdapter { HAdapter = adapter };
                NativeMethods.D3DKMTCloseAdapter(ref close);
                return false;
            }

            _kernelPending = false;
            _kernelStalled = false;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _kernelOffUntil = long.MaxValue;
            return false;
        }
    }

    private KernelWait WaitKernelVBlank(double periodMs)
    {
        var waiter = _kernel!;
        if (_kernelStalled)
        {
            if (!waiter.Tick.WaitOne(0))
                return KernelWait.TimedOut;
            _kernelStalled = false;
            _kernelPending = false;
            if (waiter.Failed)
                return KernelWait.Failed;
        }

        if (!_kernelPending)
        {
            waiter.Request();
            _kernelPending = true;
        }

        var timeoutMs = (int)Math.Ceiling(Math.Clamp(4.0 * periodMs, 100.0, 250.0));
        var index = WaitHandle.WaitAny(waiter.Handles, timeoutMs);
        if (index == 1)
            return KernelWait.Woken;
        if (index == WaitHandle.WaitTimeout)
        {
            _kernelStalled = true;
            return KernelWait.TimedOut;
        }

        _kernelPending = false;
        return waiter.Failed ? KernelWait.Failed : KernelWait.Blank;
    }

    private void CloseKernel()
    {
        var waiter = _kernel;
        _kernel = null;
        _kernelPending = false;
        _kernelStalled = false;
        waiter?.Stop();
    }

    private bool WaitForVBlank()
    {
        try
        {
            var output = _output;
            return output is not null && Succeeded(() => output.WaitForVBlank());
        }
        catch (Exception)
        {
            return false;
        }
    }

#pragma warning disable IDE0051
    private static bool Succeeded(Func<SharpGen.Runtime.Result> call) => !call().Failure;
#pragma warning restore IDE0051

    private static bool Succeeded(Action call)
    {
        call();
        return true;
    }

    private static bool DwmFlush()
    {
        try
        {
            return NativeMethods.DwmFlush() == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void ReleaseOutput()
    {
        var output = _output;
        _output = null;
        try
        {
            output?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static Vx.IDXGIOutput? FindOutput(IntPtr monitor)
    {
        try
        {
            if (Vx.DXGI.CreateDXGIFactory1<Vx.IDXGIFactory1>(out var factory).Failure || factory is null)
                return null;
            using (factory)
            {
                for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
                {
                    using (adapter)
                    {
                        for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                        {
                            if (output.Description.Monitor == monitor)
                                return output;
                            output.Dispose();
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}
