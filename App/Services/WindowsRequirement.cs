using System;

namespace Iris.App.Services;

internal static class WindowsRequirement
{
    public const int MinimumBuild = 19041;

    public static int CurrentBuild => Environment.OSVersion.Version.Build;

    public static bool IsMet => Environment.OSVersion.Version.Major > 10 || (Environment.OSVersion.Version.Major == 10 && CurrentBuild >= MinimumBuild);

    public static string Message =>
        $"IRIS needs Windows 10 version 2004 or later (Windows 11 recommended). This PC runs Windows build {CurrentBuild}.\n\nIRIS will close now.";
}
