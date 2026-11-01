using System;

namespace Iris.App.Services;

internal static class CaptureExclusion
{
    public static bool Apply(IntPtr hwnd, bool visibleInCapture = false)
    {
        if (visibleInCapture)
        {
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaNone);
            return false;
        }

        return NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
    }
}
