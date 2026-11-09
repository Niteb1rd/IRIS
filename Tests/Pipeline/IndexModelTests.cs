using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Xunit;

namespace Iris.Tests.Pipeline;

public class IndexModelTests
{
    private const string Handle = "PLAYER1";
    private const double Now = 1000.0;

    private static MarkerRecord Marker(int id, OverlayColor color, RsiState state) =>
        MarkerRecord.Born(id, new PixelRect(100, 100, 80, 14), Now - 10.0) with
        {
            Status = PlateStatus.Confirmed,
            Name = Handle,
            PlateRel = new PixelRect(-40, -20, 80, 34),
            Identity = new MarkerIdentity(Handle, MatchKind.PlayerList, color, PanelOrgs.NotLookedUp, state),
        };

    private static IReadOnlyList<PanelEntry> PanelOf(IndexSnapshot snapshot) =>
        IndexModel.ModelAt(snapshot, Now, PipelineSettings.Default, LiveSettings.Default).Panel;

    [Fact]
    public void ALiveMarkerSetsThePanelColourOverAnOlderEndedOne()
    {
        var snapshot = new IndexSnapshot
        {
            Epoch = 1,
            Markers = new[] { Marker(9, OverlayColor.Red, RsiState.Verified) },
            RecentlyEnded = new[] { Marker(5, OverlayColor.Orange, RsiState.Pending) },
        };

        Assert.Equal(OverlayColor.Red, PanelOf(snapshot).Single().Color);
    }

    [Fact]
    public void AmongEndedMarkersTheNewestSetsThePanelColour()
    {
        var snapshot = new IndexSnapshot
        {
            Epoch = 1,
            RecentlyEnded = new[] { Marker(5, OverlayColor.Orange, RsiState.Pending), Marker(9, OverlayColor.Red, RsiState.Verified) },
        };

        Assert.Equal(OverlayColor.Red, PanelOf(snapshot).Single().Color);
    }
}
