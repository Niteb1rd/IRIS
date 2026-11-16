namespace Iris.Core.Config;

public static class HostedConfigUrl
{
    public static string? Check(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return "Enter a web address that starts with https://";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return "That is not a valid web address. It must start with https://";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "Only https:// addresses are allowed.";
        if (uri.Host.Length == 0)
            return "That web address has no site name.";
        if (uri.UserInfo.Length > 0)
            return "Addresses with a user name or password are not allowed.";
        if (!IsPublicSiteName(uri))
            return "Use the address of a public website (not an IP address, localhost or a local network name).";
        return null;
    }

    private static bool IsPublicSiteName(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 or UriHostNameType.Basic or UriHostNameType.Unknown)
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Length == 0 || !host.Contains('.'))
            return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
