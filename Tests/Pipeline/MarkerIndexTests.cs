using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using Xunit;

namespace Iris.Tests.Pipeline;

public class MarkerIndexTests
{
    private static readonly PixelRect Line = new(100, 100, 80, 14);

    private readonly MarkerIndex _index = new(PipelineSettings.Default);

    private MarkerRecord Add(double? metres)
    {
        var epoch = _index.Current.Epoch;
        var update = metres is { } known
            ? new NewMarkerUpdate(PipelineGroup.Scan, 100.0, epoch, Line, null, false, known)
            : new NewMarkerUpdate(PipelineGroup.Scan, 100.0, epoch, Line, null);
        return _index.Apply(new[] { update }, 110.0).Markers.Single();
    }

    [Fact]
    public void ANewMarkerRemembersTheDistanceItWasFoundAt()
    {
        Assert.Equal(4300.0, Add(4300.0).DistanceMetres);
    }

    [Fact]
    public void AMarkerFoundWithoutADistanceHasNone()
    {
        Assert.True(double.IsNaN(Add(null).DistanceMetres));
    }

    [Fact]
    public void TheDistanceSurvivesLaterPositionUpdates()
    {
        var marker = Add(4300.0);

        var moved = _index.Apply(new[] { new PositionUpdate(PipelineGroup.Tracking, 120.0, _index.Current.Epoch, marker.Id, Line with { X = 104 }) }, 130.0);

        Assert.Equal(4300.0, moved.Markers.Single().DistanceMetres);
    }
}
