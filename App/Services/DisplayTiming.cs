using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iris.Core.Pipeline;

namespace Iris.App.Services;

internal static class DisplayTiming
{
    private const int EnumCurrentSettings = -1;
    private const int DevModeSize = 220;
    private const int MaxCachedMonitors = 16;

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, (long AtTick, double PeriodMs)> Cache = new();

    public static double RefreshPeriodMs(IntPtr gameWindow)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(
                gameWindow, gameWindow == IntPtr.Zero ? NativeMethods.MonitorDefaultToPrimary : NativeMethods.MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
                return LiveSettings.DisplayFallbackPeriodMs;

            var now = Environment.TickCount64;
            lock (Gate)
            {
                if (Cache.TryGetValue(monitor, out var known) && now - known.AtTick < LiveSettings.DisplayPeriodCacheMs)
                    return known.PeriodMs;
            }

            var period = Read(monitor);
            lock (Gate)
            {
                if (Cache.Count >= MaxCachedMonitors)
                    Cache.Clear();
                Cache[monitor] = (now, period);
            }

            return period;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return LiveSettings.DisplayFallbackPeriodMs;
        }
    }

    internal static double PeriodForHz(double hertz) => hertz >= 24 && hertz <= 600 ? 1000.0 / hertz : LiveSettings.DisplayFallbackPeriodMs;

    private static double Read(IntPtr monitor)
    {
        if (Marshal.SizeOf<DevMode>() != DevModeSize)
            return LiveSettings.DisplayFallbackPeriodMs;

        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
        if (!NativeMethods.GetMonitorInfoEx(monitor, ref info) || string.IsNullOrEmpty(info.Device))
            return LiveSettings.DisplayFallbackPeriodMs;

        var mode = new DevMode { Size = (ushort)DevModeSize };
        return EnumDisplaySettingsW(info.Device, EnumCurrentSettings, ref mode) ? PeriodForHz(mode.DisplayFrequency) : LiveSettings.DisplayFallbackPeriodMs;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNumber, ref DevMode mode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
