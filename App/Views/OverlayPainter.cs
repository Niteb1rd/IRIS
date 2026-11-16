using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.Core.Config;
using Iris.Core.Mining;
using Iris.Core.Overlay;

namespace Iris.App.Views;

public enum OverlayLayer
{
    All,
    Panel,
    Mining,
}

public sealed class OverlayPainter
{
    private static readonly Brush DarkBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0A, 0x0F)));
    private static readonly Brush BoundaryBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x00)));
    private static readonly Brush ToastFill = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x12, 0x1A)));
    private static readonly BracketRasterizer TestBrackets = new();

    private OverlayModel _model = OverlayModel.Empty;
    private PanelLayoutResult _panel = PanelLayoutResult.Empty;
    private PixelRect _origin;
    private double _screenHeight = BracketMetrics.ReferenceScreenHeight;

    static OverlayPainter()
    {
        foreach (var color in Enum.GetValues<OverlayColor>())
        {
            var (r, g, b) = OverlayPalette.Rgb(color);
            EdgeBrushes[(int)color] = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
        }
    }

    public OverlayLayer Layer { get; set; } = OverlayLayer.All;

    public double MiningScale { get; set; } = 1.0;

    public void SetContent(OverlayModel model, PanelLayoutResult panel, PixelRect origin, double screenHeight)
    {
        _model = model;
        _panel = panel;
        _origin = origin;
        _screenHeight = screenHeight;
    }

    public void Paint(DrawingContext dc)
    {
        if (Layer == OverlayLayer.Mining)
        {
            if (_model.Mining is { } label)
                DrawMining(dc, label, _origin);
            return;
        }

        var drawBrackets = Layer != OverlayLayer.Panel && _model.Brackets.Count > 0;
        var drawPanel = !_panel.IsEmpty;
        var boundary = Layer == OverlayLayer.All ? _model.Boundary : null;
        var dossier = Layer == OverlayLayer.All ? _model.Dossier : null;
        var dossierList = Layer == OverlayLayer.All ? _model.DossierList : null;
        if (!drawBrackets && !drawPanel && boundary is null && dossier is null && dossierList is null)
            return;

        var shift = new TranslateTransform(-Math.Round(_origin.X), -Math.Round(_origin.Y));
        shift.Freeze();
        dc.PushTransform(shift);
        try
        {
            if (boundary is { } area)
                DrawBoundary(dc, area, _screenHeight);

            if (dossier is { } place)
                DrawLabelledBox(dc, place, "Dossier", _panel, _screenHeight);

            if (dossierList is { } listPlace)
                DrawLabelledBox(dc, listPlace, "Player List", _panel, _screenHeight);

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

    private static void DrawLabelledBox(DrawingContext dc, PixelRect place, string label, PanelLayoutResult panel, double screenHeight)
    {
        var scale = screenHeight / BracketMetrics.ReferenceScreenHeight;
        var corner = panel.IsEmpty ? 6.0 * scale : panel.CornerRadius;
        dc.DrawRoundedRectangle(ToastFill, null, new Rect(place.X, place.Y, place.Width, place.Height), corner, corner);

        var size = (panel.IsEmpty ? 22.0 * scale : panel.FontSize) * 1.5;
        var text = Format(label, size);
        if (text.WidthIncludingTrailingWhitespace > place.Width * 0.9 && text.WidthIncludingTrailingWhitespace > 0)
            text = Format(label, Math.Max(1.0, size * place.Width * 0.9 / text.WidthIncludingTrailingWhitespace));
        var origin = new Point(place.X + (place.Width - text.WidthIncludingTrailingWhitespace) / 2, place.Y + (place.Height - text.Height) / 2);
        var shape = text.BuildGeometry(origin);
        if (shape is null)
            return;
        dc.DrawGeometry(null, RoundPen(DarkBrush, Math.Max(2.0, size * 0.16)), shape);
        dc.DrawGeometry(Brushes.White, null, shape);
    }

    private static void DrawBrackets(DrawingContext dc, BracketItem item, double screenHeight)
    {
        var extent = BracketMetrics.ExtentOf(item.Box, screenHeight);
        var left = (int)Math.Floor(extent.X);
        var top = (int)Math.Floor(extent.Y);
        var width = (int)Math.Ceiling(extent.Right) - left;
        var height = (int)Math.Ceiling(extent.Bottom) - top;
        if (width <= 0 || height <= 0)
            return;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        TestBrackets.Draw(pixels, stride, width, height, new[] { item }, left, top, screenHeight);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        dc.DrawImage(bitmap, new Rect(left, top, width, height));
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

    public PanelEntryMotion? Motion { get; set; }

    public long NowMs { get; set; }

    public AccentColor Accent { get; set; } = AccentColor.Blue;

    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Argb(PanelLook.TextArgb)));
    private static readonly Brush OrgTextBrush = Frozen(new SolidColorBrush(Argb(PanelLook.OrgTextArgb)));
    private static readonly Brush SecondaryBrush = Frozen(new SolidColorBrush(Argb(PanelLook.SecondaryTextArgb)));
    private static readonly Brush WarningBrush = Frozen(new SolidColorBrush(Argb(PanelLook.WarningTextArgb)));
    private static readonly Brush BoxBrush = Frozen(new SolidColorBrush(Argb(PanelLook.BoxArgb)));
    private static readonly Brush[] EdgeBrushes = new Brush[Enum.GetValues<OverlayColor>().Length];

    private void DrawPanel(DrawingContext dc)
    {
        var panel = _panel;
        if (panel.IsEmpty)
            return;

        var scale = PanelLook.Scale(_screenHeight);
        PanelFrame.DrawDim(dc, panel.Frame, scale);
        PanelFrame.Draw(dc, panel.Frame, scale, PanelFrame.AccentColour(Accent));
        var accent = PanelFrame.AccentBrush(Accent);

        foreach (var message in panel.Messages)
        {
            var r = message.Rect;
            dc.DrawRoundedRectangle(BoxBrush, null, ToRect(r), panel.CornerRadius, panel.CornerRadius);
            var brush = message.Centered ? TextBrush : WarningBrush;
            for (var i = 0; i < message.Lines.Count; i++)
                DrawLine(dc, message.Lines[i], r.X + panel.PaddingX, r.Y + panel.PaddingY + i * panel.LineHeight, Math.Max(1.0, r.Width - 2 * panel.PaddingX), panel, OverlayFonts.Bold, brush, message.Centered);
        }

        foreach (var slot in panel.Slots)
        {
            var look = Motion?.LookOf(slot.Entry, NowMs) ?? EntryLook.Still;
            if (look.Opacity <= 0)
                continue;
            var r = look.DropPx == 0 ? slot.Rect : slot.Rect with { Y = slot.Rect.Y + look.DropPx * scale };
            var faded = look.Opacity < 1;
            if (faded)
                dc.PushOpacity(look.Opacity);
            var box = ToRect(r);
            dc.DrawRoundedRectangle(BoxBrush, null, box, panel.CornerRadius, panel.CornerRadius);
            dc.PushClip(new RectangleGeometry(box, panel.CornerRadius, panel.CornerRadius));
            dc.DrawRectangle(EdgeBrushes[(int)slot.Entry.Color], null, new Rect(r.X, r.Y, panel.EdgeWidth * (1 + look.EdgeGain), r.Height));
            dc.Pop();

            var (textX, textWidth) = panel.TextSpan(r);
            DrawLine(dc, slot.Entry.Handle, textX, r.Y + panel.PaddingY, textWidth, panel, OverlayFonts.Bold, TextBrush);
            for (var i = 0; i < slot.OrgLines.Count; i++)
            {
                var lineTop = r.Y + panel.PaddingY + (i + 1) * panel.LineHeight;
                var line = slot.OrgLines[i];
                if (line.StartsWith(PanelText.NotesPrefix, StringComparison.Ordinal) && slot.Entry.Note is { Length: > 0 } && i == slot.OrgLines.Count - 1)
                    DrawNoteLine(dc, line, textX, lineTop, textWidth, panel, accent);
                else
                    DrawLine(dc, line, textX, lineTop, textWidth, panel, OverlayFonts.Regular, OrgTextBrush);
            }

            if (faded)
                dc.Pop();
        }

        if (panel.MoreText is { } more)
        {
            var r = panel.MoreRect;
            DrawLine(dc, more, r.X + panel.PaddingX, r.Y, Math.Max(1.0, r.Width - 2 * panel.PaddingX), panel, OverlayFonts.Regular, SecondaryBrush);
        }
    }

    private static void DrawNoteLine(DrawingContext dc, string line, double x, double lineTop, double maxWidth, PanelLayoutResult panel, Brush accent)
    {
        var label = PanelText.NotesPrefix.TrimEnd().TrimEnd(':').ToUpperInvariant() + " ";
        var labelText = Format(label, panel.FontSize * 0.8, OverlayFonts.Bold, accent);
        var labelWidth = labelText.WidthIncludingTrailingWhitespace;
        dc.DrawText(labelText, new Point(x, lineTop + (panel.LineHeight - labelText.Height) / 2));
        DrawLine(dc, line.Substring(PanelText.NotesPrefix.Length), x + labelWidth, lineTop, Math.Max(1.0, maxWidth - labelWidth), panel, OverlayFonts.Regular, SecondaryBrush);
    }

    private static void DrawLine(
        DrawingContext dc, string text, double x, double lineTop, double maxWidth, PanelLayoutResult panel, Typeface typeface, Brush brush, bool center = false)
    {
        var size = panel.FontSize;
        var formatted = Format(text, size, typeface, brush);
        if (formatted.Width > maxWidth && formatted.Width > 0)
        {
            size *= Math.Max(0.7, maxWidth / formatted.Width);
            formatted = Format(text, size, typeface, brush);
        }
        if (formatted.Width > maxWidth)
        {
            var cut = TextCut.ToWidth(text, maxWidth, part => Format(part, size, typeface, brush).WidthIncludingTrailingWhitespace);
            formatted = Format(cut, size, typeface, brush);
        }

        if (center)
            x += Math.Max(0, maxWidth - formatted.WidthIncludingTrailingWhitespace) / 2;
        dc.DrawText(formatted, new Point(x, lineTop + (panel.LineHeight - formatted.Height) / 2));
    }

    private static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Color Argb(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static (Rect Box, FormattedText Text, Point Origin) MiningLayout(MiningLabelItem label, double scale = 1.0)
    {
        var settings = MiningSettings.Default;
        var h = Math.Max(6.0, label.DigitHeight * scale);
        var size = h / CapHeightEm;
        var text = new FormattedText(label.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayFonts.Bold, size, MiningBrush(label.TextColour, 0xFF), 1.0);
        var padX = settings.LabelPadXLines * h;
        var padY = settings.LabelPadYLines * h;
        var width = text.WidthIncludingTrailingWhitespace + 2 * padX;
        var height = text.Height + 2 * padY;
        var centre = label.Signal.X + label.Signal.Width / 2.0;
        var top = label.Signal.Bottom + settings.LabelGapLines * h;
        var box = new Rect(Math.Round(centre - width / 2.0), Math.Round(top), Math.Ceiling(width), Math.Ceiling(height));
        return (box, text, new Point(box.X + padX, box.Y + padY));
    }

    private const double CapHeightEm = 0.7;

    private void DrawMining(DrawingContext dc, MiningLabelItem label, PixelRect origin)
    {
        var (box, text, at) = MiningLayout(label, MiningScale);
        var shift = new TranslateTransform(-origin.X, -origin.Y);
        shift.Freeze();
        dc.PushTransform(shift);
        try
        {
            var radius = Math.Max(2.0, box.Height * 0.25);
            dc.DrawRoundedRectangle(MiningBrush(label.BackColour, MiningSettings.Default.LabelBackAlpha), null, box, radius, radius);
            dc.DrawText(text, at);
        }
        finally
        {
            dc.Pop();
        }
    }

    private static Brush MiningBrush(Bgr colour, byte alpha) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B)));

    private static FormattedText Format(string text, double size) => Format(text, size, OverlayFonts.Bold, Brushes.White);

    private static FormattedText Format(string text, double size, Typeface typeface, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
