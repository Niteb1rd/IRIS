using Iris.Core;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Iris.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Iris.Tests.Pipeline;

public class PlateNameJudgeTests
{
    private static PlateNameJudge Judge(Func<string, IReadOnlyList<OrgMembership>?> cached) => new(
        ExclusionList.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new FakeClock(), NullLog.Instance),
        new SessionExclusions(), cached, null, LiveSettings.Default);

    [Fact]
    public void APlayerInTheCacheIsKnownEvenWithNoOrgs()
    {
        Assert.True(Judge(_ => Array.Empty<OrgMembership>()).IsKnownPlayer("SOMEPLAYER"));
    }

    [Fact]
    public void AnUncachedPlayerIsNotKnown()
    {
        Assert.False(Judge(_ => null).IsKnownPlayer("SOMEPLAYER"));
    }

    [Fact]
    public void APlayerOnTheUsersListIsKnown()
    {
        var judge = Judge(_ => null);
        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry("SOMEPLAYER", default));
        judge.Config = config;

        Assert.True(judge.IsKnownPlayer("SOMEPLAYER"));
    }

    [Fact]
    public void ADatabaseErrorMeansNotKnown()
    {
        Assert.False(Judge(_ => throw new SqliteException("locked", 5)).IsKnownPlayer("SOMEPLAYER"));
    }
}
