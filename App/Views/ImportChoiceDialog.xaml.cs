using System.Windows;
using Iris.Core.Config;

namespace Iris.App.Views;

public partial class ImportChoiceDialog : Window
{
    private ImportMode? _choice;
    private ImportMode _mode;

    private ImportChoiceDialog()
    {
        InitializeComponent();
        AppTheme.Attach(this);
    }

    public static ImportMode? Ask(Window? owner, SocialConfig imported, bool hasExistingFile, string headline, ImportMode mode, bool userJustActed = false)
    {
        var dialog = new ImportChoiceDialog { _mode = mode };
        dialog.ModeText.Text = ModeTitle(mode);
        dialog.ModeHint.Text = ModeHintText(mode);
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
            DialogHelper.PrepareBackgroundDialog(dialog, null, userJustActed);
        }

        DialogHelper.ShowModal(dialog);
        return dialog._choice;
    }

    private static string Describe(SocialConfig config)
    {
        var text = $"{Plural(config.Players.Count, "player", "players")}, "
            + $"{Plural(config.Orgs.Count, "org", "orgs")}, "
            + $"{Plural(config.UserRuleCount, "org-attribute rule", "org-attribute rules")}";
        return config.NewerVersionNote() is { } note ? text + "\n" + note : text;
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private static string ModeTitle(ImportMode mode) => mode switch
    {
        ImportMode.Merge => "Merge, Prioritize Local Config",
        ImportMode.MergeImportedWins => "Merge, Prioritize Imported Config",
        _ => "Replace, Backup Local Config",
    };

    private static string ModeHintText(ImportMode mode) => mode switch
    {
        ImportMode.Merge => "Adds only new entries; where both have one, yours is kept.",
        ImportMode.MergeImportedWins => "Adds new entries; where both have one, the imported one is used.",
        _ => "Keeps a dated copy of your lists, then uses the imported ones.",
    };

    private void OnImport(object sender, RoutedEventArgs e) => Choose(_mode == ImportMode.Replace ? ImportMode.BackupExisting : _mode);

    private void Choose(ImportMode mode)
    {
        _choice = mode;
        DialogResult = true;
    }
}
