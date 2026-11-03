
namespace Iris.Core.Detection;

public sealed record NameRowRead(string Text, double Score, bool OneWord, bool ValidChars, LineMask? Mask, IReadOnlyList<O0Glyph> O0)
{
    public static NameRowRead Empty { get; } = new(string.Empty, 0.0, false, false, null, Array.Empty<O0Glyph>());

    public bool Usable => Mask is not null && Text.Length > 0 && OneWord && ValidChars;
}
