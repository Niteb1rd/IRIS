using Iris.Core.Lookup;

namespace Iris.Core.Config;

public static class HostedConfigSchedule
{
    public static bool IsDue(AppSettings settings, DateTimeOffset utcNow)
    {
        if (!settings.HostedConfigAutoCheck || HostedConfigUrl.Check(settings.HostedConfigUrl) is not null)
            return false;
        if (settings.HostedConfigLastCheck is not { } last)
            return true;
        return last > utcNow || utcNow - last >= CacheSettings.HostedCheckInterval;
    }

    public static bool IsUpdate(AppSettings settings, string fetchedHash) =>
        !string.Equals(fetchedHash, settings.HostedConfigLastHash, StringComparison.OrdinalIgnoreCase);
}
