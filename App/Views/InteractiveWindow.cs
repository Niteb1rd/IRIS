
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    bool IsOpening { get; }

    void OpenRsiPage(string url);

    void CloseDossier();

    IReadOnlyList<SearchHit> Search(string text);

    void OpenDossierFor(string handle);

    void LookUp(string handle);

    bool SaveNote(string handle, string? citizenRecord, string text);

    void RefreshDossier();

    string? NoteFor(string handle);
}

public sealed record DossierView(string Handle, string? Moniker, string? CitizenRecord, MarkerIdentity Identity, IReadOnlyList<OrgMembership> Orgs, bool Loading, string NoteText,
    DateOnly? Enlisted = null, bool EnlistedRead = false, DateTimeOffset? LastSeen = null);

public sealed partial class InteractiveWindow : Window
{
    private const double PillLines = 2.6;

    private const double HeaderSubtitleScale = 0.85;

    private double HeaderTextHeight(double font) => (1 + HeaderSubtitleScale) * font / Scale * OverlayFonts.Bold.FontFamily.LineSpacing;

    private readonly IInteractiveHost _host;
    private readonly PixelRect _bounds;
    private readonly IReadOnlyList<RosterEntry> _roster;
    private readonly Dictionary<string, (Border Row, RosterEntry Entry)> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Canvas _root = new();
    private readonly Grid _stage = new();
    private readonly LayoutClock _layout = new();
    private TextBlock? _message;
    private int _rowCharsPerLine = 1;
    private bool _closing;
    private bool _fading;

    public InteractiveWindow(IInteractiveHost host, PixelRect gameBounds, IReadOnlyList<RosterEntry> roster)
    {
        _host = host;
        _bounds = gameBounds;
        _roster = roster;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "IRIS Interactive Overlay";
        Background = Brushes.Transparent;
        _stage.Background = GlassParts.Scrim;
        _root.Background = Brushes.Transparent;
        Content = _stage;
        _curveDepth = ScreenCurve.DepthFor(host.Settings.UiCurvePercent);
        _listHost.Clock = _layout;
        _dossierHost.Clock = _layout;

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += (_, _) =>
        {
            if (!_host.IsOpening)
                RequestExit(returnToGame: false);
        };
        Closing += OnClosing;
        Closed += (_, _) => StopNotesTimer();
    }

    public static bool CanDraw => (RenderCapability.Tier >> 16) > 0;

    private void MakeGlass(IntPtr handle)
    {
        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;
        var margins = new NativeMethods.DwmMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        GlassResult = NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);
    }

    public string DrawnAs => GlassResult == 0 ? "glass" : $"glass failed 0x{GlassResult:X8}";

    public bool GlassFailed => GlassResult != 0;

    private int GlassResult { get; set; }

    public double Scale { get; private set; } = 1.0;

    public bool IsTyping => Keyboard.FocusedElement is TextBox;

    public void CloseQuietly()
    {
        _closing = true;
        if (_fading)
            return;
        if (!IsLoaded)
        {
            Close();
            return;
        }
        _fading = true;
        AnimateClose();
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
        MakeGlass(handle);
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, (int)Math.Round(_bounds.X), (int)Math.Round(_bounds.Y),
            (int)Math.Round(_bounds.Width), (int)Math.Round(_bounds.Height), 0);
        CaptureExclusion.Apply(handle, _host.Settings.OverlayShownInCapture);
        Scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (Scale <= 0)
            Scale = 1.0;
        BuildContent();
        PrepareOpen();
        ContentRendered += (_, _) => AnimateOpen();
    }

    public double LayoutMs => _layout.Ms;

    public int LayoutCalls => _layout.Calls;

    protected override Size MeasureOverride(Size availableSize) => _layout.Time(() => base.MeasureOverride(availableSize));

    protected override Size ArrangeOverride(Size arrangeBounds) => _layout.Time(() => base.ArrangeOverride(arrangeBounds));

    private sealed class LayoutClock
    {
        private int _depth;

        public double Ms { get; private set; }

        public int Calls { get; private set; }

        public Size Time(Func<Size> layout)
        {
            if (_depth > 0)
                return layout();
            _depth++;
            var start = StopwatchClock.Shared.NowMs;
            try
            {
                return layout();
            }
            finally
            {
                _depth--;
                Ms += StopwatchClock.Shared.NowMs - start;
                Calls++;
            }
        }
    }

    private void BuildContent()
    {
        _root.Width = _bounds.Width / Scale;
        _root.Height = _bounds.Height / Scale;
        PlaceHosts();
        BuildList();
        ShowSearchButton();
        ShowContent();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None)
            return;
        e.Handled = true;
        RequestExit(returnToGame: true);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox)
            return;
        if (e.OriginalSource is not Visual source)
            return;
        if (PresentationSource.FromVisual(source) is { } other && other != PresentationSource.FromVisual(this))
            return;
        if (IsWithin<TextBox>(source))
            return;
        Focus();
    }

    private static bool IsWithin<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var node = element; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T)
                return true;
        }
        return false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
            return;
        e.Cancel = true;
        Dispatcher.InvokeAsync(() => RequestExit(returnToGame: true));
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

    private double Look => PanelLook.Scale(_bounds.Height) / Scale;

    private Color AccentColour => GlassParts.AccentColour(_host.Settings.UiAccentColor);

    private Brush AccentBrush => GlassParts.AccentAt(_host.Settings.UiAccentColor, 1);

    public TextBlock Text(string text, double sizePx, Brush? brush = null, bool regular = false) => new CutTextBlock
    {
        Text = text,
        FontFamily = (regular ? OverlayFonts.Regular : OverlayFonts.Bold).FontFamily,
        FontWeight = regular ? FontWeights.Normal : FontWeights.Bold,
        FontSize = Math.Max(1, sizePx / Scale),
        Foreground = brush ?? GlassParts.TextBrush,
    };


    private void BuildList()
    {
        var font = FontPx;
        var paddingY = font * 0.45 / Scale;
        var gap = font * 0.8 / Scale;
        var corner = font * 2.0 / 3.0 / Scale;
        var look = Look;
        var (left, top, width, bottom) = PanelPx();

        var content = new DockPanel { LastChildFill = true };

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text("INTERACTIVE OVERLAY", font));
        titles.Children.Add(Text("Toggle: " + KeyText(_host.Settings.HotkeyInteractiveOverlay), font * HeaderSubtitleScale, GlassParts.SecondaryBrush, regular: true));
        var pillHeight = font * PillLines / Scale;
        var close = GlassParts.CloseButton(pillHeight * 0.72, "Close and save", () => RequestExit(returnToGame: true));
        var header = GlassParts.Pill(titles, close, pillHeight, look, AccentColour);
        header.Margin = new Thickness(0, 0, 0, gap);
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        _listHeader = header;

        _message = Text(string.Empty, font * 0.85, GlassParts.WarningBrush);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Visibility = Visibility.Collapsed;
        _message.Margin = new Thickness(0, 0, 0, gap);
        DockPanel.SetDock(_message, Dock.Top);
        content.Children.Add(_message);

        _searchHost = new Border { Background = GlassParts.SearchPillBrush, CornerRadius = new CornerRadius(font * 1.1 / Scale), Margin = new Thickness(0, 0, 0, gap) };
        DockPanel.SetDock(_searchHost, Dock.Top);
        content.Children.Add(_searchHost);

        var rows = new StackPanel();
        _listRows = rows;
        var scroll = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        content.Children.Add(scroll);

        var pad = PanelLook.FramePadPx * PanelLook.Scale(_bounds.Height);
        var textPx = width - 2 * pad - font * 2.0 / 3.0 - 2 * font * 0.7;
        var charsPerLine = PanelLayout.CharsPerLine(textPx, font);
        var shown = _roster;

        if (shown.Count == 0)
        {
            var empty = MakeRowShell(paddingY, gap, corner);
            empty.Background = GlassParts.BoxBrush;
            empty.Cursor = null;
            empty.Child = Text("No recent players encountered.", font, GlassParts.SecondaryBrush, regular: true);
            rows.Children.Add(empty);
        }

        bool dividerShown = false;
        foreach (var entry in shown)
        {
            if (!entry.Recent && !dividerShown && rows.Children.Count > 0)
            {
                rows.Children.Add(MakeDivider(font, gap));
                dividerShown = true;
            }
            rows.Children.Add(MakePlayerRow(entry, charsPerLine, paddingY, gap, corner));
        }

        var frame = GlassParts.Framed(content, look, AccentColour);
        frame.Width = width / Scale;
        frame.MaxHeight = Math.Max(0, bottom - top) / Scale;
        frame.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(frame, ListHostLeft(left / Scale));
        Canvas.SetTop(frame, ListHostTop(top / Scale));
        _listHost.Children.Add(frame);
        _listFrame = frame;
    }

    private UIElement MakeDivider(double font, double gap)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, gap) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Rectangle Rule() => new() { Height = Math.Max(1, 1 / Scale), Fill = GlassParts.SecondaryBrush, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        var label = Text("EARLIER THIS SESSION", font * 0.8, GlassParts.SecondaryBrush, regular: true);
        label.Margin = new Thickness(font * 0.6 / Scale, 0, font * 0.6 / Scale, 0);
        var lineLeft = Rule();
        var lineRight = Rule();
        Grid.SetColumn(label, 1);
        Grid.SetColumn(lineRight, 2);
        grid.Children.Add(lineLeft);
        grid.Children.Add(label);
        grid.Children.Add(lineRight);
        return grid;
    }

    private Border MakePlayerRow(RosterEntry entry, int charsPerLine, double paddingY, double gap, double corner)
    {
        var row = MakeRowShell(paddingY, gap, corner);
        _rowCharsPerLine = charsPerLine;
        FillRow(row, entry);

        row.MouseEnter += (_, _) => PaintRow(row, entry, hover: true);
        row.MouseLeave += (_, _) => PaintRow(row, entry, hover: false);
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
            ShowMenu(menu);
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
            lines.Children.Add(Text(line, font, GlassParts.OrgTextBrush, regular: true));
        AddExtraRowLines(entry, lines, font, _rowCharsPerLine);
        row.Child = lines;
        PaintRow(row, entry, hover: row.IsMouseOver);
    }

    partial void AddExtraRowLines(RosterEntry entry, StackPanel lines, double font, int charsPerLine);

    private void PaintRow(Border row, RosterEntry entry, bool hover)
    {
        var selected = _view is not null && string.Equals(_view.Handle, entry.Handle, StringComparison.OrdinalIgnoreCase);
        var settings = _host.Settings;
        var fill = selected ? GlassParts.AccentAt(settings.UiAccentColor, PanelLook.SelectedFillOpacity) : hover ? GlassParts.BoxHoverBrush : GlassParts.BoxBrush;
        row.Background = GlassParts.EdgeFill(_host.IdentityOf(entry).Color, row.CornerRadius.TopLeft, fill);
        row.BorderBrush = selected ? GlassParts.AccentAt(settings.UiAccentColor, PanelLook.SelectedRingOpacity) : Brushes.Transparent;
    }

    private void RepaintRows()
    {
        foreach (var (row, entry) in _rows.Values)
            PaintRow(row, entry, row.IsMouseOver);
    }

    private Border MakeRowShell(double paddingY, double gap, double corner)
    {
        var ring = Math.Max(1, PanelLook.SelectedRingPx * Look);
        var paddingX = FontPx * 0.7 / Scale;
        return new Border
        {
            CornerRadius = new CornerRadius(corner),
            BorderThickness = new Thickness(ring),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(Math.Max(0, corner + paddingX - ring), Math.Max(0, paddingY - ring), Math.Max(0, paddingX - ring), Math.Max(0, paddingY - ring)),
            Margin = new Thickness(0, 0, 0, gap),
            Cursor = Cursors.Hand,
        };
    }

    private static string KeyText(string text) => text.Length == 0 ? "(none)" : text;
}
