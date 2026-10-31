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

public partial class ConfigWindow : Window, IImportHost
{
    private const string RuleValueCellTag = "RuleValueCell";

    private const string ConfigFileFilter = "IRIS config (*.txt)|*.txt|All files (*.*)|*.*";

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly CacheUpdater _updater;
    private readonly OverlayController _overlay;
    private readonly ObservableCollection<PlayerRowViewModel> _players = new();
    private readonly ObservableCollection<OrgRowViewModel> _orgs = new();
    private readonly ObservableCollection<RuleRowViewModel> _rules = new();
    private readonly CancellationTokenSource _closing = new();

    private readonly Dictionary<ListRowViewModel, Task> _checks = new();
    private bool _saving;
    private bool _isClosed;

    private bool _testStoppedByUser;

    private bool _ready;

    private string? _copiedInvalidKey;

    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _polling;
    private bool _closePromptOpen;
    private ConfigPoll? _pendingPoll;

    private int _busyCount;

    private string? _listName;
    private string _baseline = string.Empty;

    private static readonly string MissingListNotice = ConfigMessages.ListFileMissing(DataPaths.SocialListFileName);

    private string? _copiedInvalidSettingsKey;

    public ConfigWindow(AppServices services, ImportCoordinator imports, CacheUpdater updater, OverlayController overlay)
    {
        _services = services;
        _imports = imports;
        _updater = updater;
        _overlay = overlay;
        InitializeComponent();

        ConfigureSlider(PanelWidthSlider, AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax);
        ConfigureSlider(PanelTextSizeSlider, AppSettings.PanelTextSizeMin, AppSettings.PanelTextSizeMax);
        ConfigureSlider(PanelRightGapSlider, AppSettings.PanelRightGapPercentMin, AppSettings.PanelRightGapPercentMax);
        ConfigureSlider(PanelTopGapSlider, AppSettings.PanelTopGapPercentMin, AppSettings.PanelTopGapPercentMax);
        ConfigureSlider(TrackingFpsSlider, AppSettings.NameplateTrackingFpsMin, AppSettings.NameplateTrackingFpsMax);
        foreach (var slider in new[] { BoundaryTopSlider, BoundaryBottomSlider, BoundaryLeftSlider, BoundaryRightSlider })
            ConfigureSlider(slider, AppSettings.BoundaryPercentMin, AppSettings.BoundaryPercentMax);

        PlayersList.ItemsSource = _players;
        OrgsList.ItemsSource = _orgs;
        RulesList.ItemsSource = _rules;

        _players.CollectionChanged += OnRowsChanged;
        _orgs.CollectionChanged += OnRowsChanged;
        _rules.CollectionChanged += OnRowsChanged;

        LoadRowsFrom(services.CurrentConfig);
        LoadSettingsControls();
        _baseline = Snapshot();

        imports.Host = this;
        updater.RefreshingChanged += OnRefreshingChanged;
        RefreshCacheButton.IsEnabled = !updater.IsRefreshing;
        overlay.TestStateChanged += OnTestStateChanged;
        overlay.Hotkeys.StatusChanged += OnHotkeyStatusChanged;
        services.SettingsApplied += OnServicesSettingsApplied;
        UpdateTestButton();
        ApplyWindowIcon(this, EventArgs.Empty);
        AppIcons.Changed += ApplyWindowIcon;

        _watchTimer.Tick += OnWatchTick;
        Loaded += (_, _) =>
        {
            _watchTimer.Start();
            OnWatchTick(this, EventArgs.Empty);
        };

        Closed += (_, _) =>
        {
            _isClosed = true;
            AppIcons.Changed -= ApplyWindowIcon;
            _watchTimer.Stop();
            _watchTimer.Tick -= OnWatchTick;
            _updater.RefreshingChanged -= OnRefreshingChanged;
            _overlay.TestStateChanged -= OnTestStateChanged;
            _overlay.Hotkeys.StatusChanged -= OnHotkeyStatusChanged;
            _services.SettingsApplied -= OnServicesSettingsApplied;
            if (ReferenceEquals(_imports.Host, this))
                _imports.Host = null;
            _closing.Cancel();
            _closing.Dispose();

            if (_pendingPoll is { } parked)
            {
                _pendingPoll = null;
                try
                {
                    _services.ApplyPoll(parked);
                }
                catch (Exception ex)
                {
                    _services.Log.Write(DateTimeOffset.UtcNow, "Applying the last config check failed: " + ex);
                }
            }
        };

        _ready = true;
        RefreshStateUi();
        if (_services.ListFileMissing)
            SetFooter(MissingListNotice);
    }

    private void LoadRowsFrom(SocialConfig config)
    {
        _listName = config.Name;
        _checks.Clear();

        foreach (var row in _players.Cast<INotifyPropertyChanged>()
                     .Concat<INotifyPropertyChanged>(_orgs).Concat<INotifyPropertyChanged>(_rules).ToList())
            row.PropertyChanged -= OnRowPropertyChanged;
        _players.Clear();
        _orgs.Clear();
        _rules.Clear();

        foreach (var p in config.Players)
            _players.Add(new PlayerRowViewModel(p.Handle, p.Color, IsExcluded));
        foreach (var o in config.Orgs)
            _orgs.Add(new OrgRowViewModel(o.Sid, o.Priority, o.Color));
        foreach (var r in config.OrgRules.Where(rule => rule.Kind != AttributeKind.NoMatch))
            _rules.Add(new RuleRowViewModel(r.Kind, r.Value, r.Priority, r.Color));

        var noMatch = NoMatchRule.Find(config) ?? NoMatchRule.CreateDefault();
        _rules.Add(new RuleRowViewModel(AttributeKind.NoMatch, null, NoMatchRule.Priority, noMatch.Color));

        foreach (var row in NameRows().ToList())
            StartCheck(row);
    }

    private bool IsExcluded(string handle) => _services.Exclusions.IsExcluded(handle);

    private void ApplyWindowIcon(object? sender, EventArgs e)
    {
        if (AppIcons.WindowIcon is { } icon)
            Icon = icon;
    }
}
