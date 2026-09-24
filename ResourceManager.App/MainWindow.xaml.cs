using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ReminderService reminders;
    private readonly FeedbackStore feedbackStore = new();
    private readonly FeedbackSecretStore feedbackSecrets;
    private readonly FeedbackServerClient feedbackServerClient = new();
    private readonly GitHubFeedbackClient githubFeedbackClient = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer feedbackDraftTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly WinForms.NotifyIcon tray;
    private readonly MemoryStream iconStream;
    private readonly System.Drawing.Icon appIcon;
    private readonly Dictionary<string, string> peerStatus = [];
    private readonly Dictionary<string, IReadOnlyList<RemoteResource>> peerCatalogs = [];
    private readonly Dictionary<string, string[]> peerCapabilities = [];
    private readonly HashSet<string> openReminders = [];
    private readonly List<ReminderWindow> reminderWindows = [];
    private readonly Dictionary<string, ActiveDownload> activeDownloads = [];
    private readonly Dictionary<ScrollViewer, double> smoothScrollTargets = [];
    private readonly HashSet<string> removingDownloads = [];
    private byte[]? pendingAvatar;
    private bool refreshing;
    private bool exiting;
    private bool checkingUpdate;
    private bool startupUpdateCheckStarted;
    private bool restartAfterUpdate;
    private StagedUpdate? stagedUpdate;
    private readonly bool startedWithWindows;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly CancellationTokenSource downloadCancellation = new();
    private CancellationTokenSource? gatewayRefreshCancellation;
    private readonly CancellationTokenSource feedbackCancellation = new();
    private DateTimeOffset nextMappingMaintenance = DateTimeOffset.MinValue;
    private DateTimeOffset nextRouterInfoRefresh = DateTimeOffset.MinValue;
    private bool routerInfoRefreshing;
    private string? currentRouterWanIp;
    private FeedbackServerBinding? feedbackServerBinding;
    private GitHubSession? feedbackGitHubSession;
    private string? feedbackGitHubLogin;
    private bool feedbackInitialized;
    private bool feedbackBusy;
    private bool feedbackRefreshBusy;
    private bool smoothScrollRendering;
    private DateTimeOffset lastFeedbackSubmitUtc = DateTimeOffset.MinValue;
    private static string GitHubClientId => Environment.GetEnvironmentVariable("ResourceManager_GitHubClientId") ??
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitHubClientId")?.Value ?? "";

    public ObservableCollection<PeerRow> Peers { get; } = [];
    public ObservableCollection<ResourceRow> RemoteResources { get; } = [];
    public ObservableCollection<LocalResourceRow> LocalResources { get; } = [];
    public ObservableCollection<FavoriteRow> Favorites { get; } = [];
    public ObservableCollection<DownloadRow> Downloads { get; } = [];
    public ObservableCollection<GatewayRow> Gateways { get; } = [];
    public ObservableCollection<FeedbackAttachmentRow> FeedbackAttachments { get; } = [];
    public ObservableCollection<FeedbackHistoryRow> FeedbackHistory { get; } = [];
    public ObservableCollection<FeedbackTargetOption> FeedbackTargets { get; } = [];

    public MainWindow(bool startedWithWindows = false)
    {
        this.startedWithWindows = startedWithWindows;
        feedbackSecrets = new FeedbackSecretStore(NodeDefaults.DataDirectory);
        InitializeComponent();
        PreviewMouseWheel += MainWindow_PreviewMouseWheel;
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
        client = new PeerClient(store, supportsReminders: true);
        reminders = new ReminderService(store, client);
        node = new PeerNode(store, discovery, mappingManager, reminders);
        gatewayDiscovery = new GatewayDiscoveryService(store, client);
        downloader = new DownloadManager(store, client);
        catalog = new ResourceCatalog(store);
        updateClient = new UpdateClient(store.DataDirectory);
        updateClient.CleanupOldDownloads();
        reminders.ReminderReceived += delivery => Dispatcher.BeginInvoke(() => ShowReminder(delivery));
        var settings = store.GetSettings();
        NicknameBox.Text = settings.Profile.Nickname;
        ListenPortBox.Text = settings.ListenPort.ToString();
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        AutoStartBox.IsChecked = AutoStartManager.IsEnabled();
        DeviceIdText.Text = settings.Profile.DeviceId;
        UpdateStatusText.Text = $"当前版本 v{AppVersion}";
        SetSidebarUpdateState(UpdateIndicatorState.Checking);
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
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowWindow);
        timer.Tick += async (_, _) => await TimerTickAsync();
        feedbackDraftTimer.Tick += (_, _) =>
        {
            feedbackDraftTimer.Stop();
            SaveFeedbackDraft();
        };
        RefreshLocalView();
        RefreshPeersView();
        RefreshGatewaysView();
        RefreshFavoritesView();
        RefreshDownloadsView();
        InitializeFeedback();
        UpdatePageHeader();
    }

    private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var viewer = FindScrollableViewer(source, e.Delta);
        if (viewer is null) return;
        var currentTarget = smoothScrollTargets.TryGetValue(viewer, out var target) ? target : viewer.VerticalOffset;
        smoothScrollTargets[viewer] = Math.Clamp(currentTarget - e.Delta * 0.68, 0, viewer.ScrollableHeight);
        if (!smoothScrollRendering)
        {
            CompositionTarget.Rendering += SmoothScroll_Rendering;
            smoothScrollRendering = true;
        }
        e.Handled = true;
    }

    private ScrollViewer? FindScrollableViewer(DependencyObject source, int delta)
    {
        for (DependencyObject? current = source; current is not null; current = ParentOf(current))
        {
            if (current is not ScrollViewer viewer || viewer.ScrollableHeight <= 0.5) continue;
            var target = smoothScrollTargets.TryGetValue(viewer, out var pending) ? pending : viewer.VerticalOffset;
            if (delta > 0 && target > 0.5 || delta < 0 && target < viewer.ScrollableHeight - 0.5) return viewer;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject value)
    {
        if (value is FrameworkContentElement content) return content.Parent;
        if (value is Visual or System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(value);
        return LogicalTreeHelper.GetParent(value);
    }

    private void SmoothScroll_Rendering(object? sender, EventArgs e)
    {
        foreach (var item in smoothScrollTargets.ToArray())
        {
            if (!item.Key.IsVisible)
            {
                smoothScrollTargets.Remove(item.Key);
                continue;
            }
            var target = Math.Clamp(item.Value, 0, item.Key.ScrollableHeight);
            var remaining = target - item.Key.VerticalOffset;
            if (Math.Abs(remaining) < 0.45)
            {
                item.Key.ScrollToVerticalOffset(target);
                smoothScrollTargets.Remove(item.Key);
            }
            else item.Key.ScrollToVerticalOffset(item.Key.VerticalOffset + remaining * 0.24);
        }
        if (smoothScrollTargets.Count != 0) return;
        CompositionTarget.Rendering -= SmoothScroll_Rendering;
        smoothScrollRendering = false;
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
        StartStartupUpdateCheck();
        await RefreshAllAsync();
        await RefreshRouterInfoAsync();
        await MaintainMappingsAsync();
        try { AutoStartManager.RefreshEnabledPath(); } catch { }
    }

    private void StartStartupUpdateCheck()
    {
        if (startupUpdateCheckStarted) return;
        startupUpdateCheckStarted = true;
        _ = CheckForUpdatesAsync(manual: false, downloadUpdate: false);
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
            RemoteResources.Add(new ResourceRow(resource, resource.Name, KindText(resource.Kind), ModeText(resource.Mode), SizeText(resource.Size), resource.Available ? "可下载" : "原文件不可用", resource.Note));
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
            LocalResources.Add(new LocalResourceRow(item, item.Name, KindText(item.Kind), ModeText(item.Mode), SizeText(info.Size), info.Available ? "可用" : "原文件不可用", item.SourcePath, item.Note));
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
            Favorites.Add(new FavoriteRow(item, item.Name, peer?.Nickname ?? "未知设备", KindText(item.Kind), status, remote?.Note ?? ""));
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
            var note = peerCatalogs.GetValueOrDefault(item.PeerId)?.FirstOrDefault(r => r.Id == item.ResourceId)?.Note ?? "";
            Downloads.Add(new DownloadRow(item, item.ResourceName, name, item.Status,
                $"{SizeText(item.DownloadedBytes)} / {SizeText(item.TotalBytes)}", item.TargetPath, item.Error ?? "", note));
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
                        var catalog = await client.GetCatalogAsync(candidate);
                        peerCatalogs[peer.DeviceId] = catalog.Resources;
                        peerCapabilities[peer.DeviceId] = catalog.Hello.Capabilities ?? [];
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
                peerCapabilities.Remove(peer.DeviceId);
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
            ("反馈", "向维护者提交问题、建议和使用体验"),
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
        if (Tabs.SelectedIndex == 4) await RefreshFeedbackTargetHealthAsync();
        if (Tabs.SelectedIndex == 5) await RefreshRouterInfoAsync();
    }

    private void PeersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSelectedResources();

    private void RemoteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void FavoritesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private void LocalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalGrid.SelectedItem is LocalResourceRow row)
        {
            if (LocalNoteBox is not null) LocalNoteBox.Text = row.Note;
        }
        else if (LocalNoteBox is not null) LocalNoteBox.Text = "";
        UpdateActions();
    }
    private void DownloadsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private void UpdateActions()
    {
        if (RemoteFavoriteButton is null || RemoteDownloadButton is null || FavoriteDownloadButton is null ||
            RemovePeerButton is null || RemoveResourceButton is null || RemoveFavoriteButton is null || PeerNoteButton is null ||
            SaveLocalNoteButton is null || ClearLocalNoteButton is null ||
            ResumeDownloadButton is null || OpenDownloadFolderButton is null || RemoveDownloadButton is null) return;
        var remote = RemoteGrid.SelectedItem as ResourceRow;
        var peer = PeersGrid.SelectedItem as PeerRow;
        RemoteFavoriteButton.IsEnabled = remote is not null && peer?.Status == "在线";
        RemoteDownloadButton.IsEnabled = remote?.Resource.Available == true && peer?.Status == "在线";
        FavoriteDownloadButton.IsEnabled = (FavoritesGrid.SelectedItem as FavoriteRow)?.Status == "可下载";
        RemovePeerButton.IsEnabled = peer is not null;
        PeerNoteButton.IsEnabled = peer is not null;
        RemoveResourceButton.IsEnabled = LocalGrid.SelectedItem is LocalResourceRow;
        SaveLocalNoteButton.IsEnabled = LocalGrid.SelectedItem is LocalResourceRow;
        ClearLocalNoteButton.IsEnabled = LocalGrid.SelectedItem is LocalResourceRow localRow && !string.IsNullOrEmpty(localRow.Note);
        RemoveFavoriteButton.IsEnabled = FavoritesGrid.SelectedItem is FavoriteRow;
        var download = DownloadsGrid.SelectedItem as DownloadRow;
        var removing = download is not null && removingDownloads.Contains(download.Job.Id);
        var active = download is not null && activeDownloads.ContainsKey(download.Job.Id);
        ResumeDownloadButton.IsEnabled = download is not null && download.Job.Status != "已完成" && !active && !removing;
        OpenDownloadFolderButton.IsEnabled = download is not null && !removing;
        RemoveDownloadButton.IsEnabled = download is not null && !removing;
        RemoveDownloadButton.Content = active ? "暂停并移除" : "移除任务";
    }

    private async void RefreshPeers_Click(object sender, RoutedEventArgs e)
    {
        RefreshPeersButton.IsEnabled = false;
        RefreshPeersButton.Content = "正在查找…";
        SetStatus("正在查找同一局域网内的设备…");
        try
        {
            var found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(2), updateCancellation.Token);
            if (found.Count == 0)
            {
                await RefreshAllAsync();
                SetStatus("没有发现新的局域网设备；已刷新现有设备状态。");
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
                RefreshPeersButton.Content = "刷新";
                RefreshPeersButton.IsEnabled = true;
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
            peerCapabilities.Remove(deviceId);
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

    private void RemovePeer_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow row) return;
        if (System.Windows.MessageBox.Show($"移除 {row.Nickname}？该设备的收藏和本机备注也会删除。", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        store.RemovePeer(row.Peer.DeviceId);
        peerStatus.Remove(row.Peer.DeviceId);
        peerCatalogs.Remove(row.Peer.DeviceId);
        peerCapabilities.Remove(row.Peer.DeviceId);
        RefreshPeersView(); RefreshFavoritesView();
        SetStatus("设备已移除，本机备注一并删除。");
    }

    private void EditPeerNote_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerRow row) { SetStatus("请先选中设备。"); return; }
        var dialog = new DeviceNoteDialog(row.Nickname, row.Note) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true) return;
        try
        {
            store.SavePeerNote(row.Peer.DeviceId, dialog.Note);
            RefreshPeersView();
            SetStatus(dialog.Note.Trim().Length == 0 ? "已清空本机备注。" : "备注已保存，仅本机可见。");
        }
        catch (Exception ex) { ShowError("保存备注失败", ex); }
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

    private void SaveLocalNote_Click(object sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not LocalResourceRow row) { SetStatus("请先选中已发布的资源。"); return; }
        try
        {
            var text = LocalNoteBox.Text;
            store.SetResourceNote(row.Resource.Id, text);
            RefreshLocalView();
            LocalNoteBox.Text = "";
            SetStatus(string.IsNullOrWhiteSpace(text) ? "已清空资源备注。" : "资源备注已保存，对方刷新目录后可见。");
        }
        catch (Exception ex) { ShowError("保存资源备注失败", ex); }
    }

    private void ClearLocalNote_Click(object sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not LocalResourceRow row) { SetStatus("请先选中已发布的资源。"); return; }
        try
        {
            store.SetResourceNote(row.Resource.Id, "");
            LocalNoteBox.Text = "";
            RefreshLocalView();
            SetStatus("已清空资源备注。");
        }
        catch (Exception ex) { ShowError("清空资源备注失败", ex); }
    }

    private void SendReminderRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LocalResourceRow row) return;
        if (row.Status != "可用") { SetStatus("资源原文件当前不可用，无法发送提醒。"); return; }
        var notes = store.GetPeerNotes();
        var targets = store.GetPeers().Select(peer => new ReminderTarget(peer,
                peerStatus.GetValueOrDefault(peer.DeviceId, "未检查"),
                peerCapabilities.GetValueOrDefault(peer.DeviceId, [])
                    .Contains(NodeDefaults.ReminderCapability, StringComparer.Ordinal),
                notes.GetValueOrDefault(peer.DeviceId, "")))
            .ToArray();
        if (targets.Length == 0) { SetStatus("本机还没有设备记录，请先在设备页连接设备。"); return; }
        var dialog = new SendReminderDialog($"{row.Name}（{row.Kind} · {row.Size}）", targets) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true || dialog.SelectedPeers.Count == 0) return;
        SendReminderAsync(row.Resource, dialog.SelectedPeers);
    }

    private async void SendReminderAsync(LocalResource resource, IReadOnlyList<PeerInfo> targets)
    {
        var settings = store.GetSettings();
        var sent = 0;
        var failures = new List<string>();
        foreach (var peer in targets)
        {
            try
            {
                var request = new ResourceReminderRequest(NodeDefaults.ReminderCapability, Guid.NewGuid().ToString("N"),
                    settings.Profile.DeviceId, resource.Id, resource.Name, resource.Kind, resource.Note, DateTimeOffset.UtcNow);
                var receipt = await client.SendReminderAsync(peer, request);
                if (receipt.Accepted) sent++;
                else failures.Add($"{peer.Nickname}：{receipt.Reason}");
            }
            catch (Exception ex) { failures.Add($"{peer.Nickname}：{ex.Message}"); }
        }
        if (failures.Count == 0) SetStatus($"提醒已送达 {sent} 台设备。");
        else System.Windows.MessageBox.Show(
            $"已送达 {sent} 台设备，{failures.Count} 台失败：\n\n{string.Join("\n", failures)}",
            "发送提醒结果", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowReminder(ReminderDelivery delivery)
    {
        if (exiting) return;
        var key = delivery.Sender.DeviceId + "\0" + delivery.Resource.Id;
        if (!openReminders.Add(key)) return;
        var window = new ReminderWindow(delivery.Sender, delivery.Resource, delivery.SentUtc);
        reminderWindows.Add(window);
        window.Loaded += (_, _) => PositionReminderWindows();
        window.Closed += (_, _) =>
        {
            openReminders.Remove(key);
            reminderWindows.Remove(window);
            PositionReminderWindows();
        };
        window.DownloadRequested += (_, _) => { window.Close(); BeginDownload(delivery.Sender, delivery.Resource); };
        window.MuteRequested += (_, _) =>
        {
            reminders.Mute(delivery.Sender.DeviceId, TimeSpan.FromHours(1));
            SetStatus($"已静音 {delivery.Sender.Nickname} 的提醒 1 小时。");
        };
        window.Show();
    }

    private void PositionReminderWindows()
    {
        const double edgeMargin = 16;
        const double gap = 10;
        var workArea = SystemParameters.WorkArea;
        var bottom = workArea.Bottom - edgeMargin;
        foreach (var window in reminderWindows.Where(item => item.IsLoaded).Reverse())
        {
            window.Left = workArea.Right - window.ActualWidth - edgeMargin;
            window.Top = Math.Max(workArea.Top + edgeMargin, bottom - window.ActualHeight);
            bottom = window.Top - gap;
        }
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
            var note = peerCatalogs.GetValueOrDefault(job.PeerId)?.FirstOrDefault(r => r.Id == job.ResourceId)?.Note ?? "";
            var row = new DownloadRow(job, job.ResourceName, peerName, job.Status,
                $"{SizeText(job.DownloadedBytes)} / {SizeText(job.TotalBytes)}", job.TargetPath, job.Error ?? "", note);
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

    private async void SidebarUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (stagedUpdate is not null)
        {
            if (System.Windows.MessageBox.Show(
                    $"新版 v{stagedUpdate.Version} 已下载并校验完成。\n\n现在退出共享、安装更新并重新启动吗？",
                    "资源管理器更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                restartAfterUpdate = true;
                await ExitAsync();
            }
            return;
        }
        await CheckForUpdatesAsync(true);
    }

    private async Task CheckForUpdatesAsync(bool manual, bool downloadUpdate = true)
    {
        if (checkingUpdate)
        {
            if (manual) SetStatus("正在检查或下载更新，请稍候。");
            return;
        }
        checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        SetSidebarUpdateState(UpdateIndicatorState.Checking);
        if (!IsPackaged)
        {
            UpdateStatusText.Text = $"当前版本 v{AppVersion}；请使用打包后的 EXE 检查更新。";
            SetSidebarUpdateState(UpdateIndicatorState.DevelopmentBuild);
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
                SetSidebarUpdateState(UpdateIndicatorState.Latest);
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
                SetSidebarUpdateState(UpdateIndicatorState.Latest);
                if (manual) SetStatus("当前已是最新版本。");
                return;
            }

            if (!downloadUpdate)
            {
                UpdateStatusText.Text = $"发现新版本 v{latestVersion}；点击左下角更新提示即可下载。";
                SetSidebarUpdateState(UpdateIndicatorState.Available, latestVersion);
                ShowUpdateAvailableNotification(latestVersion);
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
                    SetSidebarUpdateState(UpdateIndicatorState.Available, latestVersion);
                    return;
                }
            }
            UpdateStatusText.Text = $"发现 v{latestVersion}，正从{sourceName}下载并校验…";
            SetSidebarUpdateState(UpdateIndicatorState.Downloading, latestVersion);
            var progress = new Progress<long>(bytes => UpdateDownloadProgress(sourceName, latestVersion,
                bytes, localSource?.Package.Size ?? githubSource!.Size));
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
                    progress = new Progress<long>(bytes => UpdateDownloadProgress("GitHub", latestVersion,
                        bytes, githubSource.Size));
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
            SetSidebarUpdateState(UpdateIndicatorState.Ready, latestVersion);
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
            SetSidebarUpdateState(UpdateIndicatorState.Failed);
            if (manual) ShowError("检查更新失败", ex);
        }
        finally
        {
            checkingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void UpdateDownloadProgress(string sourceName, Version version, long bytes, long size)
    {
        var percent = Math.Clamp(bytes * 100d / size, 0, 100);
        UpdateStatusText.Text = $"正从{sourceName}下载 v{version} · {percent:0}%";
        SidebarUpdateTitleText.Text = $"正在下载 v{version}";
        SidebarUpdateDetailText.Text = $"{percent:0}% · 下载完成后自动校验";
    }

    private void ShowUpdateAvailableNotification(Version version)
    {
        if (exiting) return;
        tray.BalloonTipTitle = "ResourceManager 有新版本";
        tray.BalloonTipText = $"v{version} 已发布。打开程序后点击左下角“可更新”即可下载。";
        tray.BalloonTipIcon = WinForms.ToolTipIcon.Info;
        tray.ShowBalloonTip(5000);
    }

    private void SetSidebarUpdateState(UpdateIndicatorState state, Version? version = null)
    {
        var current = $"当前版本 v{AppVersion}";
        (SidebarUpdateTitleText.Text, SidebarUpdateDetailText.Text, SidebarUpdateButton.IsEnabled,
            SidebarUpdateButton.Background, SidebarUpdateButton.BorderBrush,
            SidebarUpdateIconBadge.Background, SidebarUpdateIconText.Foreground) = state switch
        {
            UpdateIndicatorState.Checking => ("正在检查更新", current, false,
                BrushFromRgb(244, 248, 254), BrushFromRgb(221, 232, 246), BrushFromRgb(229, 238, 249), BrushFromRgb(78, 111, 156)),
            UpdateIndicatorState.Latest => ("已是最新版本", $"{current} · 点击复查", true,
                BrushFromRgb(244, 248, 254), BrushFromRgb(221, 232, 246), BrushFromRgb(229, 238, 249), BrushFromRgb(78, 111, 156)),
            UpdateIndicatorState.Available => ($"可更新到 v{version}", "点击下载并安装", true,
                BrushFromRgb(234, 244, 255), BrushFromRgb(145, 184, 252), BrushFromRgb(40, 103, 217), System.Windows.Media.Brushes.White),
            UpdateIndicatorState.Downloading => ($"正在下载 v{version}", "连接更新源…", false,
                BrushFromRgb(234, 244, 255), BrushFromRgb(145, 184, 252), BrushFromRgb(40, 103, 217), System.Windows.Media.Brushes.White),
            UpdateIndicatorState.Ready => ($"v{version} 已准备好", "点击安装并重新启动", true,
                BrushFromRgb(233, 248, 241), BrushFromRgb(150, 213, 188), BrushFromRgb(24, 118, 90), System.Windows.Media.Brushes.White),
            UpdateIndicatorState.Failed => ("更新检查失败", "点击重试", true,
                BrushFromRgb(255, 246, 232), BrushFromRgb(238, 205, 150), BrushFromRgb(155, 106, 18), System.Windows.Media.Brushes.White),
            _ => ("开发版本", "请使用打包后的 EXE", false,
                BrushFromRgb(244, 248, 254), BrushFromRgb(221, 232, 246), BrushFromRgb(229, 238, 249), BrushFromRgb(78, 111, 156))
        };
        SidebarUpdateChevron.Visibility = SidebarUpdateButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        SidebarUpdateButton.ToolTip = SidebarUpdateTitleText.Text + " · " + SidebarUpdateDetailText.Text;
    }

    private static SolidColorBrush BrushFromRgb(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
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

    private void InitializeFeedback()
    {
        feedbackInitialized = false;
        FeedbackTargetBox.ItemsSource = FeedbackTargets;
        var secrets = feedbackSecrets.Load();
        feedbackGitHubSession = secrets.GitHub;
        feedbackGitHubLogin = secrets.GitHubLogin;
        feedbackServerBinding = secrets.Server;
        var draft = feedbackStore.GetDraft();
        if (DateTimeOffset.TryParse(feedbackStore.GetSetting("last_submit_utc"), out var previousSubmit))
            lastFeedbackSubmitUtc = previousSubmit;
        FeedbackCategoryBox.SelectedIndex = (int)draft.Category;
        FeedbackTitleBox.Text = draft.Title;
        FeedbackBodyBox.Text = draft.Body;
        FeedbackAttachments.Clear();
        foreach (var attachment in draft.Attachments.Where(item => File.Exists(item.StagedPath)))
            FeedbackAttachments.Add(new FeedbackAttachmentRow(attachment));
        FeedbackEnvironmentText.Text = EnvironmentSummary();
        RefreshFeedbackHistoryView();
        RefreshFeedbackTargets();
        UpdateFeedbackBindingStatus();
        feedbackInitialized = true;
        SaveFeedbackDraft();
    }

    private string EnvironmentSummary()
    {
        var username = store.GetSettings().Profile.Nickname;
        return $"提交时自动发送：用户名 {username} · ResourceManager {AppVersion} · {RuntimeInformation.OSDescription} · " +
               $"系统 {RuntimeInformation.OSArchitecture} / 进程 {RuntimeInformation.ProcessArchitecture}";
    }

    private void RefreshFeedbackTargets(bool preferServer = false)
    {
        var selected = (FeedbackTargetBox.SelectedItem as FeedbackTargetOption)?.Kind;
        FeedbackTargets.Clear();
        if (feedbackServerBinding is not null)
            FeedbackTargets.Add(new FeedbackTargetOption(FeedbackTargetKind.LanServer, $"局域网 · {feedbackServerBinding.Name}"));
        FeedbackTargets.Add(new FeedbackTargetOption(FeedbackTargetKind.Github, $"GitHub · {GitHubFeedbackClient.OfficialOwner}/{GitHubFeedbackClient.OfficialRepository}"));
        var wanted = preferServer && feedbackServerBinding is not null ? FeedbackTargetKind.LanServer : selected;
        if (wanted is null && Enum.TryParse<FeedbackTargetKind>(feedbackStore.GetSetting("active_target"), out var saved)) wanted = saved;
        FeedbackTargetBox.SelectedItem = FeedbackTargets.FirstOrDefault(item => item.Kind == wanted) ?? FeedbackTargets.FirstOrDefault();
        UpdateFeedbackTargetStatus();
    }

    private void UpdateFeedbackBindingStatus()
    {
        FeedbackGithubStatusText.Text = feedbackGitHubSession is null
            ? string.IsNullOrWhiteSpace(GitHubClientId) ? "此构建尚未配置 GitHub App Client ID" : "尚未登录"
            : $"已登录 {feedbackGitHubLogin ?? "GitHub 用户"}";
        FeedbackGithubLoginButton.IsEnabled = feedbackGitHubSession is null && !string.IsNullOrWhiteSpace(GitHubClientId);
        FeedbackGithubLogoutButton.IsEnabled = feedbackGitHubSession is not null;
        UpdateFeedbackTargetStatus();
    }

    private void UpdateFeedbackTargetStatus()
    {
        if (FeedbackTargetBox.SelectedItem is not FeedbackTargetOption target)
        {
            FeedbackGithubAuthorizationPanel.Visibility = Visibility.Collapsed;
            FeedbackTargetStatusText.Text = "点击刷新查找局域网服务端。";
            FeedbackSubmitButton.IsEnabled = false;
            return;
        }
        FeedbackGithubAuthorizationPanel.Visibility = target.Kind == FeedbackTargetKind.Github
            ? Visibility.Visible : Visibility.Collapsed;
        var hasAttachments = FeedbackAttachments.Count > 0;
        if (target.Kind == FeedbackTargetKind.LanServer)
        {
            FeedbackTargetStatusText.Text = feedbackServerBinding is null
                ? "服务端当前不可用，请刷新后重试。"
                : $"反馈将发送到 {feedbackServerBinding.Name}；正文和附件通过可信局域网 HTTP 传输。";
            FeedbackSubmitButton.IsEnabled = feedbackServerBinding is not null && !feedbackBusy;
        }
        else
        {
            FeedbackTargetStatusText.Text = hasAttachments
                ? "GitHub API 不支持附件。请移除附件或改用局域网服务端。"
                : feedbackGitHubSession is null
                    ? "请先使用你自己的 GitHub 账号登录。客户端不包含共享写入密钥。"
                    : $"将以 {feedbackGitHubLogin} 的身份提交到 {GitHubFeedbackClient.OfficialOwner}/{GitHubFeedbackClient.OfficialRepository}。";
            FeedbackSubmitButton.IsEnabled = feedbackGitHubSession is not null && !hasAttachments && !feedbackBusy;
        }
    }

    private void FeedbackTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeedbackTargetBox.SelectedItem is FeedbackTargetOption target)
            feedbackStore.SetSetting("active_target", target.Kind.ToString());
        UpdateFeedbackTargetStatus();
    }

    private async void FeedbackRefreshTargets_Click(object sender, RoutedEventArgs e) =>
        await RefreshFeedbackServerAsync();

    private void FeedbackDraftChanged(object sender, RoutedEventArgs e)
    {
        if (!feedbackInitialized) return;
        feedbackDraftTimer.Stop();
        feedbackDraftTimer.Start();
    }

    private void SaveFeedbackDraft()
    {
        if (!feedbackInitialized) return;
        var category = FeedbackCategoryBox.SelectedItem is ComboBoxItem item && Enum.TryParse<FeedbackCategory>(item.Tag?.ToString(), out var value)
            ? value : FeedbackCategory.Problem;
        feedbackStore.SaveDraft(new FeedbackDraft(category, FeedbackTitleBox.Text, FeedbackBodyBox.Text,
            FeedbackAttachments.Select(row => row.Attachment).ToArray(), DateTimeOffset.UtcNow));
    }

    private string FeedbackClientId()
    {
        var saved = feedbackStore.GetSetting("feedback_client_id");
        if (Guid.TryParse(saved, out var existing)) return existing.ToString("N");
        var created = Guid.NewGuid().ToString("N");
        feedbackStore.SetSetting("feedback_client_id", created);
        return created;
    }

    private async Task RefreshFeedbackServerAsync()
    {
        if (feedbackRefreshBusy) return;
        feedbackRefreshBusy = true;
        FeedbackRefreshTargetsButton.IsEnabled = false;
        FeedbackTargetStatusText.Text = "正在查找局域网服务端…";
        try
        {
            var found = await FindFeedbackServerAsync();
            if (found is null)
            {
                feedbackServerBinding = null;
                SaveFeedbackSecrets();
                RefreshFeedbackTargets();
                FeedbackTargetStatusText.Text = "未找到局域网服务端；请确认服务端已启动并允许专用网络访问。";
                return;
            }
            feedbackServerBinding = new FeedbackServerBinding(found.Value.Capabilities.ServerId,
                found.Value.Capabilities.Name, found.Value.BaseUrl, FeedbackClientId());
            SaveFeedbackSecrets();
            RefreshFeedbackTargets(preferServer: true);
            FeedbackTargetStatusText.Text = $"已发现 {found.Value.Capabilities.Name} · 服务端 v{found.Value.Capabilities.ServerVersion}";
        }
        catch (OperationCanceledException) when (feedbackCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            feedbackServerBinding = null;
            SaveFeedbackSecrets();
            RefreshFeedbackTargets();
            FeedbackTargetStatusText.Text = $"刷新服务端失败：{ex.Message}";
        }
        finally
        {
            feedbackRefreshBusy = false;
            FeedbackRefreshTargetsButton.IsEnabled = true;
        }
    }

    private async Task<(FeedbackServerCapabilities Capabilities, string BaseUrl)?> FindFeedbackServerAsync()
    {
        var candidates = new List<string>();
        if (feedbackServerBinding is not null) candidates.Add(feedbackServerBinding.BaseUrl);
        candidates.Add($"http://127.0.0.1:{FeedbackRules.DefaultApiPort}/");
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(feedbackCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
            try
            {
                var capabilities = await feedbackServerClient.GetCapabilitiesAsync(candidate, timeout.Token);
                if (capabilities.Protocol == FeedbackRules.Protocol) return (capabilities, candidate);
            }
            catch (OperationCanceledException) when (!feedbackCancellation.IsCancellationRequested) { }
            catch (Exception) { }
        }
        var discovered = await FeedbackServerDiscovery.DiscoverAsync(duration: TimeSpan.FromSeconds(1.4),
            cancellationToken: feedbackCancellation.Token);
        var server = discovered.FirstOrDefault();
        if (server is null) return null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(feedbackCancellation.Token))
        {
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
            var capabilities = await feedbackServerClient.GetCapabilitiesAsync(server.BaseUrl, timeout.Token);
            return capabilities.Protocol == FeedbackRules.Protocol ? (capabilities, server.BaseUrl) : null;
        }
    }

    private Task RefreshFeedbackTargetHealthAsync() => RefreshFeedbackServerAsync();

    private async void FeedbackGithubLogin_Click(object sender, RoutedEventArgs e)
    {
        FeedbackGithubLoginButton.IsEnabled = false;
        try
        {
            var flow = await githubFeedbackClient.StartDeviceFlowAsync(GitHubClientId, feedbackCancellation.Token);
            FeedbackGithubCodeText.Text = $"请在 GitHub 输入代码 {flow.UserCode}（已复制）";
            System.Windows.Clipboard.SetText(flow.UserCode);
            Process.Start(new ProcessStartInfo(flow.VerificationUri) { UseShellExecute = true });
            var expires = DateTimeOffset.UtcNow.AddSeconds(flow.ExpiresIn);
            var interval = TimeSpan.FromSeconds(flow.Interval);
            while (DateTimeOffset.UtcNow < expires)
            {
                await Task.Delay(interval, feedbackCancellation.Token);
                try
                {
                    var session = await githubFeedbackClient.PollDeviceFlowAsync(GitHubClientId, flow.DeviceCode, feedbackCancellation.Token);
                    if (session is null) continue;
                    var user = await githubFeedbackClient.GetUserAsync(session.AccessToken, feedbackCancellation.Token);
                    var repository = await githubFeedbackClient.GetRepositoryAsync(session.AccessToken,
                        GitHubFeedbackClient.OfficialOwner, GitHubFeedbackClient.OfficialRepository, feedbackCancellation.Token);
                    if (!repository.HasIssues) throw new InvalidOperationException("官方仓库没有启用 Issues。");
                    feedbackGitHubSession = session;
                    feedbackGitHubLogin = user.Login;
                    SaveFeedbackSecrets();
                    FeedbackGithubCodeText.Text = "GitHub 登录成功。";
                    UpdateFeedbackBindingStatus();
                    RefreshFeedbackTargets();
                    return;
                }
                catch (GitHubAuthorizationSlowDownException) { interval += TimeSpan.FromSeconds(5); }
            }
            throw new TimeoutException("GitHub 登录已超时，请重试。");
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { ShowError("GitHub 登录失败", ex); }
        finally { UpdateFeedbackBindingStatus(); }
    }

    private void FeedbackGithubLogout_Click(object sender, RoutedEventArgs e)
    {
        feedbackGitHubSession = null;
        feedbackGitHubLogin = null;
        SaveFeedbackSecrets();
        FeedbackGithubCodeText.Text = "";
        UpdateFeedbackBindingStatus();
    }

    private async Task<GitHubSession> GetValidGithubSessionAsync()
    {
        var session = feedbackGitHubSession ?? throw new InvalidOperationException("请先登录 GitHub。");
        if (session.ExpiresUtc is null || session.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return session;
        if (string.IsNullOrEmpty(session.RefreshToken)) throw new InvalidOperationException("GitHub 登录已过期，请重新登录。");
        session = await githubFeedbackClient.RefreshAsync(GitHubClientId, session.RefreshToken, feedbackCancellation.Token);
        feedbackGitHubSession = session;
        SaveFeedbackSecrets();
        return session;
    }

    private void SaveFeedbackSecrets() => feedbackSecrets.Save(feedbackGitHubSession, feedbackGitHubLogin, feedbackServerBinding);

    private void FeedbackAddAttachments_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "选择反馈附件",
            Filter = "支持的附件|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.txt;*.md;*.log;*.json;*.jsonc;*.csv;*.tsv;*.pdf;*.docx;*.xlsx;*.pptx;*.zip;*.gz;*.tgz"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var additions = dialog.FileNames.Select(path => new FileInfo(path)).ToArray();
            FeedbackRules.ValidateAttachments(FeedbackAttachments.Select(row => (row.FileName, row.Attachment.Size))
                .Concat(additions.Select(file => (file.Name, file.Length))));
            foreach (var file in additions)
            {
                var staged = Path.Combine(feedbackStore.DraftDirectory, Guid.NewGuid().ToString("N") + file.Extension.ToLowerInvariant());
                File.Copy(file.FullName, staged);
                FeedbackAttachments.Add(new FeedbackAttachmentRow(new FeedbackAttachmentDraft(file.Name, staged, file.Length)));
            }
            SaveFeedbackDraft();
            UpdateFeedbackTargetStatus();
        }
        catch (Exception ex) { ShowError("添加附件失败", ex); }
    }

    private void FeedbackRemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (FeedbackAttachmentList.SelectedItem is not FeedbackAttachmentRow row) return;
        FeedbackAttachments.Remove(row);
        try { File.Delete(row.Attachment.StagedPath); } catch { }
        SaveFeedbackDraft();
        UpdateFeedbackTargetStatus();
    }

    private async void FeedbackSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (feedbackBusy || FeedbackTargetBox.SelectedItem is not FeedbackTargetOption target) return;
        var category = FeedbackCategoryBox.SelectedItem is ComboBoxItem categoryItem &&
                       Enum.TryParse<FeedbackCategory>(categoryItem.Tag?.ToString(), out var parsed)
            ? parsed : FeedbackCategory.Problem;
        var now = DateTimeOffset.UtcNow;
        var submission = new FeedbackSubmission(Guid.NewGuid().ToString(), category, FeedbackTitleBox.Text,
            FeedbackBodyBox.Text, store.GetSettings().Profile.Nickname, AppVersion, RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(), now);
        try
        {
            FeedbackRules.ValidateSubmission(submission);
            FeedbackRules.ValidateAttachments(FeedbackAttachments.Select(row => (row.FileName, row.Attachment.Size)));
            if (target.Kind == FeedbackTargetKind.Github && FeedbackAttachments.Count > 0)
                throw new InvalidOperationException("GitHub 目标不支持附件，请先移除附件或改用局域网服务端。");
            if (now - lastFeedbackSubmitUtc < TimeSpan.FromMinutes(1))
                throw new InvalidOperationException("两次反馈提交至少间隔一分钟。");
        }
        catch (Exception ex) { ShowError("无法提交反馈", ex); return; }

        var targetName = target.Kind == FeedbackTargetKind.Github
            ? $"GitHub · {GitHubFeedbackClient.OfficialOwner}/{GitHubFeedbackClient.OfficialRepository}"
            : feedbackServerBinding?.Name ?? "局域网服务端";
        var history = new FeedbackHistoryItem(submission.ClientSubmissionId, target.Kind, targetName, category,
            submission.Title.Trim(), submission.Body.Trim(), string.Join("；", FeedbackAttachments.Select(row => row.FileName)),
            null, null, "Submitting", now, now, null);
        feedbackStore.SaveHistory(history);
        RefreshFeedbackHistoryView();
        feedbackBusy = true;
        FeedbackSubmitButton.IsEnabled = false;
        FeedbackSubmitStatusText.Text = "正在提交…";
        try
        {
            if (target.Kind == FeedbackTargetKind.LanServer)
            {
                var binding = feedbackServerBinding ?? throw new InvalidOperationException("请先绑定局域网服务端。");
                var receipt = await feedbackServerClient.SubmitAsync(binding, submission,
                    FeedbackAttachments.Select(row => row.Attachment).ToArray(), feedbackCancellation.Token);
                history = history with { RemoteIssueId = receipt.IssueId, Status = receipt.Status.ToString(), UpdatedUtc = receipt.UpdatedUtc };
            }
            else
            {
                var session = await GetValidGithubSessionAsync();
                var issue = await githubFeedbackClient.CreateIssueAsync(session.AccessToken, GitHubFeedbackClient.OfficialOwner,
                    GitHubFeedbackClient.OfficialRepository, submission, feedbackCancellation.Token);
                history = history with { RemoteIssueId = issue.Number.ToString(), RemoteUrl = issue.HtmlUrl,
                    Status = issue.State == "closed" ? "Completed" : "Open", UpdatedUtc = issue.UpdatedUtc };
            }
            feedbackStore.SaveHistory(history);
            lastFeedbackSubmitUtc = DateTimeOffset.UtcNow;
            feedbackStore.SetSetting("last_submit_utc", lastFeedbackSubmitUtc.ToString("O"));
            feedbackInitialized = false;
            FeedbackTitleBox.Clear();
            FeedbackBodyBox.Clear();
            FeedbackCategoryBox.SelectedIndex = 0;
            FeedbackAttachments.Clear();
            feedbackStore.ClearDraft();
            feedbackInitialized = true;
            SaveFeedbackDraft();
            FeedbackSubmitStatusText.Text = "反馈已提交。";
        }
        catch (Exception ex)
        {
            feedbackStore.SaveHistory(history with { Status = "Failed", UpdatedUtc = DateTimeOffset.UtcNow, Error = ex.Message });
            FeedbackSubmitStatusText.Text = $"提交失败：{ex.Message}；草稿已保留。";
        }
        finally
        {
            feedbackBusy = false;
            RefreshFeedbackHistoryView();
            UpdateFeedbackTargetStatus();
        }
    }

    private void RefreshFeedbackHistoryView(string? selectId = null)
    {
        selectId ??= (FeedbackHistoryList?.SelectedItem as FeedbackHistoryRow)?.Item.ClientSubmissionId;
        FeedbackHistory.Clear();
        foreach (var item in feedbackStore.GetHistory()) FeedbackHistory.Add(new FeedbackHistoryRow(item));
        if (FeedbackHistoryList is not null)
            FeedbackHistoryList.SelectedItem = FeedbackHistory.FirstOrDefault(row => row.Item.ClientSubmissionId == selectId);
        UpdateFeedbackHistoryActions();
    }

    private async void FeedbackRefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        if (feedbackBusy) return;
        feedbackBusy = true;
        try
        {
            foreach (var item in feedbackStore.GetHistory().Where(item =>
                         item.Status != "UserClosed" &&
                         (item.TargetKind == FeedbackTargetKind.LanServer || item.RemoteIssueId is not null)))
            {
                try
                {
                    if (item.TargetKind == FeedbackTargetKind.LanServer)
                    {
                        var binding = feedbackServerBinding;
                        if (binding is null || binding.Name != item.TargetName) continue;
                        var status = await feedbackServerClient.GetStatusAsync(binding, item.ClientSubmissionId, feedbackCancellation.Token);
                        feedbackStore.SaveHistory(item with { RemoteIssueId = status.IssueId,
                            Status = status.Status.ToString(), UpdatedUtc = status.UpdatedUtc, Error = null });
                    }
                    else if (int.TryParse(item.RemoteIssueId, out var number) && feedbackGitHubSession is not null)
                    {
                        var session = await GetValidGithubSessionAsync();
                        var issue = await githubFeedbackClient.GetIssueAsync(session.AccessToken, GitHubFeedbackClient.OfficialOwner,
                            GitHubFeedbackClient.OfficialRepository, number, feedbackCancellation.Token);
                        feedbackStore.SaveHistory(item with { Status = issue.State == "closed" ? "Completed" : "Open",
                            UpdatedUtc = issue.UpdatedUtc, Error = null });
                    }
                }
                catch (Exception ex) { feedbackStore.SaveHistory(item with { Error = $"刷新失败：{ex.Message}" }); }
            }
            RefreshFeedbackHistoryView();
        }
        finally { feedbackBusy = false; }
    }

    private void FeedbackHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFeedbackHistoryActions();

    private void UpdateFeedbackHistoryActions()
    {
        if (FeedbackCloseButton is null) return;
        var row = FeedbackHistoryList.SelectedItem as FeedbackHistoryRow;
        FeedbackOpenRemoteButton.IsEnabled = !string.IsNullOrEmpty(row?.Item.RemoteUrl);
        FeedbackCloseButton.IsEnabled = row?.CanClose == true;
        FeedbackDeleteHistoryButton.IsEnabled = row?.CanDelete == true;
    }

    private async void FeedbackClose_Click(object sender, RoutedEventArgs e)
    {
        if (FeedbackHistoryList.SelectedItem is not FeedbackHistoryRow row || !row.CanClose) return;
        if (System.Windows.MessageBox.Show("关闭后，后端会把反馈标记为“用户已关闭”；该操作不会物理删除反馈。", "关闭反馈",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var item = row.Item;
            if (item.TargetKind == FeedbackTargetKind.LanServer)
            {
                var binding = feedbackServerBinding ?? throw new InvalidOperationException("原局域网服务端尚未绑定。");
                if (binding.Name != item.TargetName) throw new InvalidOperationException("当前绑定的服务端与反馈目标不一致。");
                var result = await feedbackServerClient.CloseAsync(binding, item.ClientSubmissionId, feedbackCancellation.Token);
                feedbackStore.SaveHistory(item with { Status = result.Status.ToString(), UpdatedUtc = result.UpdatedUtc, Error = null });
            }
            else
            {
                if (!int.TryParse(item.RemoteIssueId, out var number)) throw new InvalidOperationException("GitHub Issue 编号无效。");
                var session = await GetValidGithubSessionAsync();
                var issue = await githubFeedbackClient.SetIssueStateAsync(session.AccessToken, GitHubFeedbackClient.OfficialOwner,
                    GitHubFeedbackClient.OfficialRepository, number, "closed", "not_planned", feedbackCancellation.Token);
                feedbackStore.SaveHistory(item with { Status = "UserClosed", UpdatedUtc = issue.UpdatedUtc, Error = null });
            }
            RefreshFeedbackHistoryView(item.ClientSubmissionId);
        }
        catch (Exception ex) { ShowError("关闭反馈失败", ex); }
    }

    private void FeedbackDeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (FeedbackHistoryList.SelectedItem is not FeedbackHistoryRow row || !row.CanDelete) return;
        try
        {
            feedbackStore.DeleteHistory(row.Item.ClientSubmissionId);
            RefreshFeedbackHistoryView();
        }
        catch (Exception ex) { ShowError("删除反馈记录失败", ex); }
    }

    private void FeedbackOpenRemote_Click(object sender, RoutedEventArgs e)
    {
        if ((FeedbackHistoryList.SelectedItem as FeedbackHistoryRow)?.Item.RemoteUrl is not { } url) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var previousAutoStart = AutoStartManager.IsEnabled();
        try
        {
            if (!int.TryParse(ListenPortBox.Text, out var port)) throw new ArgumentException("监听端口无效。");
            var previousPort = store.GetSettings().ListenPort;
            AutoStartManager.SetEnabled(AutoStartBox.IsChecked == true);
            store.SaveSettings(NicknameBox.Text, pendingAvatar, port, CloseToTrayBox.IsChecked == true, autoUpdate: true);
            UpdateIdentity();
            FeedbackEnvironmentText.Text = EnvironmentSummary();
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
        PreviewMouseWheel -= MainWindow_PreviewMouseWheel;
        if (smoothScrollRendering) CompositionTarget.Rendering -= SmoothScroll_Rendering;
        smoothScrollRendering = false;
        smoothScrollTargets.Clear();
        timer.Stop();
        gatewayRefreshCancellation?.Cancel();
        updateCancellation.Cancel();
        downloadCancellation.Cancel();
        feedbackCancellation.Cancel();
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
            feedbackServerClient.Dispose();
            githubFeedbackClient.Dispose();
            updateCancellation.Dispose();
            downloadCancellation.Dispose();
            feedbackCancellation.Dispose();
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
internal enum UpdateIndicatorState { Checking, Latest, Available, Downloading, Ready, Failed, DevelopmentBuild }

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
    public string Title => string.IsNullOrEmpty(Note) ? Nickname : Note;
    public string NicknamePrefix => string.IsNullOrEmpty(Note) ? "" : $"{Nickname}  ·  ";
}

public sealed record GatewayRow(GatewayInfo Gateway, string Name, string WanIp, string PortRange, string Status);

public sealed record ResourceRow(RemoteResource Resource, string Name, string Kind, string Mode, string Size, string Status, string Note)
{
    public string NoteText => string.IsNullOrEmpty(Note) ? "" : $"备注：{Note}";
}
public sealed record LocalResourceRow(LocalResource Resource, string Name, string Kind, string Mode, string Size, string Status, string Path, string Note)
{
    public string NoteText => string.IsNullOrEmpty(Note) ? "" : $"备注：{Note}";
    public bool CanRemind => Status == "可用";
}
public sealed record FavoriteRow(Favorite Favorite, string Name, string PeerName, string Kind, string Status, string Note)
{
    public string NoteText => string.IsNullOrEmpty(Note) ? "" : $"备注：{Note}";
}
public sealed record DownloadRow(DownloadJob Job, string Name, string PeerName, string Status, string Progress, string Target, string Error, string Note)
{
    public string NoteText => string.IsNullOrEmpty(Note) ? "" : $"备注：{Note}";
    public double Percent => Job.TotalBytes > 0 ? Math.Clamp(Job.DownloadedBytes * 100d / Job.TotalBytes, 0, 100) : Job.Status == "已完成" ? 100 : 0;
}

public sealed record FeedbackTargetOption(FeedbackTargetKind Kind, string Display);

public sealed record FeedbackAttachmentRow(FeedbackAttachmentDraft Attachment)
{
    public string FileName => Attachment.FileName;
    public string SizeText => Attachment.Size >= 1024 * 1024
        ? $"{Attachment.Size / (1024d * 1024):0.0} MB" : $"{Attachment.Size / 1024d:0.0} KB";
}

public sealed record FeedbackHistoryRow(FeedbackHistoryItem Item)
{
    public string Title => Item.Title;
    public string TargetName => Item.TargetName;
    public string CategoryKey => Item.Category.ToString();
    public string CategoryText => FeedbackRules.CategoryText(Item.Category);
    public string CreatedText => Item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Error => Item.Error ?? "";
    public string StatusKey => Item.Status;
    public string StatusText => Item.Status switch
    {
        "PendingReview" => "已提交",
        "Accepted" => "已接受",
        "Rejected" => "已拒绝",
        "Completed" => "已完成",
        "UserClosed" => "用户已关闭",
        "Open" => "处理中",
        "Submitting" => "提交中",
        "Failed" => "提交失败",
        _ => Item.Status
    };
    public bool CanClose => Item.RemoteIssueId is not null && Item.Status is not ("Completed" or "UserClosed" or "Failed" or "Submitting");
    public bool CanDelete => FeedbackRules.IsHistoryDeletable(Item.Status);
}
