
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
using Iris.Core.Lookup;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Views;

public sealed partial class InteractiveWindow
{
    private const string NotesFullText = "IRIS: Dossier notes file is full (20 MB).";

    private readonly DispatcherTimer _notesTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Grid? _dossier;
    private DossierView? _view;
    private TextBox? _notesBox;
    private bool _notesDirty;
    private Border? _searchHost;
    private Popup? _searchPopup;

    partial void AddExtraRowLines(RosterEntry entry, StackPanel lines, double font, int charsPerLine)
    {
        if (_host.NoteFor(entry.Handle) is not { Length: > 0 } note)
            return;
        var line = PanelText.NotesLine(note, charsPerLine);
        var row = new DockPanel { LastChildFill = true };
        var label = Text(PanelText.NotesPrefix.TrimEnd().TrimEnd(':').ToUpperInvariant() + " ", font * 0.8, AccentBrush);
        label.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        row.Children.Add(Text(line.Substring(PanelText.NotesPrefix.Length), font, GlassParts.SecondaryBrush, regular: true));
        lines.Children.Add(row);
    }

    private const double DossierTitleScale = 2.0;

    private const double IdentityWidthPx = 360;

    private DossierPlacement DossierPx() => DossierText.PlaceFor(_host.Settings, _bounds.Width, _bounds.Height);

    public void ShowDossier(DossierView view)
    {
        var place = DossierPx();
        if (place.IsEmpty)
            return;
        bool samePlayer = _view is not null && string.Equals(_view.Handle, view.Handle, StringComparison.OrdinalIgnoreCase);
        if (!samePlayer)
            FlushNotes();
        var keptText = samePlayer && _notesBox is not null ? _notesBox.Text : view.NoteText;
        var keptCaret = samePlayer && _notesBox is not null ? _notesBox.CaretIndex : keptText.Length;
        var hadFocus = samePlayer && _notesBox is { IsKeyboardFocused: true };
        var wasDirty = samePlayer && _notesDirty;

        if (_dossier is not null)
            _dossierHost.Children.Remove(_dossier);
        _view = view;

        var font = FontPx;
        var gap = font * 0.8 / Scale;
        var look = Look;
        var dossier = new DockPanel { LastChildFill = true };

        var pillHeight = font * PillLines / Scale;
        var title = Text("DOSSIER", font * DossierTitleScale);
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.LineHeight = HeaderTextHeight(font);
        title.VerticalAlignment = VerticalAlignment.Center;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(title);
        var close = GlassParts.CloseButton(pillHeight * 0.72, "Close the dossier", () => _host.CloseDossier());
        var header = GlassParts.Pill(titles, close, pillHeight, look, AccentColour);
        header.Margin = new Thickness(0, 0, 0, gap);
        DockPanel.SetDock(header, Dock.Top);
        dossier.Children.Add(header);

        var pad = PanelLook.FramePadPx * PanelLook.Scale(_bounds.Height);
        var identityPx = Math.Min(IdentityWidthPx * PanelLook.Scale(_bounds.Height), (place.Width - 2 * pad) * 0.4);
        var body = new Grid { Margin = new Thickness(0, 0, 0, gap) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(identityPx / Scale) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var identity = MakeNameBox(view, font);
        body.Children.Add(identity);

        var boxPad = font * 0.9;
        var cards = new UniformGrid { Columns = 2 };
        var columnPx = (place.Width - 2 * pad - identityPx - gap * Scale - 2 * boxPad) / 2 - font * 0.4;
        foreach (var box in DossierText.OrgBoxes(view.Orgs))
            cards.Children.Add(MakeOrgBox(box, view, font, columnPx));
        var orgsContent = new StackPanel();
        var orgsTitle = GlassParts.SubHeading("Organizations", Text(string.Empty, font * 0.85), look, AccentBrush);
        orgsTitle.Margin = new Thickness(0, 0, 0, font * 0.5 / Scale);
        orgsContent.Children.Add(orgsTitle);
        orgsContent.Children.Add(cards);
        var orgs = new Border
        {
            Background = GlassParts.BoxBrush,
            CornerRadius = new CornerRadius(font * 0.8 / Scale),
            Padding = new Thickness(boxPad / Scale),
            Child = orgsContent,
        };
        Grid.SetColumn(orgs, 2);
        body.Children.Add(orgs);
        DockPanel.SetDock(body, Dock.Top);
        dossier.Children.Add(body);

        dossier.Children.Add(MakeNotesBox(keptText, font));
        var framed = GlassParts.Framed(dossier, look, AccentColour);
        framed.Width = place.Width / Scale;
        framed.Height = (place.Bottom - place.Top) / Scale;
        Canvas.SetLeft(framed, DossierHostLeft(place.Left / Scale));
        Canvas.SetTop(framed, DossierHostTop(place.Top / Scale));
        _dossierHost.Children.Add(framed);
        _dossier = framed;
        AnimateDossier(framed, samePlayer);
        RepaintRows();
        _notesDirty = wasDirty;
        if (_notesBox is not null)
        {
            _notesBox.CaretIndex = Math.Min(keptCaret, _notesBox.Text.Length);
            if (hadFocus)
                _notesBox.Focus();
        }

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
            _dossierHost.Children.Remove(_dossier);
        _dossier = null;
        _view = null;
        _notesBox = null;
        _notesDirty = false;
        RepaintRows();
    }

    public void FlushNotes()
    {
        if (!_notesDirty || _view is null || _notesBox is null)
            return;
        _notesDirty = false;
        if (!_host.SaveNote(_view.Handle, _view.CitizenRecord, _notesBox.Text))
            ShowMessage(NotesFullText);
        RefreshRow(_view.Handle);
    }

    private Border MakeNameBox(DossierView view, double font)
    {
        var format = _host.Settings.DateFormat;
        var waiting = view.Loading ? "loading..." : DossierText.NotKnown;
        var rows = new StackPanel();
        rows.Children.Add(GlassParts.SubHeading("Identity", Text(string.Empty, font * 0.85), Look, AccentBrush));
        void Add(string label, string value)
        {
            var cell = new StackPanel { Margin = new Thickness(0, font * 0.55 / Scale, 0, 0) };
            cell.Children.Add(Text(label, font * 0.85, GlassParts.SecondaryBrush, regular: true));
            cell.Children.Add(Text(value, font * 1.05));
            rows.Children.Add(cell);
        }

        Add("Handle", view.Handle);
        Add("Moniker", view.Moniker ?? waiting);
        Add("UEE Citizen #", view.CitizenRecord is null ? "loading..." : DossierText.CitizenNumber(view.CitizenRecord));
        Add("Classification", DossierText.Classification(view.Identity.Kind, view.Identity.Orgs.Match, _host.Config));
        Add("Enlist Date", view.EnlistedRead || !view.Loading ? DossierText.EnlistDate(view.Enlisted, format) : waiting);
        Add("Record Age", view.EnlistedRead || !view.Loading ? DossierText.RecordAge(view.Enlisted, DateOnly.FromDateTime(DateTime.Now)) : waiting);
        Add("Last Seen by IRIS", DossierText.LastSeen(view.LastSeen, DateTimeOffset.Now, format));

        var box = Clickable(rows, view.Identity.Color, font, () => ColourMenus.ForPlayer(_host, view.Handle, RefreshColours), edgeLines: 0.8);
        box.Padding = new Thickness(box.Padding.Left + font * 0.4 / Scale, font * 0.9 / Scale, font * 0.9 / Scale, font * 0.9 / Scale);
        box.VerticalAlignment = VerticalAlignment.Top;
        return box;
    }

    private Border MakeOrgBox(DossierOrgBox org, DossierView view, double font, double columnPx)
    {
        var config = _host.Config;
        OverlayColor? color = org.Sid is { } sid
            ? SocialListEdits.OrgRow(config, sid)?.Color
            : org.Target is { } target ? SocialListEdits.RuleRow(config, target)?.Color : null;
        var match = view.Identity.Orgs.Match;
        var decides = org.Sid is { } orgSid && match is { DecidingOrgSid: { } decidingSid }
                      && string.Equals(orgSid, decidingSid, StringComparison.OrdinalIgnoreCase)
                      && view.Identity.Kind is MatchKind.OrgList or MatchKind.OrgAttribute;

        var lines = new StackPanel();
        var title = Text(org.Title.ToUpperInvariant(), font * 0.8, AccentBrush);
        if (org.Sid is null && color is { } ruleColour)
            lines.Children.Add(Marked(title, ruleColour, font));
        else
            lines.Children.Add(title);
        if (org.Sid is not null)
        {
            var name = Text(org.FullName ?? org.Sid, font * 1.05);
            lines.Children.Add(color is { } orgColour ? Marked(name, orgColour, font) : name);
            lines.Children.Add(MakeThirdRow(org, view.Loading, font, columnPx));
        }

        Func<ContextMenu> menu = org.Sid is { } menuSid
            ? () => ColourMenus.ForOrg(_host, menuSid, RefreshColours)
            : () => ColourMenus.ForAttribute(_host, org.Target!, RefreshColours);
        var box = Clickable(lines, decides ? view.Identity.Color : null, font, menu);
        box.Margin = new Thickness(0, 0, font * 0.4 / Scale, font * 0.4 / Scale);
        return box;
    }

    private UIElement Marked(TextBlock text, OverlayColor colour, double font)
    {
        var row = new DockPanel { LastChildFill = true };
        var mark = GlassParts.HalfCircle(colour, font * 0.8 / Scale);
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(text);
        return row;
    }

    private UIElement MakeThirdRow(DossierOrgBox org, bool loading, double font, double columnPx)
    {
        var grid = new Grid { Margin = new Thickness(0, font * 0.15 / Scale, 0, 0) };
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var partPx = (columnPx - font * (1.0 + 2.0 / 3.0)) / 3 - font * 0.3;
        var chars = Math.Max(3, (int)Math.Floor(partPx / (font * 0.85 * PanelLayout.NoteCharWidth)));
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
                var shownChars = color is null ? chars : Math.Max(3, chars - 2);
                var text = Text(DossierText.Cut(value, shownChars), font * 0.85, GlassParts.SecondaryBrush, regular: true);
                UIElement shown = color is { } c ? Marked(text, c, font * 0.85) : text;
                var part = Clickable(shown, null, font * 0.6, () => ColourMenus.ForAttribute(_host, target, RefreshColours), Brushes.Transparent);
                part.Margin = new Thickness(0, 0, i < 2 ? font * 0.15 / Scale : 0, 0);
                cell = part;
            }
            else
            {
                cell = Text(loading ? "..." : string.Empty, font * 0.85, GlassParts.SecondaryBrush, regular: true);
            }
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Border MakeNotesBox(string text, double font)
    {
        var padding = font * 0.7 / Scale;
        var panel = new DockPanel { LastChildFill = true };
        var title = GlassParts.SubHeading("Notes", Text(string.Empty, font * 0.85), Look, AccentBrush);
        title.Margin = new Thickness(0, 0, 0, font * 0.4 / Scale);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var count = Text(string.Empty, font * 0.8, GlassParts.SecondaryBrush, regular: true);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.Margin = new Thickness(0, font * 0.3 / Scale, 0, 0);
        DockPanel.SetDock(count, Dock.Bottom);
        panel.Children.Add(count);

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxLength = DossierNotes.MaxNoteChars,
            Padding = new Thickness(font * 0.6 / Scale, font * 0.4 / Scale, font * 0.6 / Scale, font * 0.4 / Scale),
            Background = Brushes.Transparent,
            Foreground = GlassParts.TextBrush,
            CaretBrush = GlassParts.TextBrush,
            BorderThickness = new Thickness(0),
            FontFamily = OverlayFonts.Regular.FontFamily,
            FontWeight = FontWeights.Normal,
            FontSize = Math.Max(1, font / Scale),
        };
        void Count() => count.Text = $"{CountText(box.Text.Length)} / {CountText(DossierNotes.MaxNoteChars)}";
        Count();
        box.TextChanged += (_, _) =>
        {
            _notesDirty = true;
            Count();
        };
        box.LostKeyboardFocus += (_, _) => FlushNotes();
        panel.Children.Add(new Border { Background = GlassParts.PillBrush, CornerRadius = new CornerRadius(font * 0.6 / Scale), Child = box });
        _notesBox = box;

        if (!_notesTimer.IsEnabled)
        {
            _notesTimer.Tick += (_, _) => FlushNotes();
            _notesTimer.Start();
        }

        return new Border
        {
            Background = GlassParts.BoxBrush,
            CornerRadius = new CornerRadius(font * 0.8 / Scale),
            Padding = new Thickness(padding, padding, padding, padding),
            Child = panel,
        };
    }

    private Border Clickable(UIElement content, OverlayColor? edge, double font, Func<ContextMenu> menu, Brush? fill = null, double edgeLines = 2.0 / 3.0)
    {
        var corner = font * (edge is null ? 0.6 : edgeLines) / Scale;
        var edgeWidth = edge is null ? 0 : corner;
        var box = new Border
        {
            CornerRadius = new CornerRadius(corner),
            Padding = new Thickness(edgeWidth + font * 0.5 / Scale, font * 0.3 / Scale, font * 0.5 / Scale, font * 0.3 / Scale),
            Child = content,
        };
        var normal = fill ?? GlassParts.BoxBrush;
        void Paint(bool hover) => box.Background = GlassParts.EdgeFill(edge, edgeWidth, hover ? GlassParts.BoxHoverBrush : normal);
        Paint(false);
        box.MouseEnter += (_, _) => Paint(true);
        box.MouseLeave += (_, _) => Paint(false);
        box.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var context = menu();
            context.PlacementTarget = box;
            context.Placement = PlacementMode.MousePoint;
            ShowMenu(context);
        };
        return box;
    }


    private void ShowSearchButton()
    {
        if (_searchHost is null)
            return;
        var font = FontPx;
        var label = Text("SEARCH", font * 0.9, GlassParts.SecondaryBrush);
        label.Margin = new Thickness(font * 0.9 / Scale, font * 0.45 / Scale, font * 0.9 / Scale, font * 0.45 / Scale);
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
            Background = Brushes.Transparent,
            Foreground = GlassParts.TextBrush,
            CaretBrush = GlassParts.TextBrush,
            BorderThickness = new Thickness(0),
            FontFamily = OverlayFonts.Bold.FontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(1, font / Scale),
            Padding = new Thickness(font * 0.9 / Scale, font * 0.35 / Scale, font * 0.9 / Scale, font * 0.35 / Scale),
        };
        var list = new ListBox
        {
            MinWidth = _searchHost.ActualWidth,
            Background = GlassParts.PopupBrush,
            Foreground = GlassParts.TextBrush,
            FontFamily = OverlayFonts.Bold.FontFamily,
            FontSize = Math.Max(1, font / Scale),
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(Math.Max(1, PanelLook.SelectedRingPx * Look)),
            Padding = new Thickness(font * 0.4 / Scale),
        };
        var popup = new Popup
        {
            PlacementTarget = SearchTarget(box),
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = list,
        };
        _searchPopup = popup;

        box.TextChanged += (_, _) =>
        {
            FillSearch(list, box.Text.Trim());
            if (!popup.IsOpen)
                PlaceSearchAnchor(box, popup, list);
            popup.IsOpen = list.Items.Count > 0;
        };
        box.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is Key.Down or Key.Up)
            {
                MoveSearchSelection(list, args.Key == Key.Down ? 1 : -1);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter)
            {
                var typed = box.Text.Trim();
                if ((list.SelectedItem as ListBoxItem)?.Tag is SearchChoice picked)
                    Pick(picked);
                else if (RsiNames.IsValidHandle(typed))
                    Pick(new SearchChoice(typed, LookUp: true));
                else if (typed.Length > 0)
                    ShowMessage("IRIS: not a valid RSI handle.");
                args.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, args) =>
        {
            if (args.NewFocus is Visual next && (next == list || list.IsAncestorOf(next) || IsWithin<ContextMenu>(next)))
                return;
            if (box.Text.Trim().Length == 0)
                ShowSearchButton();
            else
                popup.IsOpen = false;
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            if (!popup.IsOpen)
                PlaceSearchAnchor(box, popup, list);
            popup.IsOpen = list.Items.Count > 0;
        };
        list.MouseLeftButtonUp += (_, args) =>
        {
            if (ItemsControl.ContainerFromElement(list, (DependencyObject)args.OriginalSource) is ListBoxItem { Tag: SearchChoice picked })
                Pick(picked);
        };

        _searchHost.Cursor = Cursors.IBeam;
        _searchHost.Child = box;
        box.Focus();
    }

    private sealed record SearchChoice(string Handle, bool LookUp);

    private void FillSearch(ListBox list, string text)
    {
        list.Items.Clear();
        if (text.Length == 0)
            return;
        var font = FontPx;
        var hits = _host.Search(text);
        if (hits.Count > 0)
        {
            list.Items.Add(SearchHeading("Local Database", font));
            foreach (var hit in hits)
                list.Items.Add(SearchItem(hit.Handle, hit.Tag, new SearchChoice(hit.Handle, LookUp: false), font));
        }
        if (RsiNames.IsValidHandle(text))
        {
            list.Items.Add(SearchHeading("RSI", font));
            list.Items.Add(SearchItem("Look up " + text, string.Empty, new SearchChoice(text, LookUp: true), font));
        }
    }

    private ListBoxItem SearchHeading(string label, double font) => new()
    {
        Content = GlassParts.SubHeading(label, Text(string.Empty, font * 0.75, GlassParts.SecondaryBrush), Look, AccentBrush),
        Focusable = false,
        IsHitTestVisible = false,
        Margin = new Thickness(0, font * 0.3 / Scale, 0, font * 0.15 / Scale),
    };

    private ListBoxItem SearchItem(string handle, string tag, SearchChoice choice, double font)
    {
        var row = new DockPanel { LastChildFill = true };
        if (tag.Length > 0)
        {
            var tagText = Text(tag, font * 0.8, GlassParts.SecondaryBrush, regular: true);
            tagText.Margin = new Thickness(font / Scale, 0, 0, 0);
            tagText.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tagText, Dock.Right);
            row.Children.Add(tagText);
        }
        row.Children.Add(Text(handle, font));
        return new ListBoxItem { Content = row, Tag = choice, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static void MoveSearchSelection(ListBox list, int step)
    {
        var index = list.SelectedIndex;
        for (var next = index + step; next >= 0 && next < list.Items.Count; next += step)
        {
            if ((list.Items[next] as ListBoxItem)?.Tag is SearchChoice)
            {
                list.SelectedIndex = next;
                list.ScrollIntoView(list.Items[next]);
                return;
            }
        }
    }

    private void Pick(SearchChoice choice)
    {
        ShowSearchButton();
        if (choice.LookUp)
            _host.LookUp(choice.Handle);
        else
            _host.OpenDossierFor(choice.Handle);
    }

    private void StopNotesTimer() => _notesTimer.Stop();

    private static readonly System.Globalization.NumberFormatInfo CountFormat = new() { NumberGroupSeparator = " ", NumberGroupSizes = new[] { 3 } };

    private static string CountText(int value) => value.ToString("N0", CountFormat);
}
