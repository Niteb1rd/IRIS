using Iris.Core.Config;
using Xunit;

namespace Iris.Tests.Config;

public class SocialListEditsTests
{
    private const string Sid = "ORG1";

    private static SocialConfig Config()
    {
        var config = new SocialConfig();
        config.Orgs.Add(new OrgEntry(Sid, 1, OverlayColor.Red));
        config.Orgs.Add(new OrgEntry("ORG2", 2, OverlayColor.Orange));
        config.Orgs.Add(new OrgEntry("ORG3", 3, OverlayColor.Yellow));
        config.Orgs.Add(new OrgEntry("ORG4", 4, OverlayColor.Green));
        return config;
    }

    [Fact]
    public void ChoosingARankInTheMenuPutsTheColourOnThatRank()
    {
        var config = Config();
        var count = ColorRanks.OrgRanking(config).Count;

        for (var rank = 1; rank <= count; rank++)
        {
            var moved = SocialListEdits.SetOrgColorRank(config, Sid, rank);

            Assert.Equal(rank, ColorRanks.RankOf(ColorRanks.OrgRanking(moved), OverlayColor.Red));
        }
    }
}
