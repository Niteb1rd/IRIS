using System;
using System.Runtime.InteropServices;
using Iris.Core.Pipeline;
using SharpGen.Runtime;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

internal static class CaptureGpuPriority
{
    private static bool _logged;

    public static void Raise(Vd.ID3D11Device device, Action<string>? log)
    {
        string text;
        try
        {
            using var dxgi = device.QueryInterface<Vx.IDXGIDevice>();
            var result = dxgi.SetGPUThreadPriority(LiveSettings.CaptureGpuPriority);
            text = result.Success
                ? $"Live detection: capture GPU priority set to {LiveSettings.CaptureGpuPriority}"
                : $"Live detection: the capture GPU priority could not be set (HRESULT 0x{result.Code:X8})";
        }
        catch (Exception ex) when (ex is SharpGenException or COMException or InvalidCastException)
        {
            text = "Live detection: the capture GPU priority could not be set (" + ex.Message + ")";
        }

        if (_logged)
            return;
        _logged = true;
        log?.Invoke(text);
    }
}
