
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Services;
using Iris.Core.Config;
using Microsoft.Win32;

namespace Iris.App.Views;

public partial class ConfigWindow
{
    private void OnAddPlayer(object sender, RoutedEventArgs e)
    {
        var row = new PlayerRowViewModel(string.Empty, OverlayColor.Cyan, IsExcluded);
        _players.Add(row);
        FocusNewRow(PlayersList, row);
    }

    private void OnAddOrg(object sender, RoutedEventArgs e)
    {
        var row = new OrgRowViewModel(string.Empty, Choices.DefaultPriority, OverlayColor.Red);
        _orgs.Add(row);
        FocusNewRow(OrgsList, row);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var row = new RuleRowViewModel(AttributeKind.Archetype, null, Choices.DefaultPriority, OverlayColor.Orange);
        var noMatchIndex = _rules.Count;
        while (noMatchIndex > 0 && _rules[noMatchIndex - 1].IsNoMatch)
            noMatchIndex--;
        _rules.Insert(noMatchIndex, row);
        FocusNewRow<ComboBox>(RulesList, row);
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case PlayerRowViewModel player:
                _players.Remove(player);
                RecheckDuplicates(_players);
                break;
            case OrgRowViewModel org:
                _orgs.Remove(org);
                RecheckDuplicates(_orgs);
                break;
            case RuleRowViewModel rule when !rule.IsNoMatch:
                _rules.Remove(rule);
                break;
        }
    }

    private void RecheckDuplicates(IEnumerable<ListRowViewModel> rows, ListRowViewModel? except = null)
    {
        foreach (var row in rows.Where(r => r.IsFlaggedDuplicate && !ReferenceEquals(r, except)).ToList())
        {
            row.ResetStatus();
            StartCheck(row);
        }
    }

    private void FocusNewRow(ItemsControl list, object item) => FocusNewRow<Control>(list, item);

    private void FocusNewRow<T>(ItemsControl list, object item) where T : Control
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                return;
            container.BringIntoView();
            FindFirst<T>(container)?.Focus();
        }));
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var deeper = FindFirst<T>(child);
            if (deeper is not null)
                return deeper;
        }
        return null;
    }

    private void OnValueLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ListRowViewModel row)
            return;

        StartCheck(row);

        RecheckDuplicates(row is OrgRowViewModel ? _orgs.Cast<ListRowViewModel>() : _players.Cast<ListRowViewModel>(), except: row);
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        EndEditing();
    }

    private void EndEditing()
    {
        FocusManager.SetFocusedElement(this, FocusSink);
        Keyboard.Focus(FocusSink);
    }

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_isClosed && Keyboard.FocusedElement is HotkeyBox)
                EndEditing();
        }));
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox)
            return;
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;
        EndEditing();
    }

    private static bool IsInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase or ComboBox or ComboBoxItem or ButtonBase or ScrollBar or Thumb)
                return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnRuleKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox kindBox || !kindBox.IsKeyboardFocusWithin || e.AddedItems.Count == 0)
            return;
        if (kindBox.DataContext is not RuleRowViewModel)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (kindBox.DataContext is not RuleRowViewModel { NeedsValue: true })
                return;
            var rowGrid = FindAncestor<Grid>(kindBox);
            if (rowGrid is null)
                return;
            var cell = rowGrid.Children.OfType<ContentControl>().FirstOrDefault(c => Equals(c.Tag, RuleValueCellTag));
            if (cell is null)
                return;
            cell.UpdateLayout();
            var valueBox = FindFirst<ComboBox>(cell);
            if (valueBox is not null)
            {
                valueBox.Focus();
                valueBox.IsDropDownOpen = true;
            }
        }));
    }

    private void StartCheck(ListRowViewModel row)
    {
        var task = CheckRowAsync(row);
        if (task.IsCompleted)
            return;
        _checks[row] = task;
        _ = ForgetWhenDoneAsync(row, task);
    }

    private async Task ForgetWhenDoneAsync(ListRowViewModel row, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }

        if (_checks.TryGetValue(row, out var current) && ReferenceEquals(current, task))
            _checks.Remove(row);
    }

    private async Task CheckRowAsync(ListRowViewModel row)
    {
        var text = row.Value.Trim();
        if (text.Length == 0)
            return;
        if (row.StatusKind != RowStatusKind.Unchecked && row.StatusKind != RowStatusKind.Failed)
            return;

        var isOrg = row is OrgRowViewModel;
        var others = (isOrg ? _orgs.Cast<ListRowViewModel>() : _players.Cast<ListRowViewModel>())
            .Where(r => !ReferenceEquals(r, row))
            .Select(r => r.Value)
            .ToList();

        var version = row.Version;
        var token = _closing.Token;
        row.BeginCheck();

        RowCheckResult result;
        try
        {
            result = isOrg
                ? await Task.Run(() => _services.Validator.ValidateOrgAsync(text, others, token))
                : await Task.Run(() => _services.Validator.ValidatePlayerAsync(text, others, token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Config check crashed: " + ex.Message);
            result = new RowCheckResult(RowCheckState.Failed, "Check failed: " + ex.Message);
        }

        if (row.Version != version || !IsInList(row))
            return;
        row.ApplyResult(result);
    }

    private bool IsInList(ListRowViewModel row) => row switch
    {
        PlayerRowViewModel player => _players.Contains(player),
        OrgRowViewModel org => _orgs.Contains(org),
        _ => false,
    };
}
