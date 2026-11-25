using System.Text.Json;
using EasyTierExt.App.Controls;
using EasyTierExt.App.Native;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Configuration;
using EasyTierExt.HostCore.Core;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;
using EasyTierExt.HostCore.Plugins;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace EasyTierExt.App;

public sealed partial class MainWindow : Window, IHostUiBridge
{
    private readonly string _baseDir;
    private readonly ClientConfigStore _configStore;
    private readonly ClientConfig _config;
    private readonly Dictionary<string, bool> _pluginStates;
    private readonly JobObjectManager _jobObject;
    private readonly EasyTierManager _etManager;
    private readonly DiscoveryService _discovery;
    private readonly MarketplaceService _marketplace;
    private readonly PluginManager _pluginManager;
    private readonly TrayIconService _trayIcon;
    private readonly ViewportHostController _viewportController;
    private readonly IntPtr _hwnd;

    private string _activeRoute = "core:overview";
    private string _selectedNetworkId;
    private string _peerFilterNetworkId = "__all__";
    private bool _isExitingForReal;
    private DeclarativePluginRenderer? _activePluginRenderer;
    private readonly Dictionary<string, Border> _navButtons = new(StringComparer.Ordinal);
    private List<SourcedRegistryPlugin> _cachedOfficialItems = [];
    private List<SourcedRegistryPlugin> _cachedThirdPartyItems = [];
    private bool _registryLoaded;
    private bool _registryLoading;
    private string _activeMarketTab = "official";
    private string _marketSearchQuery = string.Empty;
    private string _marketSelectedCategory = "__all__";
    private StackPanel? _marketCardsContainer;
    private bool _suppressMarketFilterEvents;

    private ScrollViewer? _overviewScrollViewer;
    private TextBlock? _overviewCoreInfoText;
    private StackPanel? _overviewPeerRowsPanel;
    private readonly Dictionary<string, ToggleSwitch> _overviewToggles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _togglingNetworkIds = new(StringComparer.Ordinal);
    private bool _suppressOverviewToggleEvents;

    private ToggleSwitch? _runOnStartupSwitch;
    private bool _suppressStartupSwitchEvents;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();

        _baseDir = AppContext.BaseDirectory;
        _hwnd = WindowNative.GetWindowHandle(this);

        // Window sizing & custom dark titlebar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragTitleRegion);
        AppWindow.Resize(new SizeInt32(1140, 740));
        if (AppWindow.TitleBar is { } tb)
        {
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = Color.FromArgb(255, 209, 213, 219);
            tb.ButtonHoverBackgroundColor = Color.FromArgb(255, 46, 46, 54);
        }

        var appDataDir = ClientConfigStore.GetDefaultAppDataRoot();
        _configStore = new ClientConfigStore(appDataDir, _baseDir);
        _config = _configStore.Load();
        _pluginStates = _configStore.LoadPluginStates();
        _selectedNetworkId = _config.Networks[0].NetworkId;

        _jobObject = new JobObjectManager();
        _etManager = new EasyTierManager(_baseDir, _jobObject);
        _discovery = new DiscoveryService(
            () => _etManager.GetNetworkStatuses(_config.Networks),
            netId => _etManager.GetPeers(netId));

        _viewportController = new ViewportHostController(
            _hwnd,
            RootGrid,
            (pluginId, vp) => _pluginManager!.SendViewportChangedAsync(pluginId, vp));

        _marketplace = new MarketplaceService(_baseDir, appDataDir);
        _pluginManager = new PluginManager(
            _jobObject,
            () => _etManager.GetNetworkStatuses(_config.Networks),
            netId => _etManager.GetPeers(netId),
            _discovery,
            this,
            dataRootDirectory: appDataDir);

        _trayIcon = new TrayIconService(
            onShow: () => DispatcherQueue.TryEnqueue(RestoreFromTray),
            isAutoStartEnabled: () => _config.RunOnStartup,
            onToggleAutoStart: () => DispatcherQueue.TryEnqueue(() => _ = ApplyRunOnStartupAsync(!_config.RunOnStartup, showErrorDialog: false)),
            onExit: () => DispatcherQueue.TryEnqueue(() => _ = ShutdownAndExitAsync()));

        if (startMinimized)
        {
            AppWindow.Hide();
        }

        AppWindow.Closing += (_, args) =>
        {
            if (!_isExitingForReal && _config.MinimizeToTray)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        _etManager.NetworksChanged += networks => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStatusPill(networks);
            _ = _pluginManager.BroadcastNetworksChangedAsync(networks);
            if (_activeRoute == "core:overview")
            {
                RefreshOverviewInPlace(networks);
            }
        });

        _etManager.PeersUpdated += (netId, peers) => DispatcherQueue.TryEnqueue(() =>
        {
            _ = _pluginManager.BroadcastPeersUpdatedAsync(netId, peers);
            if (_activeRoute == "core:overview")
            {
                RefreshOverviewPeersRows();
            }
        });

        _etManager.LogAppended += (netId, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeRoute == "core:quick" && netId == _selectedNetworkId && _logTextBox is not null)
            {
                _logTextBox.Text = string.Join(Environment.NewLine, _etManager.GetLogs(netId));
            }
        });

        BuildStaticMenus();
        _ = InitializeServicesAsync();
    }

    private TextBox? _logTextBox;

    private async Task InitializeServicesAsync()
    {
        if (_config.RunOnStartup)
        {
            try
            {
                AutoStartService.SetEnabled(true);
            }
            catch
            {
                _config.RunOnStartup = AutoStartService.IsEnabled();
            }
        }

        _discovery.Start();
        _pluginManager.StartServer();

        var installed = _marketplace.ScanInstalledPlugins(_pluginStates);
        _pluginManager.SetInstalledPlugins(installed);
        RebuildPluginMenus();

        foreach (var plugin in installed.Where(p => p.Enabled))
        {
            try { await _pluginManager.StartPluginAsync(plugin.Manifest.Id); } catch { }
        }

        foreach (var net in _config.Networks.Where(n => n.AutoStart))
        {
            try { await _etManager.StartNetworkAsync(net, () => _config.Networks); } catch { }
        }

        NavigateTo("core:overview");
    }

    private void RestoreFromTray()
    {
        Activate();
        AppWindow.Show();
        Win32Helpers.ShowWindow(_hwnd, Win32Helpers.SW_RESTORE);
        Win32Helpers.SetForegroundWindow(_hwnd);
    }

    private async Task ApplyRunOnStartupAsync(bool enabled, bool showErrorDialog)
    {
        try
        {
            AutoStartService.SetEnabled(enabled);
            _config.RunOnStartup = enabled;
            _configStore.Save(_config);
        }
        catch (Exception ex)
        {
            _config.RunOnStartup = AutoStartService.IsEnabled();
            if (showErrorDialog)
            {
                await ShowMessageAsync("开机自启动设置失败", ex.Message);
            }
        }
        finally
        {
            if (_runOnStartupSwitch is not null && _runOnStartupSwitch.IsOn != _config.RunOnStartup)
            {
                _suppressStartupSwitchEvents = true;
                try
                {
                    _runOnStartupSwitch.IsOn = _config.RunOnStartup;
                }
                finally
                {
                    _suppressStartupSwitchEvents = false;
                }
            }
        }
    }

    private async Task ShutdownAndExitAsync()
    {
        _isExitingForReal = true;
        _viewportController.Dispose();
        await _pluginManager.DisposeAsync();
        await _discovery.DisposeAsync();
        await _etManager.DisposeAsync();
        _trayIcon.Dispose();
        _jobObject.Dispose();
        Close();
        Application.Current.Exit();
    }

    #region Sidebar Navigation

    private void BuildStaticMenus()
    {
        AddNavButton(CoreMenuPanel, "core:overview", "网络概览");
        AddNavButton(CoreMenuPanel, "core:quick", "快速组网");
        AddNavButton(MarketMenuPanel, "market:main", "插件中心");
    }

    private void RebuildPluginMenus()
    {
        while (PluginMenuPanel.Children.Count > 1)
        {
            PluginMenuPanel.Children.RemoveAt(1);
        }
        foreach (var key in _navButtons.Keys.Where(k => k.StartsWith("plugin:", StringComparison.Ordinal)).ToList())
        {
            _navButtons.Remove(key);
        }

        foreach (var (pluginId, menu) in _pluginManager.GetActivePluginMenus())
        {
            AddNavButton(PluginMenuPanel, $"plugin:{pluginId}:{menu.Id}", menu.Title);
        }
        HighlightActiveNav();
    }

    private void AddNavButton(StackPanel parent, string route, string title)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var indicator = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = "indicator"
        };
        var text = new TextBlock
        {
            Text = title,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(indicator, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(indicator);
        row.Children.Add(text);
        border.Child = row;

        border.PointerPressed += (_, _) => NavigateTo(route);
        _navButtons[route] = border;
        parent.Children.Add(border);
    }

    private void HighlightActiveNav()
    {
        foreach (var (route, border) in _navButtons)
        {
            bool active = route == _activeRoute;
            border.Background = new SolidColorBrush(active ? Color.FromArgb(255, 37, 37, 43) : Colors.Transparent);
            if (border.Child is Grid g && g.Children[0] is Border ind && g.Children[1] is TextBlock tb)
            {
                ind.Background = new SolidColorBrush(active ? Color.FromArgb(255, 124, 92, 255) : Colors.Transparent);
                tb.Foreground = new SolidColorBrush(active ? Color.FromArgb(255, 243, 244, 246) : Color.FromArgb(255, 185, 190, 200));
                tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    private void NavigateTo(string route)
    {
        _activeRoute = route;
        HighlightActiveNav();
        RenderActiveRoute();
    }

    private void UpdateStatusPill(List<HostNetworkStatus> networks)
    {
        int online = networks.Count(n => n.Running);
        StatusPillText.Text = $"在线网络: {online}";
        StatusDot.Fill = new SolidColorBrush(online > 0
            ? Color.FromArgb(255, 34, 197, 94)
            : Color.FromArgb(255, 107, 114, 128));
    }

    #endregion

    #region Page Rendering

    private void RenderActiveRoute()
    {
        _logTextBox = null;
        _activePluginRenderer = null;

        if (_activeRoute != "core:overview")
        {
            _overviewScrollViewer = null;
            _overviewCoreInfoText = null;
            _overviewPeerRowsPanel = null;
            _overviewToggles.Clear();
        }
        if (_activeRoute != "core:quick")
        {
            _runOnStartupSwitch = null;
        }

        if (!_activeRoute.StartsWith("plugin:", StringComparison.Ordinal))
        {
            _viewportController.HideAll();
        }

        try
        {
            if (_activeRoute == "core:overview")
            {
                PageTitleText.Text = "网络概览";
                RenderOverviewPage();
            }
            else if (_activeRoute == "core:quick")
            {
                PageTitleText.Text = "快速组网";
                RenderQuickSetupPage();
            }
            else if (_activeRoute == "market:main")
            {
                PageTitleText.Text = "插件中心";
                RenderMarketplacePage();
            }
            else if (_activeRoute.StartsWith("plugin:", StringComparison.Ordinal))
            {
                var parts = _activeRoute.Split(':', 3);
                if (parts.Length == 3)
                {
                    var plugin = _pluginManager.GetInstalledPlugins().FirstOrDefault(p => p.Manifest.Id == parts[1]);
                    var menuTitle = plugin?.Manifest.Menus.FirstOrDefault(m => m.Id == parts[2])?.Title ?? parts[2];
                    PageTitleText.Text = menuTitle;
                    RenderPluginPage(parts[1], parts[2]);
                }
            }
        }
        catch (Exception ex)
        {
            _viewportController.HideAll();
            ContentHost.Children.Clear();
            ContentHost.Children.Add(new TextBlock
            {
                Text = $"页面渲染异常: {ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
    }

    private void RenderOverviewPage()
    {
        var stack = new StackPanel { Spacing = 14 };
        var statuses = _etManager.GetNetworkStatuses(_config.Networks);
        UpdateStatusPill(statuses);

        var headerBar = new Grid();
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var coreWarn = _etManager.IsCoreAvailable ? "核心就绪 (easytier-core.exe)" : "警告: ./core/easytier-core.exe 缺失";
        var infoText = new TextBlock
        {
            Text = coreWarn,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_etManager.IsCoreAvailable ? Color.FromArgb(255, 156, 163, 175) : Color.FromArgb(255, 248, 113, 113))
        };
        _overviewCoreInfoText = infoText;

        var addBtn = new Button { Content = "+ 新建网络实例" };
        addBtn.Click += (_, _) =>
        {
            var np = new NetworkProfile { DisplayName = $"网络 {_config.Networks.Count + 1}" };
            _config.Networks.Add(np);
            _selectedNetworkId = np.NetworkId;
            _configStore.Save(_config);
            RenderActiveRoute();
        };
        Grid.SetColumn(infoText, 0);
        Grid.SetColumn(addBtn, 1);
        headerBar.Children.Add(infoText);
        headerBar.Children.Add(addBtn);
        stack.Children.Add(headerBar);

        _overviewToggles.Clear();
        foreach (var net in _config.Networks)
        {
            var st = statuses.FirstOrDefault(s => s.NetworkId == net.NetworkId) ?? new HostNetworkStatus();

            var statusGrid = new Grid { ColumnSpacing = 12 };
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = $"{net.DisplayName}  ({net.NetworkName})",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var toggle = new ToggleSwitch
            {
                IsOn = st.Running,
                OnContent = "已连接",
                OffContent = "已断开",
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            _overviewToggles[net.NetworkId] = toggle;
            toggle.Toggled += async (_, _) =>
            {
                if (_suppressOverviewToggleEvents) return;
                _togglingNetworkIds.Add(net.NetworkId);
                toggle.IsEnabled = false;
                try
                {
                    if (toggle.IsOn)
                        await _etManager.StartNetworkAsync(net, () => _config.Networks);
                    else
                        await _etManager.StopNetworkAsync(net.NetworkId, () => _config.Networks);
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync("操作失败", ex.Message);
                }
                finally
                {
                    _togglingNetworkIds.Remove(net.NetworkId);
                    toggle.IsEnabled = true;
                    RefreshOverviewInPlace();
                }
            };

            var delBtn = new Button
            {
                Content = "删除",
                IsEnabled = _config.Networks.Count > 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            delBtn.Click += async (_, _) =>
            {
                await _etManager.StopNetworkAsync(net.NetworkId, () => _config.Networks);
                _config.Networks.Remove(net);
                if (_selectedNetworkId == net.NetworkId)
                    _selectedNetworkId = _config.Networks[0].NetworkId;
                _configStore.Save(_config);
                RenderActiveRoute();
            };

            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(toggle, 1);
            Grid.SetColumn(delBtn, 2);
            statusGrid.Children.Add(titleText);
            statusGrid.Children.Add(toggle);
            statusGrid.Children.Add(delBtn);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 38)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 10, 18, 10),
                Child = statusGrid
            };
            stack.Children.Add(card);
        }

        // Merged Peer Nodes Card
        var peerCard = CreateCard("组网 Peer 节点列表");
        var peerStack = (StackPanel)peerCard.Child;

        var filterBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var filterCombo = new ComboBox { MinWidth = 220 };
        filterCombo.Items.Add(new ComboBoxItem { Content = "全部在线网络", Tag = "__all__" });
        foreach (var n in _config.Networks)
        {
            filterCombo.Items.Add(new ComboBoxItem { Content = $"{n.DisplayName} ({n.NetworkName})", Tag = n.NetworkId });
        }
        filterCombo.SelectedIndex = Math.Max(0, _config.Networks.FindIndex(n => n.NetworkId == _peerFilterNetworkId) + 1);
        filterCombo.SelectionChanged += (_, _) =>
        {
            if (filterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag != _peerFilterNetworkId)
            {
                _peerFilterNetworkId = tag;
                RefreshOverviewPeersRows();
            }
        };
        var refreshBtn = new Button { Content = "刷新节点" };
        refreshBtn.Click += (_, _) => RefreshOverviewInPlace();
        filterBar.Children.Add(filterCombo);
        filterBar.Children.Add(refreshBtn);
        peerStack.Children.Add(filterBar);

        peerStack.Children.Add(CreatePeerRow("所属网络", "主机名", "虚拟 IP", "传输模式", "延迟 (RTT)", "丢包率", "NAT 类型", isHeader: true));
        _overviewPeerRowsPanel = new StackPanel();
        peerStack.Children.Add(_overviewPeerRowsPanel);
        RefreshOverviewPeersRows();

        stack.Children.Add(peerCard);

        _overviewScrollViewer = new ScrollViewer { Content = stack };
        ContentHost.Children.Clear();
        ContentHost.Children.Add(_overviewScrollViewer);
    }

    private void RefreshOverviewInPlace(List<HostNetworkStatus>? statuses = null)
    {
        if (_activeRoute != "core:overview") return;
        if (_overviewScrollViewer is null || _overviewToggles.Count != _config.Networks.Count ||
            _config.Networks.Any(n => !_overviewToggles.ContainsKey(n.NetworkId)))
        {
            RenderOverviewPage();
            return;
        }

        statuses ??= _etManager.GetNetworkStatuses(_config.Networks);
        UpdateStatusPill(statuses);

        if (_overviewCoreInfoText is not null)
        {
            _overviewCoreInfoText.Text = _etManager.IsCoreAvailable ? "核心就绪 (easytier-core.exe)" : "警告: ./core/easytier-core.exe 缺失";
        }

        foreach (var net in _config.Networks)
        {
            if (_togglingNetworkIds.Contains(net.NetworkId)) continue;
            if (!_overviewToggles.TryGetValue(net.NetworkId, out var toggle)) continue;

            var running = statuses.FirstOrDefault(s => s.NetworkId == net.NetworkId)?.Running ?? false;
            if (toggle.IsOn != running)
            {
                _suppressOverviewToggleEvents = true;
                try
                {
                    toggle.IsOn = running;
                }
                finally
                {
                    _suppressOverviewToggleEvents = false;
                }
            }
        }

        RefreshOverviewPeersRows();
    }

    private void RefreshOverviewPeersRows()
    {
        if (_overviewPeerRowsPanel is null) return;

        _overviewPeerRowsPanel.Children.Clear();
        var peers = _etManager.GetPeers(_peerFilterNetworkId == "__all__" ? null : _peerFilterNetworkId, includeSelf: true);
        if (peers.Count == 0)
        {
            _overviewPeerRowsPanel.Children.Add(new TextBlock
            {
                Text = "当前暂无在线节点",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128)),
                Margin = new Thickness(8, 12, 8, 4)
            });
            return;
        }

        foreach (var p in peers)
        {
            var displayHost = p.Hostname;
            var displayIp = string.IsNullOrWhiteSpace(p.Ipv4) ? "-" : p.Ipv4;
            var displayMode = p.RouteType.ToUpperInvariant();
            _overviewPeerRowsPanel.Children.Add(CreatePeerRow(
                p.NetworkName, displayHost, displayIp, displayMode, p.LatencyMs, p.LossRate, p.NatType, isHeader: false, isSelf: p.IsSelf));
        }
    }


    private void RenderQuickSetupPage()
    {
        var profile = GetSelectedProfile();
        var stack = new StackPanel { Spacing = 14 };

        var globalCard = CreateCard("客户端全局设置");
        var globalStack = (StackPanel)globalCard.Child;
        _runOnStartupSwitch = new ToggleSwitch
        {
            IsOn = _config.RunOnStartup,
            OnContent = "开启",
            OffContent = "关闭",
            MinWidth = 0
        };
        _runOnStartupSwitch.Toggled += (_, _) =>
        {
            if (_suppressStartupSwitchEvents || _runOnStartupSwitch is null) return;
            _ = ApplyRunOnStartupAsync(_runOnStartupSwitch.IsOn, showErrorDialog: true);
        };
        globalStack.Children.Add(OptionRow("开机自启动 (登录系统后静默驻留托盘)", _runOnStartupSwitch));
        stack.Children.Add(globalCard);

        stack.Children.Add(CreateNetworkSelectorBar());

        var card = CreateCard("基础组网参数 (LuCI 快速配置)");
        var cardStack = (StackPanel)card.Child;

        var displayBox = new TextBox { Text = profile.DisplayName, MinWidth = 260 };
        var nameBox = new TextBox { Text = profile.NetworkName, MinWidth = 260 };
        var secretBox = new TextBox { Text = profile.NetworkSecret, MinWidth = 260 };

        var modeCombo = new ComboBox { MinWidth = 260 };
        modeCombo.Items.Add(new ComboBoxItem { Content = "公共服务器发现 (推荐)", Tag = "public" });
        modeCombo.Items.Add(new ComboBoxItem { Content = "手动指定 Peer 节点", Tag = "manual" });
        modeCombo.Items.Add(new ComboBoxItem { Content = "仅本地监听 (独立建网)", Tag = "standalone" });
        modeCombo.SelectedIndex = profile.ConnectionMode switch { "manual" => 1, "standalone" => 2, _ => 0 };

        var serverBox = new TextBox
        {
            Text = profile.ConnectionMode == "manual" ? string.Join(", ", profile.PeerUrls) : profile.PublicServer,
            MinWidth = 320,
            PlaceholderText = "tcp://public.easytier.top:11010"
        };

        var ipModeCombo = new ComboBox { MinWidth = 260 };
        ipModeCombo.Items.Add(new ComboBoxItem { Content = "DHCP 自动获取", Tag = "dhcp" });
        ipModeCombo.Items.Add(new ComboBoxItem { Content = "静态指定 IPv4", Tag = "static" });
        ipModeCombo.SelectedIndex = profile.Ipv4Mode == "static" ? 1 : 0;

        var staticIpBox = new TextBox { Text = profile.StaticIpv4, MinWidth = 260, PlaceholderText = "10.10.10.10/24" };
        var hostBox = new TextBox { Text = profile.Hostname, MinWidth = 260 };
        var autoSwitch = new ToggleSwitch { IsOn = profile.AutoStart, OnContent = "是", OffContent = "否", MinWidth = 0 };

        cardStack.Children.Add(OptionRow("配置名称", displayBox));
        cardStack.Children.Add(OptionRow("网络名称 (Network Name)", nameBox));
        cardStack.Children.Add(OptionRow("网络密钥 (Secret)", secretBox));
        cardStack.Children.Add(OptionRow("连接方式", modeCombo));
        cardStack.Children.Add(OptionRow("服务器 / Peer 地址", serverBox));
        cardStack.Children.Add(OptionRow("虚拟 IP 模式", ipModeCombo));
        cardStack.Children.Add(OptionRow("静态 IPv4 (CIDR)", staticIpBox));
        cardStack.Children.Add(OptionRow("本机节点名", hostBox));
        cardStack.Children.Add(OptionRow("随软件自动启动", autoSwitch));

        // Hidden / Collapsed Advanced Settings inside Quick Setup
        var cidrBox = new TextBox { Text = string.Join(", ", profile.ProxyCidrs), MinWidth = 300, PlaceholderText = "如: 192.168.10.0/24" };
        var listenerBox = new TextBox { Text = string.Join(", ", profile.Listeners), MinWidth = 300, PlaceholderText = "tcp://0.0.0.0:11010, udp://0.0.0.0:11010" };
        var encOff = new ToggleSwitch { IsOn = profile.DisableEncryption, OnContent = "禁用", OffContent = "启用", MinWidth = 0 };
        var encAlg = new ComboBox { MinWidth = 200 };
        foreach (var alg in new[] { "aes-gcm", "aes-256-gcm", "chacha20", "xor" }) encAlg.Items.Add(alg);
        encAlg.SelectedItem = profile.EncryptionAlgorithm;
        if (encAlg.SelectedIndex < 0) encAlg.SelectedIndex = 0;

        var devNameBox = new TextBox { Text = profile.DevName, MinWidth = 200, PlaceholderText = $"默认 et_{profile.NetworkId}" };
        var mtuBox = new NumberBox { Minimum = 1200, Maximum = 1500, Value = Math.Clamp(profile.Mtu, 1200, 1500), MinWidth = 200, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var latFirst = new ToggleSwitch { IsOn = profile.LatencyFirst, OnContent = "开启", OffContent = "关闭", MinWidth = 0 };
        var kcpProxy = new ToggleSwitch { IsOn = profile.EnableKcpProxy, OnContent = "开启", OffContent = "关闭", MinWidth = 0 };
        var quicProxy = new ToggleSwitch { IsOn = profile.EnableQuicProxy, OnContent = "开启", OffContent = "关闭", MinWidth = 0 };
        var noTun = new ToggleSwitch { IsOn = profile.NoTun, OnContent = "开启", OffContent = "关闭", MinWidth = 0 };

        var advStack = new StackPanel { Spacing = 10, Padding = new Thickness(0, 6, 0, 2) };
        advStack.Children.Add(OptionRow("子网代理 CIDR (逗号分隔)", cidrBox));
        advStack.Children.Add(OptionRow("本地监听地址 (逗号分隔)", listenerBox));
        advStack.Children.Add(OptionRow("禁用传输加密", encOff));
        advStack.Children.Add(OptionRow("加密算法", encAlg));
        advStack.Children.Add(OptionRow("TUN 网卡名称", devNameBox));
        advStack.Children.Add(OptionRow("MTU 大小", mtuBox));
        advStack.Children.Add(OptionRow("延迟优先路由", latFirst));
        advStack.Children.Add(OptionRow("启用 KCP TCP 代理", kcpProxy));
        advStack.Children.Add(OptionRow("启用 QUIC TCP 代理", quicProxy));
        advStack.Children.Add(OptionRow("无 TUN 模式 (仅转发)", noTun));

        var advExpander = new Expander
        {
            Header = "高级设置",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(255, 28, 28, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
            Content = advStack
        };

        void SaveForm()
        {
            profile.DisplayName = displayBox.Text.Trim();
            profile.NetworkName = nameBox.Text.Trim();
            profile.NetworkSecret = secretBox.Text.Trim();
            profile.ConnectionMode = ((ComboBoxItem)modeCombo.SelectedItem!).Tag?.ToString() ?? "public";
            if (profile.ConnectionMode == "manual")
            {
                profile.PeerUrls = serverBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else
            {
                profile.PublicServer = serverBox.Text.Trim();
            }
            profile.Ipv4Mode = ((ComboBoxItem)ipModeCombo.SelectedItem!).Tag?.ToString() ?? "dhcp";
            profile.StaticIpv4 = staticIpBox.Text.Trim();
            profile.Hostname = hostBox.Text.Trim();
            profile.AutoStart = autoSwitch.IsOn;

            profile.ProxyCidrs = cidrBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            profile.Listeners = listenerBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            profile.DisableEncryption = encOff.IsOn;
            profile.EncryptionAlgorithm = encAlg.SelectedItem?.ToString() ?? "aes-gcm";
            profile.DevName = devNameBox.Text.Trim();
            profile.Mtu = double.IsNaN(mtuBox.Value) ? 1360 : (int)mtuBox.Value;
            profile.LatencyFirst = latFirst.IsOn;
            profile.EnableKcpProxy = kcpProxy.IsOn;
            profile.EnableQuicProxy = quicProxy.IsOn;
            profile.NoTun = noTun.IsOn;

            _configStore.Save(_config);
        }

        var btnBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        var saveBtn = new Button { Content = "保存配置" };
        saveBtn.Click += async (_, _) =>
        {
            SaveForm();
            await ShowMessageAsync("已保存", $"网络 '{profile.DisplayName}' 配置已保存。");
        };
        var saveAndStartBtn = new Button
        {
            Content = "保存并连接",
            Background = new SolidColorBrush(Color.FromArgb(255, 124, 92, 255)),
            Foreground = new SolidColorBrush(Colors.White)
        };
        saveAndStartBtn.Click += async (_, _) =>
        {
            SaveForm();
            try
            {
                await _etManager.StartNetworkAsync(profile, () => _config.Networks);
                NavigateTo("core:overview");
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("启动失败", ex.Message);
            }
        };
        btnBar.Children.Add(saveBtn);
        btnBar.Children.Add(saveAndStartBtn);
        cardStack.Children.Add(btnBar);

        stack.Children.Add(card);
        stack.Children.Add(advExpander);

        // Logs at the very bottom of Settings
        var logCard = CreateCard($"核心运行日志 ({profile.DisplayName})");
        var logStack = (StackPanel)logCard.Child;
        var clearLogBtn = new Button { Content = "清空日志", HorizontalAlignment = HorizontalAlignment.Right };
        clearLogBtn.Click += (_, _) =>
        {
            _etManager.ClearLogs(profile.NetworkId);
            if (_logTextBox is not null) _logTextBox.Text = string.Empty;
        };
        _logTextBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 200,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Text = string.Join(Environment.NewLine, _etManager.GetLogs(profile.NetworkId))
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_logTextBox, ScrollBarVisibility.Auto);
        logStack.Children.Add(clearLogBtn);
        logStack.Children.Add(_logTextBox);
        stack.Children.Add(logCard);

        ContentHost.Children.Clear();
        ContentHost.Children.Add(new ScrollViewer { Content = stack });
    }


    private void RenderMarketplacePage()
    {
        var stack = new StackPanel { Spacing = 14 };

        // 1. Top Header: Official / Third-party Tabs + Actions
        var topRow = new Grid { ColumnSpacing = 12 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabsBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        tabsBar.Children.Add(CreateMarketTabButton("official", "官方商店"));
        tabsBar.Children.Add(CreateMarketTabButton("third_party", "第三方商店"));

        var actionBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var importBtn = new Button { Content = "本地导入 (.zip)" };
        importBtn.Click += async (_, _) =>
        {
            var zipPath = Win32Helpers.OpenZipFileDialog(_hwnd);
            if (string.IsNullOrWhiteSpace(zipPath)) return;
            try
            {
                var manifest = _marketplace.InstallFromLocalZip(zipPath);
                _pluginStates[manifest.Id] = true;
                _configStore.SavePluginStates(_pluginStates);
                _pluginManager.SetInstalledPlugins(_marketplace.ScanInstalledPlugins(_pluginStates));
                await _pluginManager.StartPluginAsync(manifest.Id);
                RebuildPluginMenus();
                RenderMarketplacePage();
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("导入失败", ex.Message);
            }
        };

        var sourceMgrBtn = new Button
        {
            Content = "⚙ 来源管理",
            Background = new SolidColorBrush(Color.FromArgb(255, 39, 39, 46)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 63, 63, 74))
        };
        sourceMgrBtn.Click += async (_, _) => await ShowSourceManagerDialogAsync();

        actionBar.Children.Add(importBtn);
        actionBar.Children.Add(sourceMgrBtn);
        Grid.SetColumn(tabsBar, 0);
        Grid.SetColumn(actionBar, 1);
        topRow.Children.Add(tabsBar);
        topRow.Children.Add(actionBar);
        stack.Children.Add(topRow);
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(255, 40, 40, 46)), Margin = new Thickness(0, -10, 0, 2) });

        // 2. Store Heading + Refresh
        var headingGrid = new Grid { ColumnSpacing = 12 };
        headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headingText = new StackPanel { Spacing = 2 };
        headingText.Children.Add(new TextBlock
        {
            Text = _activeMarketTab == "official" ? "官方商店" : "第三方商店",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
        });
        headingText.Children.Add(new TextBlock
        {
            Text = _activeMarketTab == "official" ? "官方发布与审核的插件" : "来自已启用第三方订阅来源的扩展插件",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
        });

        var refreshBtn = new Button { Content = _registryLoading ? "刷新中..." : "刷新", IsEnabled = !_registryLoading, VerticalAlignment = VerticalAlignment.Center };
        refreshBtn.Click += (_, _) =>
        {
            if (!_registryLoading)
            {
                _ = LoadMarketplaceRegistryAsync();
            }
        };
        Grid.SetColumn(headingText, 0);
        Grid.SetColumn(refreshBtn, 1);
        headingGrid.Children.Add(headingText);
        headingGrid.Children.Add(refreshBtn);
        stack.Children.Add(headingGrid);

        // 3. Search & Category Filter Bar
        var filterGrid = new Grid { ColumnSpacing = 10 };
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchBox = new TextBox
        {
            PlaceholderText = "搜索插件...",
            Text = _marketSearchQuery
        };
        searchBox.TextChanged += (_, _) =>
        {
            if (_suppressMarketFilterEvents) return;
            _marketSearchQuery = searchBox.Text;
            RefreshMarketplaceCardsList();
        };

        var activeItems = _activeMarketTab == "official" ? _cachedOfficialItems : _cachedThirdPartyItems;
        var categories = activeItems
            .Select(x => string.IsNullOrWhiteSpace(x.Entry.Category) ? "扩展工具" : x.Entry.Category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var catCombo = new ComboBox { MinWidth = 150 };
        catCombo.Items.Add(new ComboBoxItem { Content = "全部分类", Tag = "__all__" });
        foreach (var cat in categories)
        {
            catCombo.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
        }

        _suppressMarketFilterEvents = true;
        try
        {
            var matchedIdx = categories.FindIndex(c => string.Equals(c, _marketSelectedCategory, StringComparison.OrdinalIgnoreCase));
            catCombo.SelectedIndex = matchedIdx >= 0 ? matchedIdx + 1 : 0;
            if (matchedIdx < 0) _marketSelectedCategory = "__all__";
        }
        finally
        {
            _suppressMarketFilterEvents = false;
        }

        catCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressMarketFilterEvents) return;
            if (catCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _marketSelectedCategory = tag;
                RefreshMarketplaceCardsList();
            }
        };

        Grid.SetColumn(searchBox, 0);
        Grid.SetColumn(catCombo, 1);
        filterGrid.Children.Add(searchBox);
        filterGrid.Children.Add(catCombo);
        stack.Children.Add(filterGrid);

        // 4. Cards Container
        _marketCardsContainer = new StackPanel { Spacing = 10 };
        stack.Children.Add(_marketCardsContainer);

        if (!_registryLoaded && !_registryLoading)
        {
            _ = LoadMarketplaceRegistryAsync();
        }
        RefreshMarketplaceCardsList();

        ContentHost.Children.Clear();
        ContentHost.Children.Add(new ScrollViewer { Content = stack });
    }

    private Border CreateMarketTabButton(string tabKey, string label)
    {
        bool active = _activeMarketTab == tabKey;
        var container = new StackPanel { Spacing = 6, Background = new SolidColorBrush(Colors.Transparent) };
        container.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(active ? Color.FromArgb(255, 243, 244, 246) : Color.FromArgb(255, 142, 146, 159))
        });
        container.Children.Add(new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(active ? Color.FromArgb(255, 124, 92, 255) : Colors.Transparent)
        });

        var wrapper = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2, 4, 2, 0),
            Child = container
        };
        wrapper.PointerPressed += (_, _) =>
        {
            if (_activeMarketTab != tabKey)
            {
                _activeMarketTab = tabKey;
                _marketSelectedCategory = "__all__";
                RenderMarketplacePage();
            }
        };
        return wrapper;
    }

    private void RefreshMarketplaceCardsList()
    {
        if (_marketCardsContainer is null) return;
        _marketCardsContainer.Children.Clear();

        if (!_registryLoaded && _registryLoading)
        {
            _marketCardsContainer.Children.Add(new TextBlock
            {
                Text = "正在加载插件列表...",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175)),
                Margin = new Thickness(4, 12, 4, 4)
            });
            return;
        }

        var installed = _pluginManager.GetInstalledPlugins();
        var activeItems = _activeMarketTab == "official" ? _cachedOfficialItems : _cachedThirdPartyItems;
        var query = _marketSearchQuery.Trim();

        var filtered = activeItems.Where(item =>
        {
            if (_marketSelectedCategory != "__all__")
            {
                var cat = string.IsNullOrWhiteSpace(item.Entry.Category) ? "扩展工具" : item.Entry.Category.Trim();
                if (!string.Equals(cat, _marketSelectedCategory, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(query))
            {
                return item.Entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       item.Entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       item.Entry.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       item.Entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }).ToList();

        if (filtered.Count == 0)
        {
            if (_activeMarketTab == "third_party" && !_config.MarketplaceSources.Any(s => !s.IsOfficial && s.Enabled))
            {
                var emptyCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 35)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(24)
                };
                var emptyStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "尚未添加或启用第三方插件源",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "点击右上角「来源管理」可订阅其他厂商或社区维护的插件仓库。",
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                var openMgrBtn = new Button
                {
                    Content = "⚙ 打开来源管理",
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                openMgrBtn.Click += async (_, _) => await ShowSourceManagerDialogAsync();
                emptyStack.Children.Add(openMgrBtn);
                emptyCard.Child = emptyStack;
                _marketCardsContainer.Children.Add(emptyCard);
            }
            else
            {
                _marketCardsContainer.Children.Add(new TextBlock
                {
                    Text = "暂无匹配的插件",
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128)),
                    Margin = new Thickness(4, 12, 4, 4)
                });
            }
        }
        else
        {
            foreach (var item in filtered)
            {
                var inst = installed.FirstOrDefault(i => string.Equals(i.Manifest.Id, item.Entry.Id, StringComparison.OrdinalIgnoreCase));
                _marketCardsContainer.Children.Add(CreateMarketPluginCard(item, inst));
            }
        }

        // On Official tab, also show locally imported plugins not present in official registry
        if (_activeMarketTab == "official")
        {
            var officialIds = _cachedOfficialItems.Select(x => x.Entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localOnly = installed
                .Where(i => !officialIds.Contains(i.Manifest.Id))
                .Where(i => string.IsNullOrEmpty(query) ||
                            i.Manifest.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            i.Manifest.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (localOnly.Count > 0)
            {
                _marketCardsContainer.Children.Add(new TextBlock
                {
                    Text = "本地导入 / 已安装插件",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175)),
                    Margin = new Thickness(2, 10, 0, 2)
                });
                foreach (var inst in localOnly)
                {
                    var localItem = new SourcedRegistryPlugin
                    {
                        Entry = new RegistryPluginEntry
                        {
                            Id = inst.Manifest.Id,
                            Name = inst.Manifest.Name,
                            Version = inst.Manifest.Version,
                            Author = string.IsNullOrWhiteSpace(inst.Manifest.Author) ? "本地导入" : inst.Manifest.Author,
                            Description = inst.Manifest.Description,
                            Icon = inst.Manifest.Icon,
                            Category = inst.Manifest.Category
                        },
                        SourceId = "local",
                        SourceName = "本地安装",
                        SourceUrl = string.Empty,
                        IsOfficial = false
                    };
                    _marketCardsContainer.Children.Add(CreateMarketPluginCard(localItem, inst, isLocalOnly: true));
                }
            }
        }
    }

    private Border CreateMarketPluginCard(SourcedRegistryPlugin item, InstalledPluginInfo? inst, bool isLocalOnly = false)
    {
        var entry = item.Entry;
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(255, 43, 43, 52)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(entry.Icon) ? "🧩" : entry.Icon,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
        });

        var authorPart = string.IsNullOrWhiteSpace(entry.Author) ? "Unknown" : entry.Author;
        var metaText = item.IsOfficial || isLocalOnly
            ? $"{authorPart} · v{entry.Version}"
            : $"{item.SourceName} · {authorPart} · v{entry.Version}";
        info.Children.Add(new TextBlock
        {
            Text = metaText,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
        });

        info.Children.Add(new TextBlock
        {
            Text = entry.Description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219))
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        bool hasUpdate = inst is not null && !isLocalOnly && MarketplaceService.IsUpdateAvailable(inst.Manifest.Version, entry.Version);

        if (inst is null || hasUpdate)
        {
            var installBtn = new Button
            {
                Content = hasUpdate ? $"更新 v{entry.Version}" : "安装",
                Background = new SolidColorBrush(Color.FromArgb(255, 124, 92, 255)),
                Foreground = new SolidColorBrush(Colors.White)
            };
            installBtn.Click += async (_, _) =>
            {
                installBtn.IsEnabled = false;
                installBtn.Content = "准备下载...";
                var progress = new Progress<double>(pct =>
                    DispatcherQueue.TryEnqueue(() => installBtn.Content = $"下载中 {(int)pct}%"));
                try
                {
                    await _pluginManager.StopPluginAsync(entry.Id);
                    var manifest = await _marketplace.InstallFromRegistryAsync(
                        entry,
                        item.SourceUrl,
                        _config.GithubMirrorPrefix,
                        progress);
                    _pluginStates[manifest.Id] = true;
                    _configStore.SavePluginStates(_pluginStates);
                    _pluginManager.SetInstalledPlugins(_marketplace.ScanInstalledPlugins(_pluginStates));
                    await _pluginManager.StartPluginAsync(manifest.Id);
                    RebuildPluginMenus();
                    RenderMarketplacePage();
                }
                catch (Exception ex)
                {
                    installBtn.Content = hasUpdate ? $"更新 v{entry.Version}" : "安装";
                    installBtn.IsEnabled = true;
                    await ShowMessageAsync("安装失败", ex.Message);
                }
            };
            actions.Children.Add(installBtn);
        }
        else
        {
            actions.Children.Add(new Button
            {
                Content = "已安装",
                IsEnabled = false
            });
        }

        if (inst is not null)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = inst.Enabled,
                OnContent = "已启用",
                OffContent = "已停用",
                MinWidth = 0
            };
            toggle.Toggled += async (_, _) =>
            {
                _pluginStates[inst.Manifest.Id] = toggle.IsOn;
                _configStore.SavePluginStates(_pluginStates);
                inst.Enabled = toggle.IsOn;
                if (toggle.IsOn)
                    await _pluginManager.StartPluginAsync(inst.Manifest.Id);
                else
                    await _pluginManager.StopPluginAsync(inst.Manifest.Id);
                RebuildPluginMenus();
            };

            var uninstBtn = new Button { Content = "卸载" };
            uninstBtn.Click += async (_, _) =>
            {
                await _pluginManager.StopPluginAsync(inst.Manifest.Id);
                _pluginStates.Remove(inst.Manifest.Id);
                _configStore.SavePluginStates(_pluginStates);
                _marketplace.UninstallPlugin(inst.Manifest.Id);
                _pluginManager.SetInstalledPlugins(_marketplace.ScanInstalledPlugins(_pluginStates));
                RebuildPluginMenus();
                RenderMarketplacePage();
            };

            actions.Children.Add(toggle);
            actions.Children.Add(uninstBtn);
        }

        Grid.SetColumn(iconBox, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(iconBox);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 16),
            Child = grid
        };
    }

    private async Task ShowSourceManagerDialogAsync()
    {
        _config.EnsureSourcesNormalized();
        var rootPanel = new StackPanel { Spacing = 14, MinWidth = 540 };

        ContentDialog? dialog = null;

        // Header
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new StackPanel { Spacing = 2 };
        headerText.Children.Add(new TextBlock
        {
            Text = "来源管理",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
        });
        headerText.Children.Add(new TextBlock
        {
            Text = "管理插件商店及订阅来源",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
        });
        var closeXBtn = new Button { Content = "×", Padding = new Thickness(10, 4, 10, 4) };
        closeXBtn.Click += (_, _) => dialog?.Hide();
        Grid.SetColumn(headerText, 0);
        Grid.SetColumn(closeXBtn, 1);
        headerGrid.Children.Add(headerText);
        headerGrid.Children.Add(closeXBtn);
        rootPanel.Children.Add(headerGrid);

        // Built-in official source
        var official = _config.MarketplaceSources.First(s => s.IsOfficial);
        rootPanel.Children.Add(new TextBlock
        {
            Text = "内置来源",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219))
        });

        var officialCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12)
        };
        var officialStack = new StackPanel { Spacing = 4 };
        var offTop = new Grid();
        offTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        offTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        offTop.Children.Add(new TextBlock
        {
            Text = official.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
        });
        var offBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 30, 58, 42)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock { Text = "已启用", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128)) }
        };
        Grid.SetColumn(offBadge, 1);
        offTop.Children.Add(offBadge);
        officialStack.Children.Add(offTop);
        officialStack.Children.Add(new TextBlock
        {
            Text = "官方内置 · 不可移除",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
        });
        officialStack.Children.Add(new TextBlock
        {
            Text = official.Url,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128))
        });
        officialCard.Child = officialStack;
        rootPanel.Children.Add(officialCard);

        // Third-party sources header + inline editor + list
        var thirdHeader = new Grid();
        thirdHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        thirdHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        thirdHeader.Children.Add(new TextBlock
        {
            Text = "第三方来源",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219))
        });

        var addSourceBtn = new Button { Content = "+ 添加来源" };
        Grid.SetColumn(addSourceBtn, 1);
        thirdHeader.Children.Add(addSourceBtn);
        rootPanel.Children.Add(thirdHeader);

        string? editingSourceId = null;
        var editorNameBox = new TextBox { PlaceholderText = "如：Community Store" };
        var editorUrlBox = new TextBox { PlaceholderText = "https://vendor.github.io/store/registry.json" };
        var editorStatusText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113)), Visibility = Visibility.Collapsed };
        var editorSaveBtn = new Button
        {
            Content = "保存并校验",
            Background = new SolidColorBrush(Color.FromArgb(255, 124, 92, 255)),
            Foreground = new SolidColorBrush(Colors.White)
        };
        var editorCancelBtn = new Button { Content = "取消" };
        var editorBtnBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        editorBtnBar.Children.Add(editorCancelBtn);
        editorBtnBar.Children.Add(editorSaveBtn);

        var editorStack = new StackPanel { Spacing = 8 };
        editorStack.Children.Add(new TextBlock { Text = "来源名称", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219)) });
        editorStack.Children.Add(editorNameBox);
        editorStack.Children.Add(new TextBlock { Text = "索引地址 (registry.json URL)", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219)) });
        editorStack.Children.Add(editorUrlBox);
        editorStack.Children.Add(editorStatusText);
        editorStack.Children.Add(editorBtnBar);

        var editorCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 26, 26, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 124, 92, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed,
            Child = editorStack
        };
        rootPanel.Children.Add(editorCard);

        var thirdListPanel = new StackPanel { Spacing = 8 };
        rootPanel.Children.Add(thirdListPanel);

        void RebuildThirdPartyCards()
        {
            thirdListPanel.Children.Clear();
            var list = _config.MarketplaceSources.Where(s => !s.IsOfficial).ToList();
            if (list.Count == 0)
            {
                thirdListPanel.Children.Add(new TextBlock
                {
                    Text = "暂无第三方订阅来源，点击右上角「+ 添加来源」新增。",
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128)),
                    Margin = new Thickness(2, 4, 2, 4)
                });
                return;
            }

            foreach (var src in list)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 38)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12, 14, 12)
                };
                var cs = new StackPanel { Spacing = 4 };
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleStack = new StackPanel { Spacing = 2 };
                titleStack.Children.Add(new TextBlock
                {
                    Text = src.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
                });
                titleStack.Children.Add(new TextBlock
                {
                    Text = $"{src.CachedPluginCount} 个插件",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
                });

                var enableSwitch = new ToggleSwitch
                {
                    IsOn = src.Enabled,
                    OnContent = "● 启用",
                    OffContent = "停用",
                    MinWidth = 0
                };
                enableSwitch.Toggled += (_, _) =>
                {
                    src.Enabled = enableSwitch.IsOn;
                    _configStore.Save(_config);
                };

                Grid.SetColumn(titleStack, 0);
                Grid.SetColumn(enableSwitch, 1);
                top.Children.Add(titleStack);
                top.Children.Add(enableSwitch);
                cs.Children.Add(top);

                cs.Children.Add(new TextBlock
                {
                    Text = src.Url,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128))
                });

                var btns = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var refBtn = new Button { Content = "刷新" };
                refBtn.Click += async (_, _) =>
                {
                    refBtn.IsEnabled = false;
                    refBtn.Content = "刷新中...";
                    try
                    {
                        var url = MarketplaceService.ApplyGithubMirror(src.Url, _config.GithubMirrorPrefix);
                        var idx = await _marketplace.FetchRegistryAsync(url);
                        src.CachedPluginCount = idx.Plugins.Count;
                        _configStore.Save(_config);
                        RebuildThirdPartyCards();
                    }
                    catch
                    {
                        refBtn.Content = "刷新失败";
                        refBtn.IsEnabled = true;
                    }
                };
                var editBtn = new Button { Content = "编辑" };
                editBtn.Click += (_, _) =>
                {
                    editingSourceId = src.Id;
                    editorNameBox.Text = src.Name;
                    editorUrlBox.Text = src.Url;
                    editorStatusText.Visibility = Visibility.Collapsed;
                    editorCard.Visibility = Visibility.Visible;
                };
                var delBtn = new Button { Content = "移除" };
                delBtn.Click += (_, _) =>
                {
                    _config.MarketplaceSources.Remove(src);
                    _configStore.Save(_config);
                    RebuildThirdPartyCards();
                };

                btns.Children.Add(refBtn);
                btns.Children.Add(editBtn);
                btns.Children.Add(delBtn);
                cs.Children.Add(btns);
                card.Child = cs;
                thirdListPanel.Children.Add(card);
            }
        }

        addSourceBtn.Click += (_, _) =>
        {
            editingSourceId = null;
            editorNameBox.Text = string.Empty;
            editorUrlBox.Text = string.Empty;
            editorStatusText.Visibility = Visibility.Collapsed;
            editorCard.Visibility = Visibility.Visible;
        };

        editorCancelBtn.Click += (_, _) => editorCard.Visibility = Visibility.Collapsed;

        editorSaveBtn.Click += async (_, _) =>
        {
            var name = editorNameBox.Text.Trim();
            var url = editorUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                editorStatusText.Text = "名称和索引地址均不能为空。";
                editorStatusText.Visibility = Visibility.Visible;
                return;
            }

            editorSaveBtn.IsEnabled = false;
            editorSaveBtn.Content = "校验中...";
            editorStatusText.Visibility = Visibility.Collapsed;
            int count = 0;
            try
            {
                var fetchUrl = MarketplaceService.ApplyGithubMirror(url, _config.GithubMirrorPrefix);
                var idx = await _marketplace.FetchRegistryAsync(fetchUrl);
                count = idx.Plugins.Count;
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(idx.StoreName))
                {
                    name = idx.StoreName;
                }
            }
            catch (Exception ex)
            {
                editorStatusText.Text = $"校验失败（仍可保存后重试）: {ex.Message}";
                editorStatusText.Visibility = Visibility.Visible;
            }

            if (editingSourceId is null)
            {
                _config.MarketplaceSources.Add(new MarketplaceSourceConfig
                {
                    Name = name,
                    Url = url,
                    Enabled = true,
                    IsOfficial = false,
                    CachedPluginCount = count
                });
            }
            else
            {
                var target = _config.MarketplaceSources.FirstOrDefault(s => s.Id == editingSourceId);
                if (target is not null)
                {
                    target.Name = name;
                    target.Url = url;
                    target.CachedPluginCount = count;
                }
            }

            _configStore.Save(_config);
            editorSaveBtn.Content = "保存并校验";
            editorSaveBtn.IsEnabled = true;
            editorCard.Visibility = Visibility.Collapsed;
            RebuildThirdPartyCards();
        };

        RebuildThirdPartyCards();

        // GitHub download mirror prefix (optional)
        var mirrorBox = new TextBox
        {
            Text = _config.GithubMirrorPrefix,
            PlaceholderText = "可选，如 https://ghfast.top/（留空直连，仅作用于 GitHub 直链）"
        };
        var mirrorExpander = new Expander
        {
            Header = "GitHub 下载加速（可选）",
            IsExpanded = !string.IsNullOrWhiteSpace(_config.GithubMirrorPrefix),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = mirrorBox
        };
        rootPanel.Children.Add(mirrorExpander);

        dialog = new ContentDialog
        {
            Content = new ScrollViewer { Content = rootPanel, MaxHeight = 560 },
            PrimaryButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
        _config.GithubMirrorPrefix = mirrorBox.Text.Trim();
        _configStore.Save(_config);
        _ = LoadMarketplaceRegistryAsync();
    }


    private async Task LoadMarketplaceRegistryAsync()
    {
        if (_registryLoading) return;
        _registryLoading = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeRoute == "market:main")
            {
                RefreshMarketplaceCardsList();
            }
        });

        try
        {
            _config.EnsureSourcesNormalized();
            var official = _config.MarketplaceSources.First(s => s.IsOfficial);
            var officialTask = _marketplace.FetchOfficialStoreAsync(official, _config.GithubMirrorPrefix);
            var thirdTask = _marketplace.FetchThirdPartyStoresAsync(_config.MarketplaceSources, _config.GithubMirrorPrefix);
            await Task.WhenAll(officialTask, thirdTask);
            _cachedOfficialItems = await officialTask;
            _cachedThirdPartyItems = await thirdTask;
            _configStore.Save(_config);
        }
        catch
        {
            _cachedOfficialItems = [];
            _cachedThirdPartyItems = [];
        }
        finally
        {
            _registryLoaded = true;
            _registryLoading = false;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeRoute == "market:main")
            {
                RenderMarketplacePage();
            }
        });
    }

    private void RenderPluginPage(string pluginId, string menuId)
    {
        var schema = _pluginManager.GetMenuSchema(pluginId, menuId);
        if (schema is null)
        {
            _viewportController.HideAll();
            ContentHost.Children.Clear();
            ContentHost.Children.Add(new TextBlock
            {
                Text = "正在等待插件初始化界面...",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
            });
            return;
        }

        var renderer = new DeclarativePluginRenderer(
            (pid, _, action) => _pluginManager.SendUiActionAsync(pid, action),
            _viewportController);
        _activePluginRenderer = renderer;

        var element = renderer.Render(pluginId, menuId, schema, _pluginManager.GetMenuValues(pluginId, menuId));
        ContentHost.Children.Clear();
        ContentHost.Children.Add(element);
    }

    #endregion

    #region IHostUiBridge

    public void OnPluginMenusChanged() => DispatcherQueue.TryEnqueue(RebuildPluginMenus);

    public void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeRoute == $"plugin:{pluginId}:{menuId}")
            {
                RenderPluginPage(pluginId, menuId);
            }
        });
    }

    public void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeRoute == $"plugin:{pluginId}:{menuId}" && _activePluginRenderer is not null)
            {
                _activePluginRenderer.ApplyValues(values);
            }
        });
    }

    public void OnPluginNavigateRequested(string pluginId, string menuId)
    {
        DispatcherQueue.TryEnqueue(() => NavigateTo($"plugin:{pluginId}:{menuId}"));
    }

    public Task<UiGetViewportHostResult> GetViewportHostAsync(string pluginId, string menuId)
    {
        var tcs = new TaskCompletionSource<UiGetViewportHostResult>();
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.SetResult(_viewportController.GetOrCreateHost(pluginId, menuId));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    #endregion

    #region UI Helpers

    private NetworkProfile GetSelectedProfile()
    {
        return _config.Networks.FirstOrDefault(n => n.NetworkId == _selectedNetworkId) ?? _config.Networks[0];
    }

    private UIElement CreateNetworkSelectorBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        bar.Children.Add(new TextBlock
        {
            Text = "当前编辑网络:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
        });
        bar.Children.Add(CreateNetworkSelectorCombo(id =>
        {
            _selectedNetworkId = id;
            RenderActiveRoute();
        }));
        return bar;
    }

    private ComboBox CreateNetworkSelectorCombo(Action<string> onChanged)
    {
        var combo = new ComboBox { MinWidth = 240 };
        int idx = 0;
        for (int i = 0; i < _config.Networks.Count; i++)
        {
            var n = _config.Networks[i];
            combo.Items.Add(new ComboBoxItem { Content = $"{n.DisplayName} ({n.NetworkName})", Tag = n.NetworkId });
            if (n.NetworkId == _selectedNetworkId) idx = i;
        }
        combo.SelectedIndex = idx;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string id && id != _selectedNetworkId)
            {
                onChanged(id);
            }
        };
        return combo;
    }

    private static Border CreateCard(string title)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246)),
            Margin = new Thickness(0, 0, 0, 2)
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 16, 18, 16),
            Child = stack
        };
    }

    private static Grid OptionRow(string label, FrameworkElement control)
    {
        var row = new Grid { MinHeight = 36, ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235))
        };
        Grid.SetColumn(tb, 0);
        Grid.SetColumn(control, 1);
        row.Children.Add(tb);
        row.Children.Add(control);
        return row;
    }

    private static Grid CreatePeerRow(string net, string host, string ip, string mode, string lat, string loss, string nat, bool isHeader, bool isSelf = false)
    {
        var g = new Grid
        {
            Padding = new Thickness(10, 7, 10, 7),
            Margin = isSelf ? new Thickness(0, 2, 0, 4) : new Thickness(0),
            Background = isHeader
                ? new SolidColorBrush(Color.FromArgb(255, 26, 26, 30))
                : (isSelf ? new SolidColorBrush(Color.FromArgb(255, 38, 31, 61)) : null),
            BorderBrush = new SolidColorBrush(isSelf
                ? Color.FromArgb(255, 124, 92, 255)
                : Color.FromArgb(255, 42, 42, 48)),
            BorderThickness = isHeader
                ? new Thickness(0)
                : (isSelf ? new Thickness(3, 1, 1, 1) : new Thickness(0, 0, 0, 1)),
            CornerRadius = (isHeader || isSelf) ? new CornerRadius(6) : new CornerRadius(0)
        };
        string[] cols = [net, host, ip, mode, lat, loss, nat];
        for (int i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Color fg = isHeader
                ? Color.FromArgb(255, 156, 163, 175)
                : (isSelf
                    ? (i == 2
                        ? Color.FromArgb(255, 56, 189, 248)
                        : (i is 1 or 3
                            ? Color.FromArgb(255, 196, 181, 253)
                            : Color.FromArgb(255, 243, 244, 246)))
                    : Color.FromArgb(255, 229, 231, 235));
            var tb = new TextBlock
            {
                Text = cols[i],
                FontSize = isHeader ? 12 : 13,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(tb, i);
            g.Children.Add(tb);
        }
        return g;
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    #endregion
}
