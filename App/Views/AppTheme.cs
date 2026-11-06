using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Iris.Core.Config;
using Microsoft.Win32;

namespace Iris.App.Views;

public static class AppTheme
{
    private const string LightFile = "ThemeLight.xaml";
    private const string DarkFile = "ThemeDark.xaml";
    private static readonly Uri LightUri = new("/IRIS;component/Views/" + LightFile, UriKind.Relative);
    private static readonly Uri DarkUri = new("/IRIS;component/Views/" + DarkFile, UriKind.Relative);

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    private static bool? _dark;

    public static bool IsDark => _dark == true;

    public static bool Resolve(DarkMode mode) => mode switch
    {
        DarkMode.On => true,
        DarkMode.Off => false,
        _ => WindowsAppsUseDarkMode(),
    };

    public static bool WindowsAppsUseDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            return key?.GetValue(AppsUseLightTheme) is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Apply(DarkMode mode, Action<string>? log = null)
    {
        var app = Application.Current;
        if (app is null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.InvokeAsync(() => Apply(mode, log));
            return;
        }

        var dark = Resolve(mode);
        if (_dark == dark)
            return;
        ResourceDictionary theme;
        try
        {
            theme = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        }
        catch (Exception ex)
        {
            log?.Invoke($"Settings window: the {(dark ? "dark" : "light")} theme could not be loaded ({ex.Message})");
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString;
            if (source is not null && (source.EndsWith(LightFile, StringComparison.OrdinalIgnoreCase) || source.EndsWith(DarkFile, StringComparison.OrdinalIgnoreCase)))
                merged.RemoveAt(i);
        }
        merged.Add(theme);
        _dark = dark;

        foreach (Window window in app.Windows)
            ApplyTitleBar(window);
    }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        if (window.WindowStyle == WindowStyle.None)
            return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var value = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
