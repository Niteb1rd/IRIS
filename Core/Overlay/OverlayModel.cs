using Iris.Core.Config;

namespace Iris.Core.Overlay;

public sealed record DetectedPlayer(string Handle, PixelRect Box);

public sealed record BracketItem(PixelRect Box, MatchKind Kind, OverlayColor Color);

public sealed record PanelEntry(string Handle, MatchKind Kind, OverlayColor Color, string OrgsLine);

public sealed class OverlayModel
{
    public static readonly OverlayModel Empty = new(Array.Empty<BracketItem>(), Array.Empty<PanelEntry>());

    public OverlayModel(IReadOnlyList<BracketItem> brackets, IReadOnlyList<PanelEntry> panel, PixelRect? boundary = null, string? note = null)
    {
        Brackets = brackets;
        Panel = panel;
        Boundary = boundary;
        Note = note;
    }

    public PixelRect? Boundary { get; }

    public IReadOnlyList<BracketItem> Brackets { get; }

    public IReadOnlyList<PanelEntry> Panel { get; }

    public string? Note { get; }

    public bool IsEmpty => Brackets.Count == 0 && Panel.Count == 0 && Note is null;
}
