using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ResourceManager.Core;
using WinForms = System.Windows.Forms;

namespace ResourceManager.App;

public partial class MainWindow : Window
{
    private readonly NodeStore store = new();
    private readonly PeerNode node;
    private readonly LanDiscoveryService discovery;
    private readonly PeerClient client;
    private readonly UpnpGatewayClient upnpGatewayClient;
    private readonly UpnpPortMappingManager mappingManager;
    private readonly GatewayDiscoveryService gatewayDiscovery;
    private readonly DownloadManager downloader;
    private readonly ResourceCatalog catalog;
    private readonly UpdateClient updateClient;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly WinForms.NotifyIcon tray;
    private readonly MemoryStream iconStream;
    private readonly System.Drawing.Icon appIcon;
    private readonly Dictionary<string, string> peerStatus = [];
    private readonly Dictionary<string, IReadOnlyList<RemoteResource>> peerCatalogs = [];
    private readonly Dictionary<string, ActiveDownload> activeDownloads = [];
    private readonly HashSet<string> removingDownloads = [];
    private byte[]? pendingAvatar;
    private bool refreshing;
    private bool exiting;
    private bool checkingUpdate;
    private bool restartAfterUpdate;
    private StagedUpdate? stagedUpdate;
    private readonly bool startedWithWindows;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly CancellationTokenSource downloadCancellation = new();
    private CancellationTokenSource? gatewayRefreshCancellation;
    private DateTimeOffset nextMappingMaintenance = DateTimeOffset.MinValue;
    private DateTimeOffset nextRouterInfoRefresh = DateTimeOffset.MinValue;
    private bool routerInfoRefreshing;
    private string? currentRouterWanIp;

    public ObservableCollection<PeerRow> Peers { get; } = [];
    public ObservableCollection<ResourceRow> RemoteResources { get; } = [];
    public ObservableCollection<LocalResourceRow> LocalResources { get; } = [];
    public ObservableCollection<FavoriteRow> Favorites { get; } = [];
    public ObservableCollection<DownloadRow> Downloads { get; } = [];
    public ObservableCollection<GatewayRow> Gateways { get; } = [];

    public MainWindow(bool startedWithWindows = false)
    {
        this.startedWithWindows = startedWithWindows;
        InitializeComponent();
        if (startedWithWindows)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        DataContext = this;
        discovery = new LanDiscoveryService(store);
        upnpGatewayClient = new UpnpGatewayClient();
        mappingManager = new UpnpPortMappingManager(store, upnpGatewayClient);
        node = new PeerNode(store, discovery, mappingManager);
        client = new PeerClient(store);
        gatewayDiscovery = new GatewayDiscoveryService(store, client);
        downloader = new DownloadManager(store, client);
        catalog = new ResourceCatalog(store);
        updateClient = new UpdateClient(store.DataDirectory);
        updateClient.CleanupOldDownloads();
        var settings = store.GetSettings();
        NicknameBox.Text = settings.Profile.Nickname;
        ListenPortBox.Text = settings.ListenPort.ToString();
        PeerPortBox.Text = settings.ListenPort.ToString();
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        AutoUpdateBox.IsChecked = settings.AutoUpdate;
        AutoStartBox.IsChecked = AutoStartManager.IsEnabled();
        DeviceIdText.Text = settings.Profile.DeviceId;
        UpdateStatusText.Text = $"当前版本 v{AppVersion}";
        pendingAvatar = settings.Profile.Avatar;
        AvatarPreview.Source = AvatarImage(pendingAvatar);
        UpdateIdentity();
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ResourceManager.AppIcon.ico")
            ?? throw new InvalidOperationException("缺少应用图标资源。");
        using var iconBytes = new MemoryStream();
        source.CopyTo(iconBytes);
        var data = iconBytes.ToArray();
        iconStream = new MemoryStream(data);
        appIcon = new System.Drawing.Icon(iconStream);
        Icon = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        tray = new WinForms.NotifyIcon
        {
            Icon = appIcon,
            Text = "资源管理器",
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip()
        };
        tray.ContextMenuStrip.Items.Add("打开资源管理器", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        tray.ContextMenuStrip.Items.Add("检查更新", null, (_, _) => Dispatcher.Invoke(async () => await CheckForUpdatesAsync(true)));
        tray.ContextMenuStrip.Items.Add("退出并停止共享", null, (_, _) => Dispatcher.Invoke(async () => await ExitAsync()));
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        timer.Tick += async (_, _) => await TimerTickAsync();
        RefreshLocalView();
        RefreshPeersView();
        RefreshGatewaysView();
        RefreshFavoritesView();
        RefreshDownloadsView();
        UpdatePageHeader();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (startedWithWindows)
        {
            Hide();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            ShowActivated = true;
        }
        try
        {
            await node.StartAsync(store.GetSettings().ListenPort);
            var discoveryReady = await TryStartDiscoveryAsync();
            SetStatus(discoveryReady
                ? $"共享服务已启动，端口 {node.Port}；可通过局域网查找设备。"
                : $"共享服务已启动，端口 {node.Port}；局域网自动发现不可用，可继续手动连接。");
        }
        catch (Exception ex) { SetStatus($"监听失败：{ex.Message}。可在设置中更换端口后重试。"); }
        timer.Start();
        await RefreshAllAsync();
        await RefreshRouterInfoAsync();
        await MaintainMappingsAsync();
        try { AutoStartManager.RefreshEnabledPath(); } catch { }
        if (store.GetSettings().AutoUpdate && IsPackaged) _ = CheckForUpdatesAsync(false);
    }

    private async Task<bool> TryStartDiscoveryAsync()
    {
        if (discovery.IsRunning) return true;
        try
        {
            await discovery.StartAsync();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("局域网发现服务启动失败", ex);
            return false;
        }
    }

    private void UpdateIdentity()
    {
        var settings = store.GetSettings();
        IdentityText.Text = settings.Profile.Nickname;
        var ips = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString()).Take(3).ToArray();
        AddressText.Text = (ips.Length == 0 ? "本机 IP 未找到" : string.Join(" / ", ips)) + $" : {settings.ListenPort}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async Task TimerTickAsync()
    {
        await RefreshAllAsync();
        if (DateTimeOffset.UtcNow >= nextRouterInfoRefresh) await RefreshRouterInfoAsync();
        if (DateTimeOffset.UtcNow >= nextMappingMaintenance) await MaintainMappingsAsync();
    }

    private async Task MaintainMappingsAsync()
    {
        nextMappingMaintenance = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            await mappingManager.MaintainMappingsAsync(updateCancellation.Token);
            await RefreshRouterInfoAsync();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { AppLog.Write("维护 UPnP 映射失败", ex); }
    }

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    private static bool IsPackaged
    {
        get
        {
#if PUBLISHED_SINGLE_FILE
            return true;
#else
            return false;
#endif
        }
    }
    private static string KindText(ResourceKind kind) => kind == ResourceKind.File ? "文件" : "文件夹";
    private static string ModeText(PublishMode mode) => mode == PublishMode.Copy ? "复制副本" : "引用原位置";
    private static string SizeText(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";

    private static ImageSource? AvatarImage(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void RefreshPeersView()
    {
        var selected = (PeersGrid.SelectedItem as PeerRow)?.Peer.DeviceId;
        Peers.Clear();
        var notes = store.GetPeerNotes();
        foreach (var peer in store.GetPeers())
        {
            var endpoint = store.GetPeerEndpoints(peer.DeviceId)
                .FirstOrDefault(item => item.Ip == peer.Ip && item.Port == peer.Port && item.Source != "DeviceChanged");
            var gateway = endpoint?.GatewayId is null
                ? null
                : store.GetGateways().FirstOrDefault(item => item.Id == endpoint.GatewayId);
            var source = endpoint?.Kind == PeerEndpointKind.Gateway
                ? $"{gateway?.Name ?? "路由器入口"} · {peer.Port}"
                : endpoint is null ? "入口已移除" : "局域网直连";
            Peers.Add(new PeerRow(peer, peerStatus.GetValueOrDefault(peer.DeviceId, "未检查"),
                AvatarImage(peer.Avatar), source, notes.GetValueOrDefault(peer.DeviceId, "")));
        }
        PeersGrid.SelectedItem = Peers.FirstOrDefault(p => p.Peer.DeviceId == selected);
        RefreshSelectedResources();
    }

    private void RefreshGatewaysView(string? selectId = null)
    {
        var selected = selectId ?? (GatewayList?.SelectedItem as GatewayRow)?.Gateway.Id;
        Gateways.Clear();
        foreach (var gateway in store.GetGateways())
            Gateways.Add(new GatewayRow(gateway, gateway.Name, gateway.WanIp,
                $"端口 {gateway.PortStart}–{gateway.PortEnd}", gateway.LastStatus ?? "未检查"));
        if (GatewayList is null) return;
        GatewayList.SelectedItem = Gateways.FirstOrDefault(item => item.Gateway.Id == selected);
    }

    private void RefreshSelectedResources()
    {
        var selected = (RemoteGrid.SelectedItem as ResourceRow)?.Resource.Id;
        RemoteResources.Clear();
        if (PeersGrid.SelectedItem is not PeerRow row)
        {
            PeerHeading.Text = "选择一台设备";
            PeerEmptyText.Text = "从左侧选择一台设备，查看对方的资源。";
            UpdateActions();
            return;
        }
        PeerHeading.Text = $"{row.Nickname} 的资源 · {row.Status}";
        if (row.Status != "在线" || !peerCatalogs.TryGetValue(row.Peer.DeviceId, out var resources))
        {
            PeerEmptyText.Text = row.Status == "设备已变更"
                ? "这个地址现在属于另一台设备。请检查连接地址。"
                : "这台设备暂时无法连接。对方重新上线后会自动恢复。";
            UpdateActions();
            return;
        }
        PeerEmptyText.Text = "这台设备还没有发布资源。";
        foreach (var resource in resources)
            RemoteResources.Add(new ResourceRow(resource, resource.Name, KindText(resource.Kind), ModeText(resource.Mode), SizeText(resource.Size), resource.Available ? "可下载" : "原文件不可用"));
        RemoteGrid.SelectedItem = RemoteResources.FirstOrDefault(r => r.Resource.Id == selected);
        UpdateActions();
    }

    private void RefreshLocalView()
    {
        var selected = (LocalGrid.SelectedItem as LocalResourceRow)?.Resource.Id;
        LocalResources.Clear();
        foreach (var item in store.GetResources())
        {
            var info = catalog.Describe(item);
            LocalResources.Add(new LocalResourceRow(item, item.Name, KindText(item.Kind), ModeText(item.Mode), SizeText(info.Size), info.Available ? "可用" : "原文件不可用", item.SourcePath));
        }
        LocalGrid.SelectedItem = LocalResources.FirstOrDefault(r => r.Resource.Id == selected);
        UpdateActions();
    }

    private void RefreshFavoritesView()
    {
        var selected = (FavoritesGrid.SelectedItem as FavoriteRow)?.Favorite;
        Favorites.Clear();
        foreach (var item in store.GetFavorites())
        {
            var peer = store.GetPeer(item.PeerId);
            var state = peer is null ? "设备已移除" : peerStatus.GetValueOrDefault(item.PeerId, "未检查");
            var remote = peerCatalogs.GetValueOrDefault(item.PeerId)?.FirstOrDefault(r => r.Id == item.ResourceId);
            var status = state switch
            {
                "在线" when remote is null => "资源已撤销",
                "在线" when !remote.Available => "原文件不可用",
                "在线" => "可下载",
                "设备已变更" => "设备已变更",
                "设备已移除" => "设备已移除",
                _ => "离线/无法连接"
            };
            Favorites.Add(new FavoriteRow(item, item.Name, peer?.Nickname ?? "未知设备", KindText(item.Kind), status));
        }
        FavoritesGrid.SelectedItem = Favorites.FirstOrDefault(f => f.Favorite.PeerId == selected?.PeerId && f.Favorite.ResourceId == selected?.ResourceId);
        UpdateActions();
    }

    private void RefreshDownloadsView()
    {
        var selected = (DownloadsGrid.SelectedItem as DownloadRow)?.Job.Id;
        Downloads.Clear();
        foreach (var item in store.GetDownloads())
        {
            var name = store.GetPeer(item.PeerId)?.Nickname ?? "未知设备";
            Downloads.Add(new DownloadRow(item, item.ResourceName, name, item.Status,
                $"{SizeText(item.DownloadedBytes)} / {SizeText(item.TotalBytes)}", item.TargetPath, item.Error ?? ""));
        }
        DownloadsGrid.SelectedItem = Downloads.FirstOrDefault(d => d.Job.Id == selected);
        UpdateActions();
    }

    private async Task RefreshAllAsync()
    {
        if (refreshing || exiting) return;
        refreshing = true;
        try
        {
            foreach (var peer in store.GetPeers())
            {
                var candidates = store.GetPeerEndpoints(peer.DeviceId).Where(item => item.Source != "DeviceChanged")
                    .GroupBy(item => (item.Ip, item.Port))
                    .Select(group => group.OrderByDescending(item => item.LastSuccessUtc).First())
                    .OrderBy(item => item.Kind == PeerEndpointKind.Direct ? 0 : 1)
                    .ThenByDescending(item => item.LastSuccessUtc)
                    .ToArray();
                var connected = false;
                foreach (var endpoint in candidates)
                {
                    var candidate = peer with { Ip = endpoint.Ip, Port = endpoint.Port };
                    try
                    {
                        peerCatalogs[peer.DeviceId] = await client.GetResourcesAsync(candidate);
                        peerStatus[peer.DeviceId] = "在线";
                        connected = true;
                        break;
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("另一台设备") ||
                                                                 ex.Message.Contains("另一台"))
                    {
                        store.MarkEndpointDeviceChanged(endpoint);
                        peerStatus[peer.DeviceId] = "设备已变更";
                    }
                    catch { }
                }
                if (connected) continue;
                if (peerStatus.GetValueOrDefault(peer.DeviceId) != "设备已变更")
                    peerStatus[peer.DeviceId] = "离线";
                peerCatalogs.Remove(peer.DeviceId);
            }
            RefreshPeersView();
            RefreshFavoritesView();
            RefreshDownloadsView();
        }
        finally { refreshing = false; }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs is not null && NavList.SelectedIndex >= 0 && Tabs.SelectedIndex != NavList.SelectedIndex)
            Tabs.SelectedIndex = NavList.SelectedIndex;
    }

    private void UpdatePageHeader()
    {
        var pages = new (string Title, string Subtitle)[]
        {
            ("设备", "连接同事的电脑，浏览他们分享的资源"),
            ("我的发布", "决定哪些资源可以被其他设备看到"),
            ("收藏", "常用资源的快捷入口和当前状态"),
            ("下载", "查看传输进度，继续中断的任务"),
            ("设置", "管理资料、连接方式和运行偏好")
        };
        var page = pages[Math.Clamp(Tabs.SelectedIndex, 0, pages.Length - 1)];
        PageTitleText.Text = page.Title;
        PageSubtitleText.Text = page.Subtitle;
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs) || NavList is null) return;
        if (NavList.SelectedIndex != Tabs.SelectedIndex) NavList.SelectedIndex = Tabs.SelectedIndex;
        UpdatePageHeader();
        if (!IsLoaded) return;
        if (Tabs.SelectedIndex is 0 or 2) await RefreshAllAsync();
        if (Tabs.SelectedIndex == 1) RefreshLocalView();
        if (Tabs.SelectedIndex == 3) RefreshDownloadsView();
        if (Tabs.SelectedIndex == 4) await RefreshRouterInfoAsync();
    }

    private void PeersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeersGrid.SelectedItem is PeerRow row)
        {
            PeerIpBox.Text = row.Ip;
            PeerPortBox.Text = row.Peer.Port.ToString();
            if (PeerNoteBox is not null) PeerNoteBox.Text = row.Note;
        }
        else if (PeerNoteBox is not null) PeerNoteBox.Text = "";
        RefreshSelectedResources();
    }

    private void RemoteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void FavoritesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void LocalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void DownloadsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private void UpdateActions()
    {
        if (RemoteFavoriteButton is null || RemoteDownloadButton is null || FavoriteDownloadButton is null ||
            RemovePeerButton is null || RemoveResourceButton is null || RemoveFavoriteButton is null || UpdateAddressButton is null ||
            SavePeerNoteButton is null || ClearPeerNoteButton is null ||
            ResumeDownloadButton is null || OpenDownloadFolderButton is null || RemoveDownloadButton is null) return;
        var remote = RemoteGrid.SelectedItem as ResourceRow;
        var peer = PeersGrid.SelectedItem as PeerRow;
        RemoteFavoriteButton.IsEnabled = remote is not null && peer?.Status == "在线";
        RemoteDownloadButton.IsEnabled = remote?.Resource.Available == true && peer?.Status == "在线";
        FavoriteDownloadButton.IsEnabled = (FavoritesGrid.SelectedItem as FavoriteRow)?.Status == "可下载";
        RemovePeerButton.IsEnabled = peer is not null;
        UpdateAddressButton.IsEnabled = peer is not null;
        SavePeerNoteButton.IsEnabled = peer is not null;
        ClearPeerNoteButton.IsEnabled = peer is not null && !string.IsNullOrEmpty(peer.Note);
        RemoveResourceButton.IsEnabled = LocalGrid.SelectedItem is LocalResourceRow;
        RemoveFavoriteButton.IsEnabled = FavoritesGrid.SelectedItem is FavoriteRow;
        var download = DownloadsGrid.SelectedItem as DownloadRow;
        var removing = download is not null && removingDownloads.Contains(download.Job.Id);
        var active = download is not null && activeDownloads.ContainsKey(download.Job.Id);
        ResumeDownloadButton.IsEnabled = download is not null && download.Job.Status != "已完成" && !active && !removing;
        OpenDownloadFolderButton.IsEnabled = download is not null && !removing;
        RemoveDownloadButton.IsEnabled = download is not null && !removing;
        RemoveDownloadButton.Content = active ? "暂停并移除" : "移除任务";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PeerPortBox.Text, out var port)) throw new ArgumentException("端口无效。");
            var peer = await client.ConnectAsync(PeerIpBox.Text.Trim(), port);
            SetStatus($"已连接 {peer.Nickname}（{peer.Ip}:{peer.Port}）。");
            await RefreshAllAsync();
            PeersGrid.SelectedItem = Peers.FirstOrDefault(p => p.Peer.DeviceId == peer.DeviceId);
        }
        catch (Exception ex) { ShowError("连接失败", ex); }
    }

    private async void DiscoverPeers_Click(object sender, RoutedEventArgs e)
    {
        DiscoverPeersButton.IsEnabled = false;
        DiscoverPeersButton.Content = "正在查找…";
        SetStatus("正在查找同一局域网内的设备…");
        try
        {
            var found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(2), updateCancellation.Token);
            if (found.Count == 0)
            {
                SetStatus("没有找到其他设备。请确认对方软件正在运行，并允许公司网络访问。");
                return;
            }

            var attempts = found.Select(async item =>
            {
                try
                {
                    var peer = await client.ConnectAsync(item.Ip, item.Port, item.DeviceId, updateCancellation.Token);
                    return (Peer: peer, Error: (Exception?)null);
                }
                catch (Exception ex) { return (Peer: (PeerInfo?)null, Error: ex); }
            });
            var results = await Task.WhenAll(attempts);
            var connected = results.Where(result => result.Peer is not null).Select(result => result.Peer!).ToArray();
            await RefreshAllAsync();
            if (connected.Length == 1)
                PeersGrid.SelectedItem = Peers.FirstOrDefault(row => row.Peer.DeviceId == connected[0].DeviceId);
            var failed = results.Length - connected.Length;
            SetStatus(failed == 0
                ? $"找到并连接了 {connected.Length} 台设备。"
                : $"找到 {results.Length} 台设备，已连接 {connected.Length} 台，{failed} 台连接失败。");
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { ShowError("查找设备失败", ex); }
        finally
        {
            if (!exiting)
            {
                DiscoverPeersButton.Content = "查找局域网设备";
                DiscoverPeersButton.IsEnabled = true;
            }
        }
    }

    private async void AddGateway_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = GatewayNameBox.Text.Trim();
            var ip = GatewayIpBox.Text.Trim();
            var existing = store.GetGateways().FirstOrDefault(item => item.WanIp == ip);
            var gateway = store.SaveGateway(name, ip, existing?.Id);
            RefreshGatewaysView(gateway.Id);
            await RefreshGatewayAsync(gateway);
        }
        catch (Exception ex) { ShowError("添加路由器入口失败", ex); }
    }

    private async void SaveGatewayAddress_Click(object sender, RoutedEventArgs e)
    {
        if (GatewayList.SelectedItem is not GatewayRow row)
        {
            SetStatus("请先选择一个路由器入口。");
            return;
        }
        try
        {
            var gateway = store.SaveGateway(GatewayNameBox.Text, GatewayIpBox.Text, row.Gateway.Id);
            RefreshGatewaysView(gateway.Id);
            await RefreshGatewayAsync(gateway);
        }
        catch (Exception ex) { ShowError("修改路由器入口失败", ex); }
    }

    private async void RefreshGateway_Click(object sender, RoutedEventArgs e)
    {
        if (gatewayRefreshCancellation is not null)
        {
            gatewayRefreshCancellation.Cancel();
            return;
        }
        if (GatewayList.SelectedItem is not GatewayRow row)
        {
            SetStatus("请先选择一个路由器入口。");
            return;
        }
        await RefreshGatewayAsync(row.Gateway);
    }

    private async Task RefreshGatewayAsync(GatewayInfo gateway)
    {
        gatewayRefreshCancellation?.Cancel();
        gatewayRefreshCancellation?.Dispose();
        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(updateCancellation.Token);
        gatewayRefreshCancellation = refreshCancellation;
        GatewayRefreshButton.Content = "取消扫描";
        GatewayRefreshButton.IsEnabled = true;
        GatewayAddButton.IsEnabled = false;
        GatewaySaveButton.IsEnabled = false;
        GatewayRemoveButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(message =>
            {
                SetStatus(message + "…");
                GatewayProgressText.Text = message;
            });
            var result = await gatewayDiscovery.RefreshAsync(gateway, progress, refreshCancellation.Token);
            SetStatus(result.Status);
            GatewayProgressText.Text = result.Status;
            RefreshGatewaysView(gateway.Id);
            await RefreshAllAsync();
        }
        catch (OperationCanceledException)
        {
            SetStatus("路由器入口扫描已取消。");
            GatewayProgressText.Text = "扫描已取消";
        }
        catch (Exception ex) { ShowError("刷新路由器入口失败", ex); }
        finally
        {
            refreshCancellation.Dispose();
            if (ReferenceEquals(gatewayRefreshCancellation, refreshCancellation))
            {
                gatewayRefreshCancellation = null;
                GatewayRefreshButton.Content = "刷新";
                GatewayAddButton.IsEnabled = true;
                GatewaySaveButton.IsEnabled = true;
                GatewayRemoveButton.IsEnabled = true;
            }
        }
    }

    private void GatewayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GatewayList.SelectedItem is not GatewayRow row) return;
        GatewayNameBox.Text = row.Gateway.Name;
        GatewayIpBox.Text = row.Gateway.WanIp;
        GatewayProgressText.Text = row.Gateway.LastStatus ?? "未检查";
    }

    private void RemoveGateway_Click(object sender, RoutedEventArgs e)
    {
        if (GatewayList.SelectedItem is not GatewayRow row) return;
        var affected = store.GetPeerEndpoints(gatewayId: row.Gateway.Id)
            .Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).ToArray();
        store.RemoveGateway(row.Gateway.Id);
        foreach (var deviceId in affected)
        {
            peerStatus[deviceId] = "离线";
            peerCatalogs.Remove(deviceId);
        }
        RefreshGatewaysView();
        RefreshPeersView();
        SetStatus("已移除本机保存的路由器入口和端点缓存。");
    }

    private async Task RefreshRouterInfoAsync()
    {
        if (routerInfoRefreshing || exiting) return;
        routerInfoRefreshing = true;
        nextRouterInfoRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
        try
        {
            var router = await mappingManager.GetCurrentRouterAsync(updateCancellation.Token);
            if (router is null)
            {
                currentRouterWanIp = null;
                RouterAdapterText.Text = "无法确定";
                RouterLanText.Text = "未发现支持 UPnP IGD 的路由器";
                RouterWanText.Text = "无法读取，请在路由器信息页查看 WAN IP";
                RouterUpnpText.Text = "未检测到";
                RouterMappingText.Text = "尚未分配";
                CopyRouterWanButton.IsEnabled = false;
                RemoveRouterMappingButton.IsEnabled = false;
                return;
            }

            currentRouterWanIp = string.IsNullOrWhiteSpace(router.WanIp) ? null : router.WanIp;
            RouterAdapterText.Text = router.AdapterName;
            RouterLanText.Text = $"{router.RouterLanIp}（本机 {router.LanIp}）";
            RouterWanText.Text = currentRouterWanIp ?? "无法读取，请在路由器信息页查看 WAN IP";
            RouterUpnpText.Text = router.UpnpAvailable ? "已开启" : "不可用";
            var mapping = store.GetLocalPortMappings().FirstOrDefault(item => item.RouterUdn == router.RouterUdn);
            RouterMappingText.Text = mapping is null
                ? "尚未分配（只有外部设备请求扩展发现后才会申请）"
                : $"{mapping.ExternalPort} → {mapping.LanIp}:{mapping.InternalPort} · 长期保留";
            CopyRouterWanButton.IsEnabled = currentRouterWanIp is not null;
            RemoveRouterMappingButton.IsEnabled = mapping is not null;
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            AppLog.Write("读取当前路由器信息失败", ex);
            RouterWanText.Text = "无法读取，请在路由器信息页查看 WAN IP";
            RouterUpnpText.Text = "读取失败";
        }
        finally { routerInfoRefreshing = false; }
    }

    private void CopyRouterWan_Click(object sender, RoutedEventArgs e)
    {
        if (currentRouterWanIp is null) return;
        try
        {
            System.Windows.Clipboard.SetText(currentRouterWanIp);
            SetStatus($"已复制路由器外部入口 {currentRouterWanIp}，可提供给路由器外的设备。");
        }
        catch (Exception ex) { ShowError("复制外部入口失败", ex); }
    }

    private async void RemoveRouterMapping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = await mappingManager.RemoveCurrentMappingAsync(updateCancellation.Token);
            SetStatus(removed ? "已删除本机外部映射。" : "当前路由器没有本机映射。");
            await RefreshRouterInfoAsync();
        }
        catch (Exception ex) { ShowError("删除外部映射失败", ex); }
    }

    private async void UpdateAddress_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow selected) { SetStatus("请先选中设备。"); return; }
        try
        {
            if (!int.TryParse(PeerPortBox.Text, out var port)) throw new ArgumentException("端口无效。");
            await client.ConnectAsync(PeerIpBox.Text.Trim(), port, selected.Peer.DeviceId);
            SetStatus("设备地址已更新。");
            await RefreshAllAsync();
        }
        catch (Exception ex) { ShowError("更新地址失败", ex); }
    }

    private void RemovePeer_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow row) return;
        if (System.Windows.MessageBox.Show($"移除 {row.Nickname}？该设备的收藏和本机备注也会删除。", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        store.RemovePeer(row.Peer.DeviceId);
        peerStatus.Remove(row.Peer.DeviceId);
        peerCatalogs.Remove(row.Peer.DeviceId);
        RefreshPeersView(); RefreshFavoritesView();
        SetStatus("设备已移除，本机备注一并删除。");
    }

    private void SavePeerNote_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow row) { SetStatus("请先选中设备。"); return; }
        try
        {
            store.SavePeerNote(row.Peer.DeviceId, PeerNoteBox.Text);
            RefreshPeersView();
            SetStatus(PeerNoteBox.Text.Trim().Length == 0 ? "已清空本机备注。" : "备注已保存，仅本机可见。");
        }
        catch (Exception ex) { ShowError("保存备注失败", ex); }
    }

    private void ClearPeerNote_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow row) { SetStatus("请先选中设备。"); return; }
        try
        {
            store.SavePeerNote(row.Peer.DeviceId, "");
            PeerNoteBox.Text = "";
            RefreshPeersView();
            SetStatus("已清空本机备注。");
        }
        catch (Exception ex) { ShowError("清空备注失败", ex); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private void RefreshLocal_Click(object sender, RoutedEventArgs e) => RefreshLocalView();

    private async void PublishFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择要发布的文件" };
        if (dialog.ShowDialog(this) == true) await PublishAsync(dialog.FileName);
    }

    private async void PublishFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog { Description = "选择要发布的文件夹" };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK) await PublishAsync(dialog.SelectedPath);
    }

    private async Task PublishAsync(string path)
    {
        var dialog = new PublishModeDialog(Path.GetFileName(path)) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true) return;
        var mode = dialog.SelectedMode;
        try
        {
            SetStatus(mode == PublishMode.Copy ? "正在复制资源…" : "正在发布资源…");
            await Task.Run(() => store.AddResource(path, mode));
            RefreshLocalView();
            SetStatus("资源已发布，已连接设备刷新后即可看到。");
        }
        catch (Exception ex) { ShowError("发布失败", ex); }
    }

    private async void RemoveResource_Click(object sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not LocalResourceRow row) return;
        var message = row.Resource.Mode == PublishMode.Copy ? "撤销后，软件管理的副本也会删除。" : "撤销后，原位置的文件不会删除。";
        if (System.Windows.MessageBox.Show($"撤销“{row.Name}”？\n{message}", "确认撤销", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await Task.Run(() => store.RemoveResource(row.Resource.Id)); RefreshLocalView(); SetStatus("资源已撤销。"); }
        catch (Exception ex) { ShowError("撤销失败", ex); }
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow peer || RemoteGrid.SelectedItem is not ResourceRow row) return;
        store.SaveFavorite(new Favorite(peer.Peer.DeviceId, row.Resource.Id, row.Resource.Name, row.Resource.Kind));
        RefreshFavoritesView();
        SetStatus("已收藏资源入口。发布者离线时入口仍会保留。");
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is not FavoriteRow row) return;
        store.RemoveFavorite(row.Favorite.PeerId, row.Favorite.ResourceId);
        RefreshFavoritesView();
    }

    private void DownloadRemote_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow peer || RemoteGrid.SelectedItem is not ResourceRow row) return;
        if (peer.Status != "在线" || !row.Resource.Available) { SetStatus("资源当前不可下载。"); return; }
        BeginDownload(peer.Peer, row.Resource);
    }

    private void DownloadFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is not FavoriteRow row) return;
        if (row.Status != "可下载") { SetStatus("该收藏当前不可下载。"); return; }
        var peer = store.GetPeer(row.Favorite.PeerId);
        var resource = peerCatalogs.GetValueOrDefault(row.Favorite.PeerId)?.FirstOrDefault(r => r.Id == row.Favorite.ResourceId);
        if (peer is not null && resource is not null) BeginDownload(peer, resource);
    }

    private void BeginDownload(PeerInfo peer, RemoteResource resource)
    {
        using var dialog = new WinForms.FolderBrowserDialog { Description = "选择下载保存目录" };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
        try
        {
            var job = downloader.CreateJob(peer, resource, dialog.SelectedPath);
            RefreshDownloadsView();
            Tabs.SelectedIndex = 3;
            QueueDownload(job.Id);
        }
        catch (Exception ex) { ShowError("创建下载失败", ex); }
    }

    private void QueueDownload(string id)
    {
        if (activeDownloads.ContainsKey(id)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(downloadCancellation.Token);
        var active = new ActiveDownload(cancellation);
        activeDownloads.Add(id, active);
        active.Task = StartDownloadAsync(id, cancellation.Token);
        UpdateActions();
    }

    private async Task StartDownloadAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<DownloadJob>(UpdateDownloadProgressSafely);
            await Task.Run(
                () => downloader.RunAsync(id, progress, cancellationToken),
                cancellationToken);
            if (!exiting && !removingDownloads.Contains(id)) SetStatus("下载完成。本地文件在发布者离线时仍可使用。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!exiting && !removingDownloads.Contains(id)) SetStatus("下载已暂停，可在下载页继续。");
        }
        catch (Exception ex)
        {
            AppLog.Write("资源下载失败", ex);
            if (!exiting) SetStatus($"下载中断：{ex.Message}。可在下载页继续。");
        }
        finally
        {
            if (activeDownloads.Remove(id, out var active)) active.Cancellation.Dispose();
            if (!exiting)
            {
                try { RefreshDownloadsView(); }
                catch (Exception ex) { AppLog.Write("刷新下载列表失败", ex); }
            }
        }
    }

    private void UpdateDownloadProgressSafely(DownloadJob job)
    {
        if (exiting || removingDownloads.Contains(job.Id)) return;
        try
        {
            var index = Downloads.ToList().FindIndex(row => row.Job.Id == job.Id);
            var peerName = index >= 0
                ? Downloads[index].PeerName
                : store.GetPeer(job.PeerId)?.Nickname ?? "未知设备";
            var row = new DownloadRow(job, job.ResourceName, peerName, job.Status,
                $"{SizeText(job.DownloadedBytes)} / {SizeText(job.TotalBytes)}", job.TargetPath, job.Error ?? "");
            if (index >= 0) Downloads[index] = row;
            else Downloads.Insert(0, row);
            UpdateActions();
        }
        catch (Exception ex)
        {
            AppLog.Write("更新下载进度失败", ex);
            SetStatus("下载仍在后台进行，但进度显示暂时无法更新。");
        }
    }

    private void ResumeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is not DownloadRow row || row.Job.Status == "已完成") return;
        QueueDownload(row.Job.Id);
    }

    private async void RemoveDownload_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is not DownloadRow row || removingDownloads.Contains(row.Job.Id)) return;
        var active = activeDownloads.GetValueOrDefault(row.Job.Id);
        var completed = row.Job.Status == "已完成";
        var message = completed
            ? $"从下载列表移除“{row.Name}”？\n\n已经下载的文件会保留在原位置。"
            : active is not null
                ? $"暂停并移除“{row.Name}”？\n\n已下载的临时内容会一起删除。"
                : $"移除“{row.Name}”？\n\n已下载的临时内容会一起删除。";
        if (System.Windows.MessageBox.Show(message, "确认移除下载任务", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        removingDownloads.Add(row.Job.Id);
        UpdateActions();
        try
        {
            if (active is not null)
            {
                active.Cancellation.Cancel();
                await active.Task;
            }
            var latest = store.GetDownload(row.Job.Id) ?? row.Job;
            downloader.RemoveJob(row.Job.Id, latest.Status != "已完成");
            SetStatus(latest.Status == "已完成" ? "下载记录已移除，本地文件已保留。" : "下载任务和临时内容已移除。");
        }
        catch (Exception ex) { ShowError("移除下载任务失败", ex); }
        finally
        {
            removingDownloads.Remove(row.Job.Id);
            RefreshDownloadsView();
        }
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is not DownloadRow row) return;
        var path = Directory.Exists(row.Job.TargetPath) ? row.Job.TargetPath : Path.GetDirectoryName(row.Job.TargetPath);
        if (path is null || !Directory.Exists(path)) { SetStatus("保存目录不存在。"); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择头像", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var scale = Math.Min(1.0, 128.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using var output = new MemoryStream();
            encoder.Save(output);
            pendingAvatar = output.ToArray();
            if (pendingAvatar.Length > 512 * 1024) throw new InvalidDataException("头像压缩后仍超过 512 KB，请换一张图片。");
            AvatarPreview.Source = AvatarImage(pendingAvatar);
        }
        catch (Exception ex) { ShowError("读取头像失败", ex); }
    }

    private void ClearAvatar_Click(object sender, RoutedEventArgs e) { pendingAvatar = null; AvatarPreview.Source = null; }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdate)
        {
            if (manual) SetStatus("正在检查或下载更新，请稍候。");
            return;
        }
        checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        if (!IsPackaged)
        {
            UpdateStatusText.Text = $"当前版本 v{AppVersion}；请使用打包后的 EXE 检查更新。";
            checkingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
            return;
        }
        UpdateStatusText.Text = "正在检查 GitHub 和已连接设备上的最新版本…";
        try
        {
            var localTask = FindLocalUpdatesAsync(updateCancellation.Token);
            AppUpdate? github = null;
            Exception? githubError = null;
            try { github = await updateClient.GetLatestAsync(updateCancellation.Token); }
            catch (OperationCanceledException) when (exiting) { throw; }
            catch (Exception ex) { githubError = ex; }
            var localUpdates = await localTask;
            var local = localUpdates.FirstOrDefault();
            if (github is null && local is null)
            {
                if (githubError is not null) throw new InvalidOperationException(
                    "GitHub 检查失败，已连接设备也没有可用的本地更新源。", githubError);
                UpdateStatusText.Text = "尚未发布可供更新的版本。";
                if (manual) SetStatus("GitHub 和已连接设备均未发布安装包。");
                return;
            }
            var current = UpdateClient.ParseVersion(AppVersion);
            if (githubError is not null && (local is null || local.Version <= current))
                throw new InvalidOperationException("GitHub 检查失败，已连接设备没有发布更高版本。", githubError);
            var latestVersion = github?.Version;
            if (local is not null && (latestVersion is null || local.Version > latestVersion))
                latestVersion = local.Version;
            if (latestVersion is null || latestVersion <= current)
            {
                UpdateStatusText.Text = $"当前已是最新版本 v{AppVersion}";
                if (manual) SetStatus("当前已是最新版本。");
                return;
            }

            var localSource = localUpdates.FirstOrDefault(item => item.Version == latestVersion &&
                (github?.Version != latestVersion ||
                 string.Equals(item.Package.Sha256, github.Sha256, StringComparison.OrdinalIgnoreCase)));
            var githubSource = github?.Version == latestVersion ? github : null;
            var sourceName = localSource is null ? "GitHub" : $"“{localSource.Peer.Nickname}”的本地源";
            if (localSource is not null && githubSource is null)
            {
                var acceptUnconfirmedLocal = manual && System.Windows.MessageBox.Show(
                    $"{sourceName}发布了新版 v{latestVersion}，但 GitHub 当前无法确认这个版本的官方 SHA-256。\n\n仅当你信任该设备和它发布的程序时继续。是否下载？",
                    "确认本地更新源", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!acceptUnconfirmedLocal && github is not null && github.Version > current)
                {
                    latestVersion = github.Version;
                    githubSource = github;
                    localSource = localUpdates.FirstOrDefault(item => item.Version == github.Version &&
                        string.Equals(item.Package.Sha256, github.Sha256, StringComparison.OrdinalIgnoreCase));
                    sourceName = localSource is null ? "GitHub" : $"“{localSource.Peer.Nickname}”的本地源";
                }
                else if (!acceptUnconfirmedLocal)
                {
                    UpdateStatusText.Text = manual
                        ? $"已取消从{sourceName}下载 v{latestVersion}。"
                        : $"{sourceName}发布了 v{latestVersion}；请手动检查并确认此来源。";
                    return;
                }
            }
            UpdateStatusText.Text = $"发现 v{latestVersion}，正从{sourceName}下载并校验…";
            var progress = new Progress<long>(bytes => UpdateStatusText.Text =
                $"正从{sourceName}下载 v{latestVersion} · {bytes * 100d / (localSource?.Package.Size ?? githubSource!.Size):0}%");
            string package;
            string packageHash;
            if (localSource is not null)
            {
                try
                {
                    package = await updateClient.DownloadAsync(localSource.Package, localSource.Peer, client,
                        progress, updateCancellation.Token);
                    packageHash = localSource.Package.Sha256;
                }
                catch (Exception ex) when (githubSource is not null && ex is not OperationCanceledException)
                {
                    sourceName = "GitHub（本地源不可用，已回退）";
                    UpdateStatusText.Text = $"本地源下载失败，正从 GitHub 下载 v{latestVersion}…";
                    progress = new Progress<long>(bytes => UpdateStatusText.Text =
                        $"正从 GitHub 下载 v{latestVersion} · {bytes * 100d / githubSource.Size:0}%");
                    package = await updateClient.DownloadAsync(githubSource, progress, updateCancellation.Token);
                    packageHash = githubSource.Sha256;
                }
            }
            else
            {
                package = await updateClient.DownloadAsync(githubSource!, progress, updateCancellation.Token);
                packageHash = githubSource!.Sha256;
            }
            stagedUpdate = new StagedUpdate(package, packageHash, latestVersion.ToString());
            UpdateStatusText.Text = $"v{latestVersion} 已从{sourceName}下载并校验，退出后安装。";
            if (manual && System.Windows.MessageBox.Show(
                    $"新版 v{latestVersion} 已准备好（来源：{sourceName}）。\n\n现在退出共享、安装更新并重新启动吗？",
                    "资源管理器更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                restartAfterUpdate = true;
                await ExitAsync();
            }
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"检查更新失败：{ex.Message}";
            if (manual) ShowError("检查更新失败", ex);
        }
        finally
        {
            checkingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task<IReadOnlyList<LocalUpdateCandidate>> FindLocalUpdatesAsync(CancellationToken cancellationToken)
    {
        var tasks = store.GetPeers().Select(async peer =>
        {
            try
            {
                var package = await client.GetSharedUpdateAsync(peer, cancellationToken);
                if (package is null) return null;
                var version = UpdateClient.ParseVersion(package.Version);
                if (string.IsNullOrWhiteSpace(package.ResourceId) || package.ResourceId.Length > 100 ||
                    package.Size is <= 0 or > UpdateClient.MaxPackageBytes || package.Sha256.Length != 64 ||
                    package.Sha256.Any(character => !Uri.IsHexDigit(character))) return null;
                return new LocalUpdateCandidate(peer, package, version);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return null; }
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.OfType<LocalUpdateCandidate>().OrderByDescending(item => item.Version).ToArray();
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var previousAutoStart = AutoStartManager.IsEnabled();
        try
        {
            if (!int.TryParse(ListenPortBox.Text, out var port)) throw new ArgumentException("监听端口无效。");
            var previousPort = store.GetSettings().ListenPort;
            AutoStartManager.SetEnabled(AutoStartBox.IsChecked == true);
            store.SaveSettings(NicknameBox.Text, pendingAvatar, port, CloseToTrayBox.IsChecked == true, AutoUpdateBox.IsChecked == true);
            UpdateIdentity();
            if (!node.IsRunning || previousPort != port)
            {
                await discovery.StopAsync();
                await node.StopAsync();
                await node.StartAsync(port);
            }
            var discoveryReady = await TryStartDiscoveryAsync();
            if (previousPort != port) await MaintainMappingsAsync();
            SetStatus(discoveryReady
                ? "设置已保存。设备资料会在下次状态检查时同步给其他电脑。"
                : "设置已保存，但局域网自动发现不可用；仍可使用 IP 地址手动连接。");
        }
        catch (Exception ex)
        {
            try { AutoStartManager.SetEnabled(previousAutoStart); } catch { }
            AutoStartBox.IsChecked = previousAutoStart;
            ShowError("保存设置失败", ex);
        }
    }

    private void ShowError(string title, Exception ex)
    {
        SetStatus($"{title}：{ex.Message}");
        System.Windows.MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal void ShowWindow()
    {
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (store.GetSettings().CloseToTray)
        {
            Hide();
            tray.ShowBalloonTip(1500, "资源管理器仍在运行", "从托盘菜单选择“退出并停止共享”可完全退出。", WinForms.ToolTipIcon.Info);
        }
        else _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        timer.Stop();
        gatewayRefreshCancellation?.Cancel();
        updateCancellation.Cancel();
        downloadCancellation.Cancel();
        var downloads = activeDownloads.Values.Select(active => active.Task).ToArray();
        if (downloads.Length > 0)
        {
            try { await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
        try
        {
            await discovery.StopAsync();
            await node.DisposeAsync();
        }
        finally
        {
            gatewayRefreshCancellation?.Dispose();
            client.Dispose();
            upnpGatewayClient.Dispose();
            updateClient.Dispose();
            updateCancellation.Dispose();
            downloadCancellation.Dispose();
            tray.Visible = false;
            tray.Dispose();
            appIcon.Dispose();
            iconStream.Dispose();
            if (stagedUpdate is not null && Environment.ProcessPath is not null)
            {
                Process.Start(new ProcessStartInfo(stagedUpdate.PackagePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(stagedUpdate.PackagePath)!,
                    ArgumentList =
                    {
                        "--apply-update", Environment.ProcessPath, stagedUpdate.Sha256,
                        restartAfterUpdate ? "restart" : "no-restart",
                        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                });
            }
            Close();
            System.Windows.Application.Current.Shutdown();
        }
    }
}

internal sealed record StagedUpdate(string PackagePath, string Sha256, string Version);
internal sealed record LocalUpdateCandidate(PeerInfo Peer, SharedUpdatePackage Package, Version Version);

internal sealed class ActiveDownload(CancellationTokenSource cancellation)
{
    public CancellationTokenSource Cancellation { get; } = cancellation;
    public Task Task { get; set; } = Task.CompletedTask;
}

public sealed record PeerRow(PeerInfo Peer, string Status, ImageSource? Avatar, string Source, string Note)
{
    public string Nickname => Peer.Nickname;
    public string Ip => Peer.Ip;
    public string Address => $"{Peer.Ip}:{Peer.Port}";
    public string NoteText => string.IsNullOrEmpty(Note) ? "" : $"备注：{Note}";
}

public sealed record GatewayRow(GatewayInfo Gateway, string Name, string WanIp, string PortRange, string Status);

public sealed record ResourceRow(RemoteResource Resource, string Name, string Kind, string Mode, string Size, string Status);
public sealed record LocalResourceRow(LocalResource Resource, string Name, string Kind, string Mode, string Size, string Status, string Path);
public sealed record FavoriteRow(Favorite Favorite, string Name, string PeerName, string Kind, string Status);
public sealed record DownloadRow(DownloadJob Job, string Name, string PeerName, string Status, string Progress, string Target, string Error)
{
    public double Percent => Job.TotalBytes > 0 ? Math.Clamp(Job.DownloadedBytes * 100d / Job.TotalBytes, 0, 100) : Job.Status == "已完成" ? 100 : 0;
}
