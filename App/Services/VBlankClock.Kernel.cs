
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Pipeline;
using Microsoft.Win32.SafeHandles;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

internal sealed partial class VBlankClock
{
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
