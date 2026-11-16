using Iris.Core.Config;

namespace Iris.Core.Overlay;

public static class PanelLook
{
    public const double ReferenceHeight = 1440;

    public static double Scale(double windowHeight) => windowHeight / ReferenceHeight;

    public const double FramePadPx = 22;

    public const double FrameRadiusPx = 18;

    public const double AccentLinePx = 3;

    public const double AccentSidePx = 28;

    public const double AccentFadePx = 28;

    public const double GlowPx = 16;

    public const double GlowOpacity = 0.30;

    public const double BackEdgeOffsetPx = 5;

    public const double BackEdgePx = 2;

    public const uint BackEdgeArgb = 0x8C96A0AA;

    public const uint GlassArgb = 0x1296B4D2;

    public const double DimOpacity = 0.65;

    public const double DimSolidPx = 20;

    public const double DimFadePx = 35;

    public const double HaloPx = DimSolidPx + DimFadePx;

    public const uint BoxArgb = 0x13FFFFFF;

    public const uint BoxHoverArgb = 0x21FFFFFF;

    public const double SelectedFillOpacity = 0.22;

    public const double SelectedRingOpacity = 0.85;

    public const double SelectedRingPx = 2;

    public const double ScrimOpacity = 0.75;

    public const uint PillArgb = 0x8C080A0C;

    public const uint SearchPillArgb = 0x99080A0C;

    public const uint PopupArgb = 0xF7161B20;

    public const uint MenuArgb = 0xFF161B20;

    public const uint MenuHighlightArgb = 0xFF34393D;

    public const uint MenuBorderArgb = 0xFF3A424A;

    public const double DotOpacity = 0.35;

    public const double DotPitchPx = 6;

    public const uint CloseRingArgb = 0x8CE8ECEF;

    public const uint CloseFillArgb = 0x990A0C0E;

    public const double SubBarPx = 3;

    public const uint TextArgb = 0xFFE8ECEF;

    public const uint OrgTextArgb = 0xFFB7C0C8;

    public const uint SecondaryTextArgb = 0xFF9AA4AD;

    public const uint WarningTextArgb = 0xFFFFB040;

    public static (byte R, byte G, byte B) AccentRgb(AccentColor color) => color switch
    {
        AccentColor.Cyan => (0x3F, 0xD0, 0xE0),
        AccentColor.Mint => (0x4F, 0xD1, 0xA5),
        AccentColor.Green => (0x6B, 0xD3, 0x5C),
        AccentColor.Amber => (0xE8, 0xA2, 0x3A),
        AccentColor.Red => (0xE5, 0x48, 0x4D),
        AccentColor.Magenta => (0xE0, 0x5A, 0xC8),
        AccentColor.Purple => (0x9B, 0x7B, 0xEA),
        AccentColor.White => (0xD8, 0xDE, 0xE3),
        _ => (0x4E, 0xA8, 0xDE),
    };
}
