using Iris.Core.Config;

namespace Iris.App.Views;

public sealed class RankStripItem
{
    public RankStripItem(bool orgs, OverlayColor color, int rank, int count)
    {
        Orgs = orgs;
        Color = color;
        Rank = rank;
        CanMoveUp = rank > 1;
        CanMoveDown = rank < count;
    }

    public bool Orgs { get; }

    public OverlayColor Color { get; }

    public int Rank { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }
}
