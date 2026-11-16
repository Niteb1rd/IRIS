using System;
using System.Windows;
using System.Windows.Media;
using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.App.Views;

internal static class PanelFrame
{
    private static readonly Brush GlassBrush = Frozen(new SolidColorBrush(Argb(PanelLook.GlassArgb)));
    private static readonly Color BackEdge = Argb(PanelLook.BackEdgeArgb);
    private static readonly Brush[] AccentBrushes = new Brush[Enum.GetValues<AccentColor>().Length];

    static PanelFrame()
    {
        foreach (var accent in Enum.GetValues<AccentColor>())
            AccentBrushes[(int)accent] = Frozen(new SolidColorBrush(AccentColour(accent)));
    }

    public static Color AccentColour(AccentColor accent)
    {
        var (r, g, b) = PanelLook.AccentRgb(accent);
        return Color.FromRgb(r, g, b);
    }

    public static Brush AccentBrush(AccentColor accent) => Enum.IsDefined(accent) ? AccentBrushes[(int)accent] : AccentBrushes[(int)AccentColor.Blue];

    public static void Draw(DrawingContext dc, PixelRect frame, double scale, Color accent)
    {
        if (frame.IsEmpty)
            return;

        var radius = Math.Min(PanelLook.FrameRadiusPx * scale, Math.Min(frame.Width, frame.Height) / 2);
        var box = new Rect(frame.X, frame.Y, frame.Width, frame.Height);

        var backShift = PanelLook.BackEdgeOffsetPx * scale;
        var backThick = Math.Max(1.0, PanelLook.BackEdgePx * scale);
        DrawCap(dc, box, radius, backShift, backThick, BackEdge, top: true, scale);
        DrawCap(dc, box, radius, backShift, backThick, BackEdge, top: false, scale);

        dc.DrawRoundedRectangle(GlassBrush, null, box, radius, radius);

        var glowHeight = Math.Min(PanelLook.GlowPx * scale, frame.Height);
        var glow = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, frame.Y),
            EndPoint = new Point(0, frame.Y + glowHeight),
        };
        glow.GradientStops.Add(new GradientStop(WithAlpha(accent, PanelLook.GlowOpacity), 0));
        glow.GradientStops.Add(new GradientStop(WithAlpha(accent, 0), 1));
        glow.Freeze();
        dc.PushClip(new RectangleGeometry(box, radius, radius));
        dc.DrawRectangle(glow, null, new Rect(frame.X, frame.Y, frame.Width, glowHeight));
        dc.Pop();

        var lineThick = Math.Max(1.0, PanelLook.AccentLinePx * scale);
        DrawCap(dc, box, radius, 0, lineThick, accent, top: true, scale);
        DrawCap(dc, box, radius, 0, lineThick, accent, top: false, scale);
    }

    private static void DrawCap(DrawingContext dc, Rect box, double radius, double shift, double thickness, Color colour, bool top, double scale)
    {
        var half = thickness / 2;
        var left = box.Left + half;
        var right = box.Right - half;
        var r = Math.Max(0.0, radius - half);
        var fullSolid = radius + PanelLook.AccentSidePx * scale;
        var fullReach = fullSolid + PanelLook.AccentFadePx * scale;
        var reach = Math.Min(fullReach, box.Height / 2);
        var solid = reach * fullSolid / fullReach;

        var edgeY = top ? box.Top + half + shift : box.Bottom - half + shift;
        var endY = top ? box.Top + reach + shift : box.Bottom - reach + shift;
        var cornerY = top ? edgeY + r : edgeY - r;

        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(new Point(left, endY), false, false);
            ctx.LineTo(new Point(left, cornerY), true, true);
            ctx.ArcTo(new Point(left + r, edgeY), new Size(r, r), 0, false, top ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new Point(right - r, edgeY), true, true);
            ctx.ArcTo(new Point(right, cornerY), new Size(r, r), 0, false, top ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new Point(right, endY), true, true);
        }
        path.Freeze();

        var fade = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, top ? box.Top + shift : box.Bottom + shift),
            EndPoint = new Point(0, endY),
        };
        fade.GradientStops.Add(new GradientStop(colour, 0));
        fade.GradientStops.Add(new GradientStop(colour, reach > 0 ? solid / reach : 1));
        fade.GradientStops.Add(new GradientStop(WithAlpha(colour, 0), 1));
        fade.Freeze();

        var pen = new Pen(fade, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, path);
    }

    public static void DrawDim(DrawingContext dc, PixelRect frame, double scale)
    {
        if (frame.IsEmpty)
            return;

        var radius = Math.Round(PanelLook.FrameRadiusPx * scale);
        var corner = radius + Math.Round(PanelLook.DimSolidPx * scale);
        var reach = corner + Math.Round(PanelLook.DimFadePx * scale);

        var cx1 = Math.Round(frame.X) + radius;
        var cx2 = Math.Max(cx1, Math.Round(frame.Right) - radius);
        var cy1 = Math.Round(frame.Y) + radius;
        var cy2 = Math.Max(cy1, Math.Round(frame.Bottom) - radius);
        var dark = Color.FromArgb((byte)Math.Round(255 * PanelLook.DimOpacity), 0, 0, 0);
        var share = corner / reach;

        var vertical = Linear(new Point(0, cy1 - reach), new Point(0, cy2 + reach), dark, (cy1 - reach, cy1 - corner), (cy2 + corner, cy2 + reach));
        dc.DrawRectangle(vertical, null, new Rect(cx1, cy1 - reach, cx2 - cx1, cy2 - cy1 + 2 * reach));

        var horizontal = Linear(new Point(cx1 - reach, 0), new Point(cx2 + reach, 0), dark, (cx1 - reach, cx1 - corner), (cx2 + corner, cx2 + reach));
        dc.DrawRectangle(horizontal, null, new Rect(cx1 - reach, cy1, reach, cy2 - cy1));
        dc.DrawRectangle(horizontal, null, new Rect(cx2, cy1, reach, cy2 - cy1));

        foreach (var (x, y, left, up) in new[] { (cx1, cy1, true, true), (cx2, cy1, false, true), (cx1, cy2, true, false), (cx2, cy2, false, false) })
        {
            var radial = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(x, y),
                GradientOrigin = new Point(x, y),
                RadiusX = reach,
                RadiusY = reach,
            };
            AddFade(radial.GradientStops, dark, share);
            radial.Freeze();
            dc.DrawRectangle(radial, null, new Rect(left ? x - reach : x, up ? y - reach : y, reach, reach));
        }
    }

    private static Brush Linear(Point start, Point end, Color dark, (double From, double To) rise, (double From, double To) fall)
    {
        var length = Math.Max(1e-6, (end - start).Length);
        var origin = start.X != end.X ? start.X : start.Y;
        double At(double v) => Math.Clamp((v - origin) / length, 0, 1);

        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(WithAlpha(dark, 0), At(rise.From)));
        brush.GradientStops.Add(new GradientStop(WithAlpha(dark, Share(dark) * 0.35), At((rise.From + rise.To) / 2)));
        brush.GradientStops.Add(new GradientStop(dark, At(rise.To)));
        brush.GradientStops.Add(new GradientStop(dark, At(fall.From)));
        brush.GradientStops.Add(new GradientStop(WithAlpha(dark, Share(dark) * 0.35), At((fall.From + fall.To) / 2)));
        brush.GradientStops.Add(new GradientStop(WithAlpha(dark, 0), At(fall.To)));
        brush.Freeze();
        return brush;
    }

    private static void AddFade(GradientStopCollection stops, Color dark, double share)
    {
        stops.Add(new GradientStop(dark, 0));
        stops.Add(new GradientStop(dark, share));
        stops.Add(new GradientStop(WithAlpha(dark, Share(dark) * 0.35), (share + 1) / 2));
        stops.Add(new GradientStop(WithAlpha(dark, 0), 1));
    }

    private static double Share(Color colour) => colour.A / 255.0;

    private static Color WithAlpha(Color colour, double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), colour.R, colour.G, colour.B);

    private static Color Argb(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
