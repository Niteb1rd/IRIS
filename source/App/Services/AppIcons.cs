using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Iris.App.Services;

public static class AppIcons
{
    private static ImageSource? _windowOn;
    private static ImageSource? _windowOff;
    private static Drawing.Icon? _trayOn;
    private static Drawing.Icon? _trayOff;

    public static bool OverlayDisabled { get; private set; }

    public static event EventHandler? Changed;

    public static void SetOverlayDisabled(bool disabled)
    {
        if (disabled == OverlayDisabled)
            return;
        OverlayDisabled = disabled;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static ImageSource? WindowIcon => OverlayDisabled
        ? _windowOff ??= LoadImage("iris-off.ico")
        : _windowOn ??= LoadImage("iris-on.ico");

    public static Drawing.Icon Tray => (OverlayDisabled
        ? _trayOff ??= LoadIcon("tray-off.ico")
        : _trayOn ??= LoadIcon("tray-on.ico")) ?? Drawing.SystemIcons.Application;

    private static Uri UriOf(string name) => new("pack://application:,,,/Assets/" + name, UriKind.Absolute);

    private static ImageSource? LoadImage(string name)
    {
        try
        {
            return BitmapFrame.Create(UriOf(name));
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static Drawing.Icon? LoadIcon(string name)
    {
        try
        {
            var info = Application.GetResourceStream(UriOf(name));
            if (info is null)
                return null;
            using var stream = info.Stream;
            return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
