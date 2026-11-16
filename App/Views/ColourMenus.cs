using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Iris.Core.Config;
using Iris.Core.Lookup;
using Iris.Core.Overlay;

namespace Iris.App.Views;

public static class ColourMenus
{
    public static ContextMenu ForPlayer(IInteractiveHost host, string handle, Action changed)
    {
        var menu = NewMenu();
        menu.Items.Add(Caption("Assign Color"));
        foreach (var color in Enum.GetValues<OverlayColor>())
            menu.Items.Add(ColourItem(color, () => Apply(host, c => SocialListEdits.SetPlayerColor(c, handle, color), changed)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove Color", SocialListEdits.PlayerRow(host.Config, handle) is not null,
            () => Apply(host, c => SocialListEdits.RemovePlayer(c, handle), changed)));
        AddRsiPage(menu, host, RsiNames.IsValidHandle(handle) ? RsiUrls.PlayerProfile(handle) : null);
        return menu;
    }

    public static ContextMenu ForOrg(IInteractiveHost host, string sid, Action changed)
    {
        var row = SocialListEdits.OrgRow(host.Config, sid);
        var menu = Build(
            "Org",
            row?.Color,
            ColorRanks.OrgRanking(host.Config),
            setColor: color => Apply(host, c => SocialListEdits.SetOrgColor(c, sid, color), changed),
            setRank: rank => Apply(host, c => SocialListEdits.SetOrgColorRank(c, sid, rank), changed),
            remove: () => Apply(host, c => SocialListEdits.RemoveOrg(c, sid), changed));
        AddRsiPage(menu, host, RsiNames.IsValidSid(sid) ? RsiUrls.OrgPage(sid) : null);
        return menu;
    }

    private static void AddRsiPage(ContextMenu menu, IInteractiveHost host, string? url)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open RSI Webpage", url is not null, () => host.OpenRsiPage(url!)));
    }

    public static ContextMenu ForAttribute(IInteractiveHost host, AttributeTarget target, Action changed)
    {
        var row = SocialListEdits.RuleRow(host.Config, target);
        bool permanent = target.Kind == AttributeKind.NoMatch;
        return Build(
            "Attribute",
            row?.Color,
            permanent ? null : ColorRanks.RuleRanking(host.Config),
            setColor: color => Apply(host, c => SocialListEdits.SetRuleColor(c, target, color), changed),
            setRank: rank => Apply(host, c => SocialListEdits.SetRuleColorRank(c, target, rank), changed),
            remove: permanent ? null : () => Apply(host, c => SocialListEdits.RemoveRule(c, target), changed));
    }

    private static ContextMenu Build(string tab, OverlayColor? rowColor, IReadOnlyList<OverlayColor>? ranking, Action<OverlayColor> setColor, Action<int> setRank, Action? remove)
    {
        var menu = NewMenu();
        if (rowColor is not { } current)
        {
            menu.Items.Add(Caption(tab + " Color"));
            foreach (var color in Enum.GetValues<OverlayColor>())
                menu.Items.Add(ColourItem(color, () => setColor(color)));
        }
        else
        {
            var colours = new MenuItem { Header = tab + " Color" };
            foreach (var color in Enum.GetValues<OverlayColor>())
                colours.Items.Add(ColourItem(color, () => setColor(color)));
            menu.Items.Add(colours);
            if (ranking is { Count: > 0 })
            {
                var ranks = new MenuItem { Header = tab + " Color Rank" };
                for (int i = 0; i < ranking.Count; i++)
                {
                    int rank = i + 1;
                    var item = ColourItem(ranking[i], () => setRank(rank));
                    item.Header = rank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "  " + ranking[i];
                    item.IsChecked = ranking[i] == current;
                    ranks.Items.Add(item);
                }
                menu.Items.Add(ranks);
            }
        }

        if (remove is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove Color", rowColor is not null, remove));
        }
        return menu;
    }

    private static readonly Uri LookUri = new("/IRIS;component/Views/IoMenus.xaml", UriKind.Relative);
    private static ResourceDictionary? s_look;
    private static bool s_lookTried;
    private static string? s_lookProblem;

    public static string? LoadLook()
    {
        Look();
        var problem = s_lookProblem;
        s_lookProblem = null;
        return problem;
    }

    private static ContextMenu NewMenu()
    {
        var menu = new ContextMenu();
        if (Look() is { } look && look["IoMenu.Menu"] is Style style)
        {
            menu.Resources.MergedDictionaries.Add(look);
            menu.Style = style;
        }
        return menu;
    }

    private static ResourceDictionary? Look()
    {
        if (s_lookTried)
            return s_look;
        s_lookTried = true;
        try
        {
            var look = new ResourceDictionary { Source = LookUri };
            look["IoMenu.Back"] = GlassParts.MenuBrush;
            look["IoMenu.Border"] = GlassParts.MenuBorderBrush;
            look["IoMenu.Highlight"] = GlassParts.MenuHighlightBrush;
            look["IoMenu.Text"] = GlassParts.TextBrush;
            look["IoMenu.Disabled"] = GlassParts.SecondaryBrush;
            s_look = look;
        }
        catch (Exception ex)
        {
            s_look = null;
            s_lookProblem = ex.Message;
        }
        return s_look;
    }

    private static void Apply(IInteractiveHost host, Func<SocialConfig, SocialConfig> change, Action changed)
    {
        if (host.Edit(change))
            changed();
    }

    private static MenuItem Caption(string text) => new() { Header = text, IsEnabled = false };

    private static MenuItem Item(string text, bool enabled, Action click)
    {
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    private static MenuItem ColourItem(OverlayColor color, Action click)
    {
        var (r, g, b) = OverlayPalette.Rgb(color);
        var item = Item(color.ToString(), true, click);
        item.Icon = new Rectangle { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(r, g, b)) };
        return item;
    }
}
