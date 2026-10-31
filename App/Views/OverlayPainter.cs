using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.App.Views;

public enum OverlayLayer
{
    All,
    Brackets,
    Panel,
}

public sealed class OverlayPainter
{
    private static readonly Brush DarkBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0A, 0x0F)));
    private static readonly Brush BoundaryBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x00)));
    private static readonly Brush ToastFill = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x12, 0x1A)));
    private static readonly Brush[] ColorBrushes = new Brush[8];
    private static readonly Brush[] PanelFills = new Brush[8];

    private OverlayModel _model = OverlayModel.Empty;
    private PanelLayoutResult _panel = PanelLayoutResult.Empty;
    private PixelRect _origin;
    private double _screenHeight = BracketMetrics.ReferenceScreenHeight;

    static OverlayPainter()
    {
        foreach (var color in Enum.GetValues<OverlayColor>())
        {
            var (r, g, b) = OverlayPalette.Rgb(color);
            ColorBrushes[(int)color] = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
            PanelFills[(int)color] = Frozen(new SolidColorBrush(Color.FromArgb(0x80, r, g, b)));
        }
    }

    public OverlayLayer Layer { get; set; } = OverlayLayer.All;

    public void SetContent(OverlayModel model, PanelLayoutResult panel, PixelRect origin, double screenHeight)
    {
        _model = model;
        _panel = panel;
        _origin = origin;
        _screenHeight = screenHeight;
    }

    public void Paint(DrawingContext dc)
    {
        var drawBrackets = Layer != OverlayLayer.Panel && _model.Brackets.Count > 0;
        var drawPanel = Layer != OverlayLayer.Brackets && !_panel.IsEmpty;
        var boundary = Layer == OverlayLayer.All ? _model.Boundary : null;
        if (!drawBrackets && !drawPanel && boundary is null)
            return;

        var shift = new TranslateTransform(-_origin.X, -_origin.Y);
        shift.Freeze();
        dc.PushTransform(shift);
        try
        {
            if (boundary is { } area)
                DrawBoundary(dc, area, _screenHeight);

            if (drawBrackets)
            {
                foreach (var item in _model.Brackets)
                    DrawBrackets(dc, item, _screenHeight);
            }

            if (drawPanel)
            {
                DrawPanel(dc);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private static void DrawBoundary(DrawingContext dc, PixelRect area, double screenHeight)
    {
        var stroke = Math.Max(1.0, screenHeight / BracketMetrics.ReferenceScreenHeight * 1.5);
        var inset = (stroke + 2.0) / 2.0;
        var rect = new Rect(area.X + inset, area.Y + inset, Math.Max(0.0, area.Width - 2 * inset), Math.Max(0.0, area.Height - 2 * inset));
        dc.DrawRectangle(null, RoundPen(DarkBrush, stroke + 2.0), rect);
        dc.DrawRectangle(null, RoundPen(BoundaryBrush, stroke), rect);
    }

    private static void DrawBrackets(DrawingContext dc, BracketItem item, double screenHeight)
    {
        var box = item.Box;
        var m = BracketMetrics.For(box, screenHeight);
        var stroke = m.Stroke;
        var outline = m.Outline;

        var left = BracketShape(item.Kind, m.LeftX(box), m.Top, m.Width, m.Height, mirrored: false);
        var right = BracketShape(item.Kind, m.RightX(box), m.Top, m.Width, m.Height, mirrored: true);

        var darkPen = RoundPen(DarkBrush, stroke + 2 * outline);
        var colorPen = RoundPen(ColorBrushes[(int)item.Color], stroke);

        dc.DrawGeometry(null, darkPen, left);
        dc.DrawGeometry(null, darkPen, right);
        dc.DrawGeometry(null, colorPen, left);
        dc.DrawGeometry(null, colorPen, right);
    }

    private static readonly Dictionary<(Brush Brush, double Thickness), Pen> Pens = new();

    private static Pen RoundPen(Brush brush, double thickness)
    {
        if (Pens.TryGetValue((brush, thickness), out var known))
            return known;
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        if (Pens.Count >= 64)
            Pens.Clear();
        Pens[(brush, thickness)] = pen;
        return pen;
    }

    private static Geometry BracketShape(MatchKind kind, double x, double y, double width, double height, bool mirrored)
    {
        Point P(double u, double v) => new(mirrored ? x + (1 - u) * width : x + u * width, y + v * height);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            switch (kind)
            {
                case MatchKind.PlayerList:
                    ctx.BeginFigure(P(1, 0), false, false);
                    ctx.QuadraticBezierTo(P(0.5, 0), P(0.5, 0.12), true, true);
                    ctx.LineTo(P(0.5, 0.38), true, true);
                    ctx.QuadraticBezierTo(P(0.5, 0.5), P(0, 0.5), true, true);
                    ctx.QuadraticBezierTo(P(0.5, 0.5), P(0.5, 0.62), true, true);
                    ctx.LineTo(P(0.5, 0.88), true, true);
                    ctx.QuadraticBezierTo(P(0.5, 1), P(1, 1), true, true);
                    break;

                case MatchKind.OrgList:
                    ctx.BeginFigure(P(1, 0), false, false);
                    ctx.LineTo(P(0, 0), true, true);
                    ctx.LineTo(P(0, 1), true, true);
                    ctx.LineTo(P(1, 1), true, true);
                    break;

                default:
                    ctx.BeginFigure(P(1, 0), false, false);
                    ctx.LineTo(P(0, 0.5), true, true);
                    ctx.LineTo(P(1, 1), true, true);
                    break;
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawPanel(DrawingContext dc)
    {
        var panel = _panel;
        if (panel.IsEmpty)
            return;

        var edge = Math.Max(1.0, panel.FontSize * 0.08);
        var edgePen = RoundPen(DarkBrush, edge * 2);

        foreach (var message in panel.Messages)
        {
            var r = message.Rect;
            dc.DrawRoundedRectangle(ToastFill, null, new Rect(r.X, r.Y, r.Width, r.Height), panel.CornerRadius, panel.CornerRadius);
            for (var i = 0; i < message.Lines.Count; i++)
                DrawOutlinedText(dc, message.Lines[i], r.X + panel.PaddingX, r.Y + panel.PaddingY + i * panel.LineHeight, Math.Max(1.0, r.Width - 2 * panel.PaddingX), panel, edgePen, message.Centered);
        }

        foreach (var slot in panel.Slots)
        {
            var r = slot.Rect;
            dc.DrawRoundedRectangle(PanelFills[(int)slot.Entry.Color], null, new Rect(r.X, r.Y, r.Width, r.Height), panel.CornerRadius, panel.CornerRadius);

            var textX = r.X + panel.PaddingX;
            var textWidth = Math.Max(1.0, r.Width - 2 * panel.PaddingX);
            DrawOutlinedText(dc, slot.Entry.Handle, textX, r.Y + panel.PaddingY, textWidth, panel, edgePen);
            DrawOutlinedText(dc, slot.Entry.OrgsLine, textX, r.Y + panel.PaddingY + panel.LineHeight, textWidth, panel, edgePen);
        }

        if (panel.MoreText is { } more)
        {
            var r = panel.MoreRect;
            DrawOutlinedText(dc, more, r.X + panel.PaddingX, r.Y, Math.Max(1.0, r.Width - 2 * panel.PaddingX), panel, edgePen);
        }
    }

    private static void DrawOutlinedText(
        DrawingContext dc, string text, double x, double lineTop, double maxWidth, PanelLayoutResult panel, Pen edgePen, bool center = false)
    {
        var size = panel.FontSize;
        var formatted = Format(text, size);
        if (formatted.Width > maxWidth && formatted.Width > 0)
        {
            size *= Math.Max(0.7, maxWidth / formatted.Width);
            formatted = Format(text, size);
        }
        formatted.MaxTextWidth = maxWidth;
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;

        if (center)
            x += Math.Max(0, maxWidth - formatted.WidthIncludingTrailingWhitespace) / 2;
        var origin = new Point(x, lineTop + (panel.LineHeight - formatted.Height) / 2);
        var shape = formatted.BuildGeometry(origin);
        if (shape is null)
            return;
        dc.DrawGeometry(null, edgePen, shape);
        dc.DrawGeometry(Brushes.White, null, shape);
    }

    private static FormattedText Format(string text, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayFonts.Bold, size, Brushes.White, 1.0);

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
