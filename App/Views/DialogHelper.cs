using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Iris.App.Views;

internal static class DialogHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static bool IsIrisForeground()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            if (GetWindowThreadProcessId(foreground, out var processId) == 0)
                return false;
            return processId == (uint)Environment.ProcessId;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static bool PrepareOwner(Window? owner)
    {
        if (owner is null || !owner.IsLoaded || !owner.IsVisible)
            return false;

        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();
        return true;
    }

    public static void PrepareBackgroundDialog(
        Window dialog, Window? owner, string balloonText, Action<string>? notify, bool userJustActed = false)
    {
        dialog.Topmost = false;

        if (userJustActed || IsIrisForeground())
        {
            if (owner is not null && PrepareOwner(owner))
            {
                dialog.Owner = owner;
                dialog.ShowInTaskbar = false;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.ShowInTaskbar = true;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return;
        }

        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = true;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            notify?.Invoke(balloonText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
