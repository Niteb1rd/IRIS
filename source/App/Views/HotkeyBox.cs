using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Services;
using Iris.Core.Config;

namespace Iris.App.Views;

public sealed class HotkeyBox : TextBox
{
    private const string NoneText = "(none)";
    private const string CapturingText = "Press keys... (Esc cancels)";

    private string _value = string.Empty;
    private bool _capturing;
    private string? _problem;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ContextMenu = null;
        ShowText();
        Unloaded += (_, _) => EndCapture();
    }

    public event EventHandler? HotkeyChanged;

    public string HotkeyValue
    {
        get => _value;
        set
        {
            _value = HotkeyText.Normalize(value);
            _problem = null;
            ShowText();
        }
    }

    public void ClearHotkey()
    {
        if (_value.Length == 0)
            return;
        _value = string.Empty;
        _problem = null;
        ShowText();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Select(0, 0);
        ShowText();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        EndCapture();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        e.Handled = true;
        if (!IsKeyboardFocused)
            Focus();
        BeginCapture();
    }

    private void BeginCapture()
    {
        if (_capturing)
            return;
        _capturing = true;
        HotkeyManager.CaptureInProgress = true;
        _problem = null;
        ShowText();
    }

    private void EndCapture()
    {
        if (_capturing)
            HotkeyManager.CaptureInProgress = false;
        _capturing = false;
        _problem = null;
        ShowText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            if ((e.Key is Key.Enter or Key.Space) && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                    BeginCapture();
                return;
            }
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        if (e.IsRepeat)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
            case Key.LeftAlt:
            case Key.RightAlt:
            case Key.LeftShift:
            case Key.RightShift:
            case Key.LWin:
            case Key.RWin:
            case Key.None:
            case Key.DeadCharProcessed:
            case Key.ImeProcessed:
                return;

            case Key.Escape:
                EndCapture();
                return;
        }

        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Windows))
        {
            _problem = HotkeyText.WindowsKeyError;
            ShowText();
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (held.HasFlag(ModifierKeys.Control))
            modifiers |= HotkeyModifiers.Control;
        if (held.HasFlag(ModifierKeys.Alt))
            modifiers |= HotkeyModifiers.Alt;
        if (held.HasFlag(ModifierKeys.Shift))
            modifiers |= HotkeyModifiers.Shift;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!HotkeyText.TryCreate(modifiers, virtualKey, out var hotkey, out var reason))
        {
            _problem = reason;
            ShowText();
            return;
        }

        _value = HotkeyText.Format(hotkey!);
        EndCapture();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowText()
    {
        if (_capturing)
        {
            Text = _problem ?? CapturingText;
            Foreground = _problem is null ? SystemColors.ControlTextBrush : Brushes.Firebrick;
        }
        else if (_value.Length == 0)
        {
            Text = NoneText;
            Foreground = SystemColors.GrayTextBrush;
        }
        else
        {
            Text = _value;
            Foreground = SystemColors.ControlTextBrush;
        }
    }
}
