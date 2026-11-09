
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Iris.Core.Config;

namespace Iris.App.Views;

public partial class ConfigWindow
{
    private void SyncRankings()
    {
        _orgRanking = ColorRanks.Sync(_orgRanking, _orgs.Select(o => o.Color));
        _ruleRanking = ColorRanks.Sync(_ruleRanking, _rules.Where(r => !r.IsNoMatch).Select(r => r.Color));
        Fill(_orgRankItems, _orgRanking, orgs: true);
        Fill(_ruleRankItems, _ruleRanking, orgs: false);
    }

    private static void Fill(ObservableCollection<RankStripItem> items, IReadOnlyList<OverlayColor> ranking, bool orgs)
    {
        if (items.Count == ranking.Count && items.Select(i => i.Color).SequenceEqual(ranking))
            return;
        items.Clear();
        for (var i = 0; i < ranking.Count; i++)
            items.Add(new RankStripItem(orgs, ranking[i], i + 1, ranking.Count));
    }

    private void OnRankUp(object sender, RoutedEventArgs e) => MoveRank(sender, -1);

    private void OnRankDown(object sender, RoutedEventArgs e) => MoveRank(sender, +1);

    private void MoveRank(object sender, int by)
    {
        if ((sender as FrameworkElement)?.DataContext is not RankStripItem item)
            return;
        if (item.Orgs)
            _orgRanking = ColorRanks.Move(_orgRanking, item.Color, item.Rank + by);
        else
            _ruleRanking = ColorRanks.Move(_ruleRanking, item.Color, item.Rank + by);
        SyncRankings();
        UpdateSaveButton();
    }
}
