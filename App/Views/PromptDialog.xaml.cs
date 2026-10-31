using System;
using System.Windows;

namespace Iris.App.Views;

public partial class PromptDialog : Window
{
    private static int _openCount;
    private static PromptDialog? _notice;
    private bool _yes;
    private bool _modeless;

    private PromptDialog(string message, bool yesNo, bool defaultYes, string okText = "OK")
    {
        InitializeComponent();
        MessageText.Text = message;
        if (yesNo)
        {
            YesButton.IsDefault = defaultYes;
            NoButton.IsDefault = !defaultYes;
        }
        else
        {
            YesButton.Content = okText;
            YesButton.IsCancel = true;
            NoButton.Visibility = Visibility.Collapsed;
            NoButton.IsCancel = false;
        }
    }

    public static bool IsAnyOpen => _openCount > 0;

    private static void ShowCounted(PromptDialog dialog)
    {
        _openCount++;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            _openCount--;
        }
    }

    public static bool Ask(
        string message, string balloonText, Action<string>? notify, Window? owner, bool defaultYes = true, bool userJustActed = false)
    {
        var dialog = new PromptDialog(message, yesNo: true, defaultYes);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify, userJustActed);
        ShowCounted(dialog);
        return dialog._yes;
    }

    public static void Tell(string message, string balloonText, Action<string>? notify, Window? owner, bool userJustActed = false, string okText = "OK")
    {
        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true, okText);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify, userJustActed);
        ShowCounted(dialog);
    }

    public static void ShowNotice(string message)
    {
        if (_notice is not null)
            return;

        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true, okText: "Close") { _modeless = true };
        DialogHelper.PrepareBackgroundDialog(dialog, owner: null, balloonText: string.Empty, notify: null);
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_notice, dialog))
                _notice = null;
        };
        _notice = dialog;
        dialog.Show();
    }

    public static void CloseNotice()
    {
        var dialog = _notice;
        _notice = null;
        if (dialog is null)
            return;
        try
        {
            dialog.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        if (_modeless)
        {
            Close();
            return;
        }

        _yes = true;
        DialogResult = true;
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _yes = false;
        DialogResult = false;
    }
}
