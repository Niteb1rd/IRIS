using Iris.Core.Config;

namespace Iris.Core.Detection;

public static class NameFilter
{
    public static bool IsOnPlayerList(string handle, SocialConfig config) =>
        config.Players.Any(p => LookAlikes.NamesEqual(p.Handle, handle));
}
