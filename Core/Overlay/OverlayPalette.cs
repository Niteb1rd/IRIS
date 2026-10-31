using Iris.Core.Config;

namespace Iris.Core.Overlay;

public static class OverlayPalette
{
    public static (byte R, byte G, byte B) Rgb(OverlayColor color) => color switch
    {
        OverlayColor.Red => (0xFF, 0x3B, 0x30),
        OverlayColor.Orange => (0xFF, 0x95, 0x00),
        OverlayColor.Yellow => (0xFF, 0xE6, 0x00),
        OverlayColor.Green => (0x2E, 0xE6, 0x5A),
        OverlayColor.Cyan => (0x00, 0xE5, 0xFF),
        OverlayColor.Blue => (0x3D, 0x8B, 0xFF),
        OverlayColor.Magenta => (0xFF, 0x3D, 0xDB),
        OverlayColor.Purple => (0xA6, 0x6B, 0xFF),
        _ => (0xFF, 0xFF, 0xFF),
    };
}
