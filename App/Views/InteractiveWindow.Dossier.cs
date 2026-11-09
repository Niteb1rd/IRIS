
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Views;

public sealed partial class InteractiveWindow
{
    private const string NotesFullText = "IRIS: Dossier notes file is full (20 MB).";

    private const double NamePaddingLines = 1.5;

    private const double NameGapFactor = 3;

    private const double NotesGapFactor = 3;

    private static readonly Brush CellTint = MakeFrozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CellTintHover = MakeFrozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

    private static readonly Brush SearchBrush = MakeFrozen(Color.FromArgb(0xCC, 0x60, 0x60, 0x68));

    private readonly DispatcherTimer _notesTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DockPanel? _dossier;
    private DossierView? _view;
    private TextBox? _notesBox;
    private bool _notesDirty;
    private bool _notesUnsaved;
    private Border? _searchHost;
    private Popup? _searchPopup;

    public string? DossierHandle => _view?.Handle;

    partial void AddExtraRowLines(RosterEntry entry, StackPanel lines, double font, int charsPerLine)
    {
        if (_host.NoteFor(entry.Handle) is { Length: > 0 } note)
            lines.Children.Add(Text(PanelText.NotesLine(note, charsPerLine), font));
    }

    private DossierPlacement DossierPx() => DossierText.PlaceFor(_host.Settings, _bounds.Width, _bounds.Height);

    public void ShowDossier(DossierView view)
    {
        var place = DossierPx();
        if (place.IsEmpty)
            return;
        bool samePlayer = _view is not null && string.Equals(_view.Handle, view.Handle, StringComparison.OrdinalIgnoreCase);
        if (!samePlayer)
            FlushNotes();
        if (!samePlayer)
            _notesUnsaved = false;
        var keptText = samePlayer && _notesBox is not null && (_notesDirty || _notesUnsaved) ? _notesBox.Text : view.NoteText;
        var keptCaret = samePlayer && _notesBox is not null ? _notesBox.CaretIndex : keptText.Length;
        var hadFocus = samePlayer && _notesBox is { IsKeyboardFocused: true };
        var wasDirty = samePlayer && _notesDirty;

        if (_dossier is not null)
            _root.Children.Remove(_dossier);
        _view = view;

        var font = FontPx;
        var gap = font * 0.4 / Scale;
        var dossier = new DockPanel { Width = place.Width / Scale, Height = (place.Bottom - place.Top) / Scale, LastChildFill = true };
        Canvas.SetLeft(dossier, place.Left / Scale);
        Canvas.SetTop(dossier, place.Top / Scale);

        var nameRow = new Grid { Margin = new Thickness(0, 0, 0, gap * NameGapFactor) };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = MakeNameBox(view, font);
        Grid.SetColumn(name, 1);
        nameRow.Children.Add(name);
        var close = MakeCloseButton(font * CloseButtonLines * 0.5, () => _host.CloseDossier());
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(gap, 0, 0, 0);
        Grid.SetColumn(close, 2);
        nameRow.Children.Add(close);
        DockPanel.SetDock(nameRow, Dock.Top);
        dossier.Children.Add(nameRow);

        var orgs = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, gap * NotesGapFactor) };
        var columnPx = place.Width / 2 - font * 0.4;
        foreach (var box in DossierText.OrgBoxes(view.Orgs))
            orgs.Children.Add(MakeOrgBox(box, view.Loading, font, columnPx));
        DockPanel.SetDock(orgs, Dock.Top);
        dossier.Children.Add(orgs);

        dossier.Children.Add(MakeNotesBox(keptText, font));
        _root.Children.Add(dossier);
        _dossier = dossier;
        _notesDirty = wasDirty;
        if (_notesBox is not null)
        {
            _notesBox.CaretIndex = Math.Min(keptCaret, _notesBox.Text.Length);
            if (hadFocus)
                _notesBox.Focus();
        }

        if (_searchHost is not null)
            Panel.SetZIndex(_searchHost, 1);
    }

    private void RefreshDossierColours()
    {
        if (_view is not null)
            _host.RefreshDossier();
    }

    public void CloseDossierView()
    {
        FlushNotes();
        if (_dossier is not null)
            _root.Children.Remove(_dossier);
        _dossier = null;
        _view = null;
        _notesBox = null;
        _notesDirty = false;
    }

    public void FlushNotes()
    {
        if (!_notesDirty || _view is null || _notesBox is null)
            return;
        _notesDirty = false;
        var saved = _host.SaveNote(_view.Handle, _view.CitizenRecord, _notesBox.Text);
        _notesUnsaved = !saved || _host.NotesUnreadable;
        if (!saved)
            ShowMessage(NotesFullText);
        RefreshRow(_view.Handle);
    }

    private Border MakeNameBox(DossierView view, double font)
    {
        var lines = new StackPanel();
        lines.Children.Add(Text("Handle: " + view.Handle, font));
        lines.Children.Add(Text("Moniker: " + (view.Moniker ?? (view.Loading ? "loading..." : "?")), font));
        lines.Children.Add(Text("UEE Citizen #: " + (view.CitizenRecord ?? "loading..."), font));
        var match = view.Identity.Orgs.Match;
        lines.Children.Add(Text("Classification from: " + DossierText.ClassificationFrom(view.Identity.Kind, match, _host.Config), font));
        var box = Clickable(lines, view.Identity.Color, font, () => ColourMenus.ForPlayer(_host, view.Handle, RefreshColours));
        box.Padding = new Thickness(font * NamePaddingLines / Scale, font * 0.3 / Scale, font * NamePaddingLines / Scale, font * 0.3 / Scale);
        return box;
    }

    private Border MakeOrgBox(DossierOrgBox org, bool loading, double font, double columnPx)
    {
        var config = _host.Config;
        OverlayColor? color = org.Sid is { } sid
            ? SocialListEdits.OrgRow(config, sid)?.Color
            : org.Target is { } target ? SocialListEdits.RuleRow(config, target)?.Color : null;

        var lines = new StackPanel();
        lines.Children.Add(Text(org.Title, font));
        if (org.Sid is not null)
        {
            lines.Children.Add(Text(org.FullName ?? org.Sid, font));
            lines.Children.Add(MakeThirdRow(org, loading, font, columnPx));
        }

        Func<ContextMenu> menu = org.Sid is { } orgSid
            ? () => ColourMenus.ForOrg(_host, orgSid, RefreshColours)
            : () => ColourMenus.ForAttribute(_host, org.Target!, RefreshColours);
        var box = Clickable(lines, color, font, menu);
        box.Margin = new Thickness(0, 0, font * 0.2 / Scale, font * 0.2 / Scale);
        return box;
    }

    private UIElement MakeThirdRow(DossierOrgBox org, bool loading, double font, double columnPx)
    {
        var grid = new Grid { Margin = new Thickness(0, font * 0.15 / Scale, 0, 0) };
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var partPx = columnPx / 3 - font * 0.3;
        var chars = Math.Max(3, (int)Math.Floor(partPx / (font * PanelLayout.NoteCharWidth)));
        var parts = new (string? Value, AttributeKind Kind)[]
        {
            (org.Archetype, AttributeKind.Archetype), (org.Primary, AttributeKind.Activity), (org.Secondary, AttributeKind.Activity),
        };
        for (int i = 0; i < parts.Length; i++)
        {
            var (value, kind) = parts[i];
            UIElement cell;
            if (value is { Length: > 0 })
            {
                var target = new AttributeTarget(kind, value);
                var color = SocialListEdits.RuleRow(_host.Config, target)?.Color;
                var text = Text(DossierText.Cut(value, chars), font * 0.85);
                text.TextTrimming = TextTrimming.None;
                var part = Clickable(text, color, font * 0.6, () => ColourMenus.ForAttribute(_host, target, RefreshColours), CellTint, CellTintHover);
                part.Margin = new Thickness(0, 0, i < 2 ? font * 0.15 / Scale : 0, 0);
                cell = part;
            }
            else
            {
                cell = Text(loading ? "..." : string.Empty, font * 0.85);
            }
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Border MakeNotesBox(string text, double font)
    {
        var padding = font * 0.5 / Scale;
        var panel = new DockPanel { LastChildFill = true };
        var title = Text("NOTES:", font);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxLength = DossierNotes.MaxNoteChars,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            FontFamily = OverlayFonts.Bold.FontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(1, font / Scale),
        };
        box.TextChanged += (_, _) => _notesDirty = true;
        panel.Children.Add(box);
        _notesBox = box;

        if (!_notesTimer.IsEnabled)
        {
            _notesTimer.Tick += (_, _) => FlushNotes();
            _notesTimer.Start();
        }

        return new Border
        {
            Background = NoColourBrush,
            CornerRadius = new CornerRadius(font * 0.5 / Scale),
            Padding = new Thickness(padding, padding, padding, padding),
            Child = panel,
        };
    }

    private Border Clickable(UIElement content, OverlayColor? color, double font, Func<ContextMenu> menu, Brush? noColour = null, Brush? noColourHover = null)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(font * 0.5 / Scale),
            Padding = new Thickness(font * 0.5 / Scale, font * 0.3 / Scale, font * 0.5 / Scale, font * 0.3 / Scale),
            Child = content,
        };
        void Paint(bool hover) => box.Background = color is { } c
            ? ColourBrush(c, hover ? HoverAlpha : RowAlpha)
            : hover ? noColourHover ?? HoverBlack : noColour ?? NoColourBrush;
        Paint(false);
        box.MouseEnter += (_, _) => Paint(true);
        box.MouseLeave += (_, _) => Paint(false);
        box.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var context = menu();
            context.PlacementTarget = box;
            context.Placement = PlacementMode.MousePoint;
            context.IsOpen = true;
        };
        return box;
    }

    private static readonly Brush HoverBlack = MakeFrozen(Color.FromArgb(HoverAlpha, 0, 0, 0));


    private void BuildSearch()
    {
        var place = DossierPx();
        var (_, top, _, _) = PanelPx();
        var left = place.IsEmpty ? _bounds.Width * DossierText.LeftMarginShare : place.Left;
        var host = new Border { Background = SearchBrush, CornerRadius = new CornerRadius(FontPx * 0.4 / Scale) };
        Canvas.SetLeft(host, left / Scale);
        Canvas.SetTop(host, top / Scale);
        Panel.SetZIndex(host, 1);
        _searchHost = host;
        ShowSearchButton();
        _root.Children.Add(host);
    }

    private void ShowSearchButton()
    {
        if (_searchHost is null)
            return;
        var font = FontPx;
        var label = Text("Search", font);
        label.Margin = new Thickness(font * 0.6 / Scale, font * 0.3 / Scale, font * 0.6 / Scale, font * 0.3 / Scale);
        _searchHost.Child = label;
        _searchHost.Cursor = Cursors.Hand;
        _searchHost.MouseLeftButtonUp -= OnSearchClicked;
        _searchHost.MouseLeftButtonUp += OnSearchClicked;
        if (_searchPopup is not null)
            _searchPopup.IsOpen = false;
    }

    private void OnSearchClicked(object sender, MouseButtonEventArgs e)
    {
        if (_searchHost is null || _searchHost.Child is TextBox)
            return;
        e.Handled = true;
        var font = FontPx;
        var box = new TextBox
        {
            Width = font * 14 / Scale,
            Background = SearchBrush,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            FontFamily = OverlayFonts.Bold.FontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(1, font / Scale),
            Padding = new Thickness(font * 0.4 / Scale, font * 0.2 / Scale, font * 0.4 / Scale, font * 0.2 / Scale),
        };
        var list = new ListBox
        {
            MinWidth = box.Width,
            Background = MakeFrozen(Color.FromArgb(0xEE, 0x20, 0x20, 0x26)),
            Foreground = Brushes.White,
            FontFamily = OverlayFonts.Bold.FontFamily,
            FontSize = Math.Max(1, font / Scale),
            BorderThickness = new Thickness(0),
        };
        var popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = list,
        };
        _searchPopup = popup;

        box.TextChanged += (_, _) =>
        {
            var found = box.Text.Trim().Length == 0 ? Array.Empty<string>() : _host.Search(box.Text.Trim());
            list.ItemsSource = found;
            popup.IsOpen = found.Count > 0;
        };
        box.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                args.Handled = true;
            }
            else if (args.Key == Key.Up && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && list.Items.Count > 0)
            {
                Pick(list.SelectedItem as string ?? list.Items[0] as string);
                args.Handled = true;
            }
        };
        list.MouseLeftButtonUp += (_, _) => Pick(list.SelectedItem as string);

        _searchHost.Cursor = Cursors.IBeam;
        _searchHost.Child = box;
        box.Focus();
    }

    private void Pick(string? handle)
    {
        if (handle is null)
            return;
        ShowSearchButton();
        _host.OpenDossierFor(handle);
    }

    private void StopNotesTimer() => _notesTimer.Stop();

    private static Brush MakeFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
