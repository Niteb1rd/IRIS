using System.Reflection;
using Iris.Core.Updates;

namespace Iris.App.Services;

internal static class AppVersion
{
    public static string Text { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static ReleaseVersion? Version { get; } = ReleaseVersion.TryParse(Text);

    public static bool IsDev => Version is null || Version.IsDev;

    public static bool Diagnostics => IsDev;
}
