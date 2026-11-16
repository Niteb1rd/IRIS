using System;
using System.IO;

namespace Iris.App.Services;

internal static class WindowsTools
{
    public static string Notepad => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
}
