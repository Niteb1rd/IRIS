using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.App.Views;

internal sealed class GlassFrameElement : FrameworkElement
{
    private readonly double _lookScale;
    private readonly Color _accent;

    public GlassFrameElement(double lookScale, Color accent)
    {
        _lookScale = lookScale;
        _accent = accent;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc) =>
        PanelFrame.Draw(dc, new PixelRect(0, 0, ActualWidth, ActualHeight), _lookScale, _accent);
}

internal static class GlassParts
{
    public static readonly Brush TextBrush = Frozen(Argb(PanelLook.TextArgb));
    public static readonly Brush OrgTextBrush = Frozen(Argb(PanelLook.OrgTextArgb));
    public static readonly Brush SecondaryBrush = Frozen(Argb(PanelLook.SecondaryTextArgb));
    public static readonly Brush WarningBrush = Frozen(Argb(PanelLook.WarningTextArgb));
    public static readonly Brush BoxBrush = Frozen(Argb(PanelLook.BoxArgb));
    public static readonly Brush BoxHoverBrush = Frozen(Argb(PanelLook.BoxHoverArgb));
    public static readonly Brush PillBrush = Frozen(Argb(PanelLook.PillArgb));
    public static readonly Brush SearchPillBrush = Frozen(Argb(PanelLook.SearchPillArgb));
    public static readonly Brush PopupBrush = Frozen(Argb(PanelLook.PopupArgb));
    public static readonly Brush MenuBrush = Frozen(Argb(PanelLook.MenuArgb));
    public static readonly Brush MenuHighlightBrush = Frozen(Argb(PanelLook.MenuHighlightArgb));
    public static readonly Brush MenuBorderBrush = Frozen(Argb(PanelLook.MenuBorderArgb));
    private static readonly Brush CloseRing = Frozen(Argb(PanelLook.CloseRingArgb));
    private static readonly Brush CloseFill = Frozen(Argb(PanelLook.CloseFillArgb));

    public static Brush Scrim { get; } = Frozen(Color.FromArgb((byte)Math.Round(255 * PanelLook.ScrimOpacity), 0, 0, 0));

    public static Grid Framed(UIElement content, double look, Color accent)
    {
        var grid = new Grid();
        grid.Children.Add(new GlassFrameElement(look, accent));
        grid.Children.Add(new Border { Padding = new Thickness(PanelLook.FramePadPx * look), Child = content });
        return grid;
    }

    public static Brush EdgeFill(OverlayColor? edge, double edgeWidth, Brush fill)
    {
        if (edge is not { } colour || edgeWidth <= 0 || fill is not SolidColorBrush plain)
            return fill;
        var (r, g, b) = OverlayPalette.Rgb(colour);
        var edgeColour = Color.FromRgb(r, g, b);
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(2 * edgeWidth, 0) };
        brush.GradientStops.Add(new GradientStop(edgeColour, 0));
        brush.GradientStops.Add(new GradientStop(edgeColour, 0.5));
        brush.GradientStops.Add(new GradientStop(plain.Color, 0.5));
        brush.GradientStops.Add(new GradientStop(plain.Color, 1));
        brush.Freeze();
        return brush;
    }

    public static Border HalfCircle(OverlayColor colour, double height)
    {
        var (r, g, b) = OverlayPalette.Rgb(colour);
        var half = height / 2;
        return new Border
        {
            Width = half,
            Height = height,
            CornerRadius = new CornerRadius(half, 0, 0, half),
            Background = Frozen(Color.FromRgb(r, g, b)),
            Margin = new Thickness(0, 0, half, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static StackPanel SubHeading(string label, TextBlock text, double look, Brush accent)
    {
        text.Text = label.ToUpperInvariant();
        var bar = new Rectangle { Width = Math.Max(1, PanelLook.SubBarPx * look), Height = text.FontSize * 0.75, Fill = accent, Margin = new Thickness(0, 0, text.FontSize * 0.5, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(bar);
        row.Children.Add(text);
        return row;
    }

    public static Border Pill(UIElement titles, UIElement? right, double height, double look, Color accent)
    {
        var row = new DockPanel { LastChildFill = true };
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }

        DockPanel.SetDock(titles, Dock.Left);
        row.Children.Add(titles);
        row.Children.Add(Dots(look, accent, height * 0.45));
        var pill = new Border
        {
            Background = PillBrush,
            CornerRadius = new CornerRadius(height / 2),
            MinHeight = height,
            Padding = new Thickness(height * 0.45, height * 0.1, height * 0.14, height * 0.1),
            Child = row,
        };
        pill.SizeChanged += (_, e) => pill.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        return pill;
    }

    private static Rectangle Dots(double look, Color accent, double height)
    {
        var pitch = Math.Max(2, PanelLook.DotPitchPx * look);
        var dot = Math.Max(0.5, look);
        var drawing = new GeometryDrawing(Frozen(Color.FromArgb((byte)Math.Round(255 * PanelLook.DotOpacity), accent.R, accent.G, accent.B)), null,
            new EllipseGeometry(new Point(dot, dot), dot / 2, dot / 2));
        var tile = new DrawingBrush(new DrawingGroup { Children = { new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pitch, pitch))), drawing } })
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, pitch, pitch),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        tile.Freeze();
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.6));
        fade.Freeze();
        return new Rectangle { Height = height, Fill = tile, OpacityMask = fade, Margin = new Thickness(height * 0.6, 0, height * 0.6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    public static Border CloseButton(double side, string tip, Action onClick)
    {
        var stroke = Math.Max(1, side * 0.07);
        var ring = Math.Max(1, side * 0.03);
        var inner = Math.Max(1, side - 2 * ring);
        var inset = inner * 0.31;
        var cross = new Canvas { Width = inner, Height = inner, IsHitTestVisible = false };
        cross.Children.Add(new Line { X1 = inset, Y1 = inset, X2 = inner - inset, Y2 = inner - inset, Stroke = TextBrush, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        cross.Children.Add(new Line { X1 = inner - inset, Y1 = inset, X2 = inset, Y2 = inner - inset, Stroke = TextBrush, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var button = new Border
        {
            Width = side,
            Height = side,
            CornerRadius = new CornerRadius(side / 2),
            BorderBrush = CloseRing,
            BorderThickness = new Thickness(ring),
            Background = CloseFill,
            Cursor = Cursors.Hand,
            Child = cross,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.MouseEnter += (_, _) => button.Background = BoxHoverBrush;
        button.MouseLeave += (_, _) => button.Background = CloseFill;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return button;
    }

    public static Color AccentColour(AccentColor accent) => PanelFrame.AccentColour(accent);

    public static Brush AccentAt(AccentColor accent, double opacity)
    {
        var c = PanelFrame.AccentColour(accent);
        return Frozen(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B));
    }

    private static Color Argb(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
