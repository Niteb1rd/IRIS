
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Iris.App.Services;
using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Views;

public interface IInteractiveHost
{
    AppSettings Settings { get; }

    SocialConfig Config { get; }

    bool Edit(Func<SocialConfig, SocialConfig> change);

    MarkerIdentity IdentityOf(RosterEntry entry);

    void OpenDossier(RosterEntry entry);

    void Exit(bool returnToGame);

    void OpenRsiPage(string url);

    void CloseDossier();

    IReadOnlyList<string> Search(string text);

    void OpenDossierFor(string handle);

    bool SaveNote(string handle, string? citizenRecord, string text);

    void RefreshDossier();

    string? NoteFor(string handle);
}

public sealed record DossierView(string Handle, string? Moniker, string? CitizenRecord, MarkerIdentity Identity, IReadOnlyList<OrgMembership> Orgs, bool Loading, string NoteText);

public sealed partial class InteractiveWindow : Window
{
    private const byte RowAlpha = 0x66;
    private const byte HoverAlpha = 0xCC;
    private const byte ScrimAlpha = 0x66;

    private const double CloseButtonLines = 2.2;

    private static readonly Brush CloseGrey = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA8)));
    private static readonly Brush CloseHover = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));

    private readonly IInteractiveHost _host;
    private readonly PixelRect _bounds;
    private readonly IReadOnlyList<RosterEntry> _roster;
    private readonly Dictionary<string, (Border Row, RosterEntry Entry)> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Canvas _root = new();
    private TextBlock? _message;
    private int _rowCharsPerLine = 1;
    private bool _closing;

    public InteractiveWindow(IInteractiveHost host, PixelRect gameBounds, IReadOnlyList<RosterEntry> roster)
    {
        _host = host;
        _bounds = gameBounds;
        _roster = roster;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "IRIS Interactive Overlay";
        Background = Brushes.Transparent;
        _root.Background = Frozen(new SolidColorBrush(Color.FromArgb(ScrimAlpha, 0, 0, 0)));
        Content = _root;

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => RequestExit(returnToGame: false);
        Closed += (_, _) => StopNotesTimer();
    }

    public double Scale { get; private set; } = 1.0;

    public Canvas Root => _root;

    public PixelRect GameBounds => _bounds;

    public bool IsTyping => Keyboard.FocusedElement is TextBox;

    public void CloseQuietly()
    {
        _closing = true;
        Close();
    }

    public void RefreshColours()
    {
        RefreshRows();
        RefreshDossierColours();
    }

    public void RefreshRow(string handle)
    {
        if (_rows.TryGetValue(handle, out var row))
            FillRow(row.Row, row.Entry);
    }

    public void RefreshRows()
    {
        foreach (var (row, entry) in _rows.Values)
            FillRow(row, entry);
    }

    public void ShowMessage(string text)
    {
        if (_message is null)
            return;
        _message.Text = text;
        _message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, (int)Math.Round(_bounds.X), (int)Math.Round(_bounds.Y),
            (int)Math.Round(_bounds.Width), (int)Math.Round(_bounds.Height), 0);
        CaptureExclusion.Apply(handle, _host.Settings.OverlayShownInCapture);
        Scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (Scale <= 0)
            Scale = 1.0;
        _root.Width = _bounds.Width / Scale;
        _root.Height = _bounds.Height / Scale;
        BuildList();
        BuildSearch();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None)
            return;
        e.Handled = true;
        RequestExit(returnToGame: true);
    }

    private void RequestExit(bool returnToGame)
    {
        if (_closing)
            return;
        _closing = true;
        _host.Exit(returnToGame);
    }


    public double FontPx => PanelLayout.ScaledTextSize(_host.Settings.PanelTextSize, _bounds.Height);

    public (double Left, double Top, double Width, double Bottom) PanelPx()
        => DossierText.PanelArea(_host.Settings, _bounds.Width, _bounds.Height);

    public TextBlock Text(string text, double sizePx, Brush? brush = null) => new()
    {
        Text = text,
        FontFamily = OverlayFonts.Bold.FontFamily,
        FontWeight = FontWeights.Bold,
        FontSize = Math.Max(1, sizePx / Scale),
        Foreground = brush ?? Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };


    private void BuildList()
    {
        var font = FontPx;
        var paddingX = font * 0.7 / Scale;
        var paddingY = font * 0.45 / Scale;
        var gap = font * 0.4 / Scale;
        var corner = font * 0.5 / Scale;
        var (left, top, width, bottom) = PanelPx();

        var panel = new DockPanel { Width = width / Scale, Height = Math.Max(0, bottom - top) / Scale, LastChildFill = true };
        Canvas.SetLeft(panel, left / Scale);
        Canvas.SetTop(panel, top / Scale);

        var header = new Border
        {
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x12, 0x1A))),
            CornerRadius = new CornerRadius(corner),
            Padding = new Thickness(paddingX, paddingY, paddingX, paddingY),
            Margin = new Thickness(0, 0, 0, gap),
        };
        var headerLines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerLines.Children.Add(Text("IRIS Interactive Overlay", font));
        headerLines.Children.Add(Text("Toggle: " + KeyText(_host.Settings.HotkeyInteractiveOverlay), font * 0.85));
        _message = Text(string.Empty, font * 0.85, Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40))));
        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextTrimming = TextTrimming.None;
        _message.Visibility = Visibility.Collapsed;
        headerLines.Children.Add(_message);
        var headerRow = new DockPanel { LastChildFill = true };
        var close = MakeCloseButton(font * CloseButtonLines, () => RequestExit(returnToGame: true));
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(gap, 0, 0, 0);
        DockPanel.SetDock(close, Dock.Right);
        headerRow.Children.Add(close);
        headerRow.Children.Add(headerLines);
        header.Child = headerRow;
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var rows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        panel.Children.Add(scroll);

        var charsPerLine = PanelLayout.CharsPerLine(width - 2 * font * 0.7, font);
        var shown = _roster;

        if (shown.Count == 0)
        {
            var empty = MakeRowShell(OverlayColor.Grey, paddingX, paddingY, gap, corner);
            empty.Child = Text("No recent players encountered.", font);
            rows.Children.Add(empty);
        }

        bool dividerShown = false;
        foreach (var entry in shown)
        {
            if (!entry.Recent && !dividerShown && rows.Children.Count > 0)
            {
                rows.Children.Add(new Rectangle { Height = Math.Max(1, 2 / Scale), Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))), Margin = new Thickness(0, 0, 0, gap) });
                dividerShown = true;
            }
            rows.Children.Add(MakePlayerRow(entry, charsPerLine, paddingX, paddingY, gap, corner));
        }

        _root.Children.Add(panel);
    }

    private Border MakePlayerRow(RosterEntry entry, int charsPerLine, double paddingX, double paddingY, double gap, double corner)
    {
        var row = MakeRowShell(_host.IdentityOf(entry).Color, paddingX, paddingY, gap, corner);
        _rowCharsPerLine = charsPerLine;
        FillRow(row, entry);

        row.MouseEnter += (_, _) => SetRowColour(row, _host.IdentityOf(entry).Color, hover: true);
        row.MouseLeave += (_, _) => SetRowColour(row, _host.IdentityOf(entry).Color, hover: false);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _host.OpenDossier(entry);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = ColourMenus.ForPlayer(_host, entry.Handle, RefreshColours);
            menu.PlacementTarget = row;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
        _rows[entry.Handle] = (row, entry);
        return row;
    }

    private void FillRow(Border row, RosterEntry entry)
    {
        var font = FontPx;
        var identity = _host.IdentityOf(entry);
        var lines = new StackPanel();
        lines.Children.Add(Text(entry.Handle, font));
        var settings = _host.Settings;
        foreach (var line in PanelText.Lines(identity.Orgs, settings.PanelOrgDisplay, settings.PanelMainOrgOnly, _rowCharsPerLine))
            lines.Children.Add(Text(line, font));
        AddExtraRowLines(entry, lines, font, _rowCharsPerLine);
        row.Child = lines;
        SetRowColour(row, identity.Color, hover: row.IsMouseOver);
    }

    partial void AddExtraRowLines(RosterEntry entry, StackPanel lines, double font, int charsPerLine);

    private static Border MakeRowShell(OverlayColor color, double paddingX, double paddingY, double gap, double corner)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(corner),
            Padding = new Thickness(paddingX, paddingY, paddingX, paddingY),
            Margin = new Thickness(0, 0, 0, gap),
            Cursor = Cursors.Hand,
        };
        SetRowColour(row, color, hover: false);
        return row;
    }

    public static void SetRowColour(Border box, OverlayColor color, bool hover) => box.Background = ColourBrush(color, hover ? HoverAlpha : RowAlpha);

    public static Brush NoColourBrush { get; } = Frozen(new SolidColorBrush(Color.FromArgb(RowAlpha, 0, 0, 0)));

    public static Brush ColourBrush(OverlayColor color, byte alpha)
    {
        var (r, g, b) = OverlayPalette.Rgb(color);
        return Frozen(new SolidColorBrush(Color.FromArgb(alpha, r, g, b)));
    }

    private static string KeyText(string text) => text.Length == 0 ? "(none)" : text;

    private Border MakeCloseButton(double sidePx, Action onClick)
    {
        var side = sidePx / Scale;
        var stroke = Math.Max(1, sidePx * 0.08 / Scale);
        var inner = Math.Max(1, side - 2 * stroke);
        var inset = inner * 0.26;
        var cross = new Canvas { Width = inner, Height = inner, IsHitTestVisible = false };
        cross.Children.Add(new Line { X1 = inset, Y1 = inset, X2 = inner - inset, Y2 = inner - inset, Stroke = CloseGrey, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        cross.Children.Add(new Line { X1 = inner - inset, Y1 = inset, X2 = inset, Y2 = inner - inset, Stroke = CloseGrey, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var button = new Border
        {
            Width = side,
            Height = side,
            BorderBrush = CloseGrey,
            BorderThickness = new Thickness(stroke),
            CornerRadius = new CornerRadius(side * 0.15),
            Background = NoColourBrush,
            Cursor = Cursors.Hand,
            Child = cross,
            ToolTip = "Close and save",
        };
        button.MouseEnter += (_, _) => button.Background = CloseHover;
        button.MouseLeave += (_, _) => button.Background = NoColourBrush;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return button;
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
