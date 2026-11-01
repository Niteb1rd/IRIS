using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Iris.App.Views;

internal static class DialogHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const string SystemDialogClass = "#32770";

    private static readonly List<Window> OpenModals = new();

    public static bool? ShowModal(Window dialog)
    {
        OpenModals.Add(dialog);
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            OpenModals.Remove(dialog);
        }
    }

    public static void CloseModals()
    {
        for (var i = OpenModals.Count - 1; i >= 0; i--)
        {
            try
            {
                OpenModals[i].Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public static bool ActivateModal()
    {
        if (OpenModals.Count == 0)
            return false;
        var dialog = OpenModals[^1];
        if (dialog.WindowState == WindowState.Minimized)
            dialog.WindowState = WindowState.Normal;
        dialog.Activate();
        return true;
    }

    public static bool IsSystemDialogOpen()
    {
        try
        {
            var found = false;
            var name = new StringBuilder(16);
            EnumThreadWindows(GetCurrentThreadId(), (hWnd, _) =>
            {
                name.Clear();
                if (IsWindowVisible(hWnd) && GetClassName(hWnd, name, name.Capacity) > 0 && name.ToString() == SystemDialogClass)
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

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

    public static void PrepareBackgroundDialog(Window dialog, Window? owner, bool userJustActed = false)
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
    }
}
