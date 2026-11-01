using System.Text.RegularExpressions;

namespace Iris.Core.Lookup;

public static class RsiNames
{
    public const int MinHandleLength = 4;

    public const int MaxHandleLength = 64;

    private static readonly Regex HandlePattern = new(
        "^[A-Za-z0-9_-]{" + MinHandleLength + "," + MaxHandleLength + "}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex SidPattern = new(@"^[A-Za-z0-9_-]{1,20}\z", RegexOptions.CultureInvariant);

    public static bool IsValidHandle(string? handle) => handle is not null && HandlePattern.IsMatch(handle);

    public static bool IsValidSid(string? sid) => sid is not null && SidPattern.IsMatch(sid);
}

public static class RsiUrls
{
    public const string Host = "robertsspaceindustries.com";
    public const string BaseUrl = "https://robertsspaceindustries.com";

    public const string OrgMembersApi = BaseUrl + "/api/orgs/getOrgMembers";

    public static string PlayerOrgs(string handle)
    {
        if (!RsiNames.IsValidHandle(handle))
            throw new ArgumentException("invalid name", nameof(handle));
        return $"{BaseUrl}/en/citizens/{handle}/organizations";
    }

    public static string OrgPage(string sid)
    {
        if (!RsiNames.IsValidSid(sid))
            throw new ArgumentException("invalid name", nameof(sid));
        return $"{BaseUrl}/en/orgs/{sid}";
    }

    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);
}
