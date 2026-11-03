using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Iris.App.Services;

internal static class InstallerRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E4F579D1-DACB-43D5-A498-1AD3D1D3A211}_is1";

    public static bool SetVersion(string installFolder, string version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue("InstallLocation") is not string location || !SameFolder(location, installFolder))
                return false;
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("DisplayName", "IRIS", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
