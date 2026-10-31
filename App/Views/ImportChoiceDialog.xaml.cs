using System;
using System.Windows;
using Iris.Core.Config;

namespace Iris.App.Views;

public partial class ImportChoiceDialog : Window
{
    private ImportMode? _choice;

    private ImportChoiceDialog()
    {
        InitializeComponent();
    }

    public static ImportMode? Ask(
        Window? owner, SocialConfig imported, bool hasExistingFile, string headline, Action<string>? notify = null,
        bool userJustActed = false)
    {
        var dialog = new ImportChoiceDialog();
        dialog.HeadlineText.Text = headline;
        dialog.SummaryText.Text = Describe(imported);
        dialog.ChoicesPanel.Visibility = hasExistingFile ? Visibility.Visible : Visibility.Collapsed;
        dialog.NoExistingPanel.Visibility = hasExistingFile ? Visibility.Collapsed : Visibility.Visible;

        if (owner is not null && DialogHelper.PrepareOwner(owner))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            DialogHelper.PrepareBackgroundDialog(dialog, null, headline, notify, userJustActed);
        }

        dialog.ShowDialog();
        return dialog._choice;
    }

    private static string Describe(SocialConfig config)
    {
        var name = string.IsNullOrWhiteSpace(config.Name) ? "(no name)" : config.Name;
        return $"List name: {name}\n"
            + $"{Plural(config.Players.Count, "player", "players")}, "
            + $"{Plural(config.Orgs.Count, "org", "orgs")}, "
            + $"{Plural(config.UserRuleCount, "org-attribute rule", "org-attribute rules")}";
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private void OnBackup(object sender, RoutedEventArgs e) => Choose(ImportMode.BackupExisting);

    private void OnMerge(object sender, RoutedEventArgs e) => Choose(ImportMode.Merge);

    private void OnReplace(object sender, RoutedEventArgs e) => Choose(ImportMode.Replace);

    private void Choose(ImportMode mode)
    {
        _choice = mode;
        DialogResult = true;
    }
}
