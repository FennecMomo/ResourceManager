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
    private readonly PeerClient client;
    private readonly DownloadManager downloader;
    private readonly ResourceCatalog catalog;
    private readonly UpdateClient updateClient;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly WinForms.NotifyIcon tray;
    private readonly MemoryStream iconStream;
    private readonly System.Drawing.Icon appIcon;
    private readonly Dictionary<string, string> peerStatus = [];
    private readonly Dictionary<string, IReadOnlyList<RemoteResource>> peerCatalogs = [];
    private readonly HashSet<string> activeDownloads = [];
    private byte[]? pendingAvatar;
    private bool refreshing;
    private bool exiting;
    private bool checkingUpdate;
    private bool restartAfterUpdate;
    private StagedUpdate? stagedUpdate;
    private readonly bool startedWithWindows;
    private readonly CancellationTokenSource updateCancellation = new();

    public ObservableCollection<PeerRow> Peers { get; } = [];
    public ObservableCollection<ResourceRow> RemoteResources { get; } = [];
    public ObservableCollection<LocalResourceRow> LocalResources { get; } = [];
    public ObservableCollection<FavoriteRow> Favorites { get; } = [];
    public ObservableCollection<DownloadRow> Downloads { get; } = [];

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
        node = new PeerNode(store);
        client = new PeerClient(store);
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
        timer.Tick += async (_, _) => await RefreshAllAsync();
        RefreshLocalView();
        RefreshPeersView();
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
            SetStatus($"共享服务已启动，端口 {node.Port}。");
        }
        catch (Exception ex) { SetStatus($"监听失败：{ex.Message}。可在设置中更换端口后重试。"); }
        timer.Start();
        await RefreshAllAsync();
        try { AutoStartManager.RefreshEnabledPath(); } catch { }
        if (store.GetSettings().AutoUpdate && IsPackaged) _ = CheckForUpdatesAsync(false);
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
        foreach (var peer in store.GetPeers())
            Peers.Add(new PeerRow(peer, peerStatus.GetValueOrDefault(peer.DeviceId, "未检查"), AvatarImage(peer.Avatar)));
        PeersGrid.SelectedItem = Peers.FirstOrDefault(p => p.Peer.DeviceId == selected);
        RefreshSelectedResources();
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
                try
                {
                    peerCatalogs[peer.DeviceId] = await client.GetResourcesAsync(peer);
                    peerStatus[peer.DeviceId] = "在线";
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("另一台设备"))
                {
                    peerStatus[peer.DeviceId] = "设备已变更";
                    peerCatalogs.Remove(peer.DeviceId);
                }
                catch
                {
                    peerStatus[peer.DeviceId] = "离线";
                    peerCatalogs.Remove(peer.DeviceId);
                }
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
    }

    private void PeersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeersGrid.SelectedItem is PeerRow row)
        { PeerIpBox.Text = row.Ip; PeerPortBox.Text = row.Peer.Port.ToString(); }
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
            ResumeDownloadButton is null || OpenDownloadFolderButton is null) return;
        var remote = RemoteGrid.SelectedItem as ResourceRow;
        var peer = PeersGrid.SelectedItem as PeerRow;
        RemoteFavoriteButton.IsEnabled = remote is not null && peer?.Status == "在线";
        RemoteDownloadButton.IsEnabled = remote?.Resource.Available == true && peer?.Status == "在线";
        FavoriteDownloadButton.IsEnabled = (FavoritesGrid.SelectedItem as FavoriteRow)?.Status == "可下载";
        RemovePeerButton.IsEnabled = peer is not null;
        UpdateAddressButton.IsEnabled = peer is not null;
        RemoveResourceButton.IsEnabled = LocalGrid.SelectedItem is LocalResourceRow;
        RemoveFavoriteButton.IsEnabled = FavoritesGrid.SelectedItem is FavoriteRow;
        var download = DownloadsGrid.SelectedItem as DownloadRow;
        ResumeDownloadButton.IsEnabled = download is not null && download.Job.Status != "已完成" && !activeDownloads.Contains(download.Job.Id);
        OpenDownloadFolderButton.IsEnabled = download is not null;
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
        if (System.Windows.MessageBox.Show($"移除 {row.Nickname}？该设备的收藏也会删除。", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        store.RemovePeer(row.Peer.DeviceId);
        peerStatus.Remove(row.Peer.DeviceId);
        peerCatalogs.Remove(row.Peer.DeviceId);
        RefreshPeersView(); RefreshFavoritesView();
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
            _ = StartDownloadAsync(job.Id);
        }
        catch (Exception ex) { ShowError("创建下载失败", ex); }
    }

    private async Task StartDownloadAsync(string id)
    {
        if (!activeDownloads.Add(id)) return;
        UpdateActions();
        try
        {
            var progress = new Progress<DownloadJob>(_ => RefreshDownloadsView());
            await downloader.RunAsync(id, progress);
            SetStatus("下载完成。本地文件在发布者离线时仍可使用。");
        }
        catch (Exception ex) { SetStatus($"下载中断：{ex.Message}。可在下载页继续。"); }
        finally { activeDownloads.Remove(id); RefreshDownloadsView(); }
    }

    private void ResumeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is not DownloadRow row || row.Job.Status == "已完成") return;
        _ = StartDownloadAsync(row.Job.Id);
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
        UpdateStatusText.Text = "正在检查 GitHub 上的最新版本…";
        try
        {
            var latest = await updateClient.GetLatestAsync(updateCancellation.Token);
            if (latest is null)
            {
                UpdateStatusText.Text = "尚未发布可供更新的版本。";
                if (manual) SetStatus("GitHub 尚未发布安装包。");
                return;
            }
            var current = UpdateClient.ParseVersion(AppVersion);
            if (latest.Version <= current)
            {
                UpdateStatusText.Text = $"当前已是最新版本 v{AppVersion}";
                if (manual) SetStatus("当前已是最新版本。");
                return;
            }
            UpdateStatusText.Text = $"发现 v{latest.Version}，正在下载并校验…";
            var progress = new Progress<long>(bytes => UpdateStatusText.Text =
                $"正在下载 v{latest.Version} · {bytes * 100d / latest.Size:0}%");
            var package = await updateClient.DownloadAsync(latest, progress, updateCancellation.Token);
            stagedUpdate = new StagedUpdate(package, latest.Sha256, latest.Version.ToString());
            UpdateStatusText.Text = $"v{latest.Version} 已下载并校验，退出后安装。";
            if (manual && System.Windows.MessageBox.Show(
                    $"新版 v{latest.Version} 已准备好。\n\n现在退出共享、安装更新并重新启动吗？",
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
                await node.StopAsync();
                await node.StartAsync(port);
            }
            SetStatus("设置已保存。设备资料会在下次状态检查时同步给其他电脑。");
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
        updateCancellation.Cancel();
        try { await node.StopAsync(); }
        finally
        {
            client.Dispose();
            updateClient.Dispose();
            updateCancellation.Dispose();
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
                        restartAfterUpdate ? "restart" : "no-restart"
                    }
                });
            }
            Close();
            System.Windows.Application.Current.Shutdown();
        }
    }
}

internal sealed record StagedUpdate(string PackagePath, string Sha256, string Version);

public sealed record PeerRow(PeerInfo Peer, string Status, ImageSource? Avatar)
{
    public string Nickname => Peer.Nickname;
    public string Ip => Peer.Ip;
    public string Address => $"{Peer.Ip}:{Peer.Port}";
}

public sealed record ResourceRow(RemoteResource Resource, string Name, string Kind, string Mode, string Size, string Status);
public sealed record LocalResourceRow(LocalResource Resource, string Name, string Kind, string Mode, string Size, string Status, string Path);
public sealed record FavoriteRow(Favorite Favorite, string Name, string PeerName, string Kind, string Status);
public sealed record DownloadRow(DownloadJob Job, string Name, string PeerName, string Status, string Progress, string Target, string Error)
{
    public double Percent => Job.TotalBytes > 0 ? Math.Clamp(Job.DownloadedBytes * 100d / Job.TotalBytes, 0, 100) : Job.Status == "已完成" ? 100 : 0;
}
