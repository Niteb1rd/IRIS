using System;
using Forms = System.Windows.Forms;

namespace Iris.App.Services;

public sealed class TrayIcon : IDisposable
{
    private const string DefaultTooltip = "IRIS";
    private const int MaxTooltipLength = 63;
    private const int MaxBalloonLength = 200;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _disableOverlayItem;
    private readonly Forms.ToolStripMenuItem _disablePanelItem;
    private bool _disposed;

    public TrayIcon()
    {
        var settingsItem = new Forms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _disableOverlayItem = new Forms.ToolStripMenuItem("Disable Overlay");
        _disableOverlayItem.Click += (_, _) => ToggleOverlayRequested?.Invoke(this, EventArgs.Empty);

        _disablePanelItem = new Forms.ToolStripMenuItem("Disable Info Panel");
        _disablePanelItem.Click += (_, _) => TogglePanelRequested?.Invoke(this, EventArgs.Empty);

        var openLogItem = new Forms.ToolStripMenuItem("Open log");
        openLogItem.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);

        var openConfigItem = new Forms.ToolStripMenuItem("Open config location");
        openConfigItem.Click += (_, _) => OpenConfigLocationRequested?.Invoke(this, EventArgs.Empty);

        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_disableOverlayItem);
        _menu.Items.Add(_disablePanelItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(openLogItem);
        _menu.Items.Add(openConfigItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(quitItem);

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcons.Tray,
            Text = DefaultTooltip,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? OpenLogRequested;

    public event EventHandler? OpenConfigLocationRequested;

    public event EventHandler? QuitRequested;

    public event EventHandler? ToggleOverlayRequested;

    public event EventHandler? TogglePanelRequested;

    public void SetOverlayDisabled(bool disabled)
    {
        if (_disposed)
            return;
        _disableOverlayItem.Checked = disabled;
        AppIcons.SetOverlayDisabled(disabled);
        _icon.Icon = AppIcons.Tray;
    }

    public void SetPanelDisabled(bool disabled)
    {
        if (!_disposed)
            _disablePanelItem.Checked = disabled;
    }

    public void SetActivity(string? activity)
    {
        if (_disposed)
            return;
        SetTooltip(activity is null ? DefaultTooltip : DefaultTooltip + " - " + activity);
    }

    public void ShowBalloon(string text)
    {
        if (_disposed)
            return;
        var shown = text.Length <= MaxBalloonLength ? text : text.Substring(0, MaxBalloonLength - 1) + "…";
        _icon.ShowBalloonTip(8000, "IRIS", shown, Forms.ToolTipIcon.Info);
    }

    private void SetTooltip(string text)
    {
        _icon.Text = text.Length <= MaxTooltipLength ? text : text.Substring(0, MaxTooltipLength);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
