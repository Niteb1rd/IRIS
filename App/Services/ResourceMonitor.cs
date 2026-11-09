using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Overlay;

namespace Iris.App.Services;

internal sealed class ResourceMonitor : IDisposable
{
    private const int IntervalMs = 1000;
    private const int StopWaitMs = 1500;

    private readonly Action<ResourceReading> _publish;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;

    public ResourceMonitor(Action<ResourceReading> publish)
    {
        _publish = publish;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "IRIS resource readout" };
        _thread.Start();
    }

    private void Run()
    {
        using var process = Process.GetCurrentProcess();
        using var gpu = GpuEngineCounters.TryCreate();
        var lastCpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        while (!_stop.Wait(IntervalMs))
        {
            try
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var elapsed = clock.Elapsed;
                clock.Restart();
                var percent = ResourceUsage.CpuPercent(cpu - lastCpu, elapsed, Environment.ProcessorCount);
                lastCpu = cpu;
                _publish(new ResourceReading(percent, process.WorkingSet64, gpu?.Read(Environment.ProcessId)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.Join(StopWaitMs))
            _stop.Dispose();
    }
}

internal sealed class GpuEngineCounters : IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint FormatDouble = 0x00000200;
    private const uint Success = 0;
    private const uint MoreData = 0x800007D2;
    private const uint StatusValid = 0;
    private const uint StatusNew = 1;

    private IntPtr _query;
    private readonly IntPtr _counter;

    private GpuEngineCounters(IntPtr query, IntPtr counter)
    {
        _query = query;
        _counter = counter;
    }

    public static GpuEngineCounters? TryCreate()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != Success)
                return null;
            if (PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out var counter) != Success)
            {
                PdhCloseQuery(query);
                return null;
            }

            PdhCollectQueryData(query);
            return new GpuEngineCounters(query, counter);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public double? Read(int processId)
    {
        if (PdhCollectQueryData(_query) != Success)
            return null;
        uint size = 0;
        uint count = 0;
        var status = PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, ref count, IntPtr.Zero);
        if (status == Success)
            return 0.0;
        if (status != MoreData)
            return null;

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, ref count, buffer) != Success)
                return null;
            var itemSize = Marshal.SizeOf<CounterItem>();
            var engines = new List<(string Instance, double Percent)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Status is StatusValid or StatusNew && Marshal.PtrToStringUni(item.Name) is { } name)
                    engines.Add((name, item.Value));
            }

            return ResourceUsage.GpuPercent(engines, processId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        var query = _query;
        _query = IntPtr.Zero;
        if (query != IntPtr.Zero)
            PdhCloseQuery(query);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
