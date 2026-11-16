using System;
using System.Collections.Generic;
using System.Threading;
using Vd = Vortice.Direct3D11;

namespace Iris.App.Services;

internal static class CaptureSharing
{
    private static readonly Lock s_gate = new();
    private static readonly HashSet<string> s_logged = new();

    public static void LogOnce(Action<string>? log, string source, Vd.ResourceOptionFlags flags)
    {
        if (log is null)
            return;
        lock (s_gate)
        {
            if (!s_logged.Add(source))
                return;
        }

        var verdict = (flags & Vd.ResourceOptionFlags.SharedNTHandle) != 0
            ? "shareable with an NT handle: a D3D12 copy queue could open it (same graphics card, synchronised with the D3D11 writer)"
            : (flags & (Vd.ResourceOptionFlags.Shared | Vd.ResourceOptionFlags.SharedKeyedMutex)) != 0
                ? "shareable only with a legacy handle: D3D12 cannot open it, a D3D11 copy would be needed first"
                : "not shareable: a D3D12 copy queue would need a D3D11 copy first";
        log($"{source}: captured picture texture flags {flags}, {verdict}");
    }
}
