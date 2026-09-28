using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace ResourceManager.App;

public partial class MainWindow
{
    public ObservableCollection<ServerTabRow> Servers { get; } = [];
    private WorkspaceClient? workspaceClient;
    private bool editingServer;
    private bool changingServer;
    private void InitializeServers()
    {
        RefreshUploads();
        workspaceClient = new WorkspaceClient(store, AppVersion);
        foreach (var binding in store.GetServerBindings()) Servers.Add(new(binding, store.GetSettings().Profile.DeviceId));
    }
    private void StartServers() { foreach (var row in Servers) StartServer(row); }
    private void StartServer(ServerTabRow row)
    {
        if (exiting || row.Worker is { IsCompleted: false }) return;
        row.Cancellation = new(); row.Worker = RunServerAsync(row, row.Cancellation.Token);
    }
    private async Task RunServerAsync(ServerTabRow row, CancellationToken token)
    {
        var retry = 1; var cursor = ""; string? profile = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var currentProfile = JsonSerializer.Serialize(store.GetSettings().Profile, WorkspaceProtocol.Json);
                    if (row.Session is null || currentProfile != profile)
                    {
                        await StopPublicationAsync(row);
                        row.Update("连接中", null);
                        row.Session = await workspaceClient!.JoinAsync(row.Binding, token); profile = currentProfile; cursor = "";
                        var capabilities = await workspaceClient.InspectAsync(row.Address, token);
                        row.ServerVersion = capabilities.Version;
                        row.SupportsResources = capabilities.Features?.Contains("published-resources-v1") == true;
                        row.SupportsStorage = capabilities.Features?.Contains("stored-resources-v1") == true;
                    }
                    var snapshot = await workspaceClient!.WatchAsync(row.Binding, row.Session, cursor, token);
                    token.ThrowIfCancellationRequested();
                    var save = cursor != snapshot.Cursor || row.Status != "在线";
                    cursor = snapshot.Cursor; retry = 1;
                    row.Update("在线", null, snapshot);
                    if (save) SaveServerState(row);
                    if (row.SupportsResources)
                    {
                        row.SetCatalogs(await workspaceClient!.CatalogsAsync(row.Binding, row.Session, token));
                        RefreshFavoritesView();
                        if (row.PublicationWorker is null)
                        {
                            row.PublicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                            row.PublicationWorker = new WorkspacePublisher(store).RunAsync(workspaceClient!, row.Binding, row.Session,
                                error => Dispatcher.BeginInvoke(() => { row.SyncError = error; row.Notify(); }), row.PublicationCancellation.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (WorkspaceException ex) when (ex.StopRetry)
                {
                    await StopPublicationAsync(row);
                    row.Session = null; row.Update("连接受限", ex.Message); SaveServerState(row); break;
                }
                catch (Exception ex)
                {
                    await StopPublicationAsync(row);
                    row.Session = null; cursor = ""; row.Update("离线 · 自动重连", ex.Message); SaveServerState(row);
                    await Task.Delay(TimeSpan.FromSeconds(retry), token); retry = Math.Min(retry * 2, 30);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await StopPublicationAsync(row); }
    }
    private static async Task StopPublicationAsync(ServerTabRow row)
    {
        row.PublicationCancellation?.Cancel();
        if (row.PublicationWorker is not null) await row.PublicationWorker;
        row.PublicationCancellation?.Dispose(); row.PublicationCancellation = null; row.PublicationWorker = null;
    }
    private void SaveServerState(ServerTabRow row)
    {
        try { store.SaveServerBinding(row.Binding); }
        catch (Exception ex) { AppLog.Write("服务器状态缓存保存失败", ex); }
    }
    private async Task StopServerAsync(ServerTabRow row)
    {
        row.Cancellation?.Cancel();
        if (row.Worker is not null) await row.Worker;
        if (row.Session is { } session)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await workspaceClient!.LeaveAsync(row.Binding, session, timeout.Token); } catch { }
        }
        row.Session = null; row.Cancellation?.Dispose(); row.Cancellation = null; row.Worker = null;
    }
    private async Task StopServersAsync()
    {
        await Task.WhenAll(Servers.Select(StopServerAsync)); workspaceClient?.Dispose();
    }
    private void AddServer_Click(object sender, RoutedEventArgs e) => EditServer(null);
    private void EditServer_Click(object sender, RoutedEventArgs e) { if (ServerTabs.SelectedItem is ServerTabRow row) EditServer(row); }
    private void EditServer(ServerTabRow? row)
    {
        if (editingServer || changingServer || exiting) return;
        var dialog = new ServerAddressDialog(row?.Binding, async (name, address) =>
        {
            ServerBinding binding;
            if (row is not null && address == row.Binding.Address) binding = row.Binding with { Name = name };
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var info = await workspaceClient!.InspectAsync(address, timeout.Token);
                if (row is not null && (row.Binding.ServerId != info.ServerId || row.Binding.PublicKey != info.PublicKey)) throw new InvalidOperationException("新地址不是原服务器，不能覆盖原绑定；请另行添加。");
                binding = new(row?.Binding.Id ?? Guid.NewGuid().ToString("N"), name, address, info.ServerId, info.PublicKey, "待连接", null, row?.Binding.Cached);
            }
            // Validate duplicate binding before stopping a working connection.
            if (Servers.Any(other => other != row && (other.Binding.Address == binding.Address || other.Binding.ServerId == binding.ServerId))) throw new ArgumentException("这台服务器已经添加。");
            if (row is not null) await StopServerAsync(row);
            store.SaveServerBinding(binding);
            if (row is not null) Servers.Remove(row);
            var replacement = new ServerTabRow(binding, store.GetSettings().Profile.DeviceId); Servers.Add(replacement); ServerTabs.SelectedItem = replacement; StartServer(replacement);
        }) { Owner = this, Icon = Icon };
        editingServer = true;
        try { dialog.ShowDialog(); } finally { editingServer = false; }
    }
    private async void ReconnectServer_Click(object sender, RoutedEventArgs e)
    {
        if (changingServer || editingServer || exiting || ServerTabs.SelectedItem is not ServerTabRow row) return;
        changingServer = true;
        try { await StopServerAsync(row); if (!exiting) StartServer(row); } finally { changingServer = false; }
    }
    private async void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (changingServer || editingServer || exiting || ServerTabs.SelectedItem is not ServerTabRow row) return;
        if (System.Windows.MessageBox.Show(this, $"移除服务器“{row.Binding.Name}”？本机发布资源和局域网设备记录会保留。", "移除服务器", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        changingServer = true;
        try { await StopServerAsync(row); store.RemoveServerBinding(row.Binding.Id); Servers.Remove(row); }
        catch (Exception ex) { SetStatus("移除服务器失败：" + ex.Message); if (!exiting) StartServer(row); }
        finally { changingServer = false; }
    }

    public sealed class ServerTabRow : INotifyPropertyChanged
    {
        public ServerBinding Binding { get; private set; }
        public string Name => Binding.Name;
        public string Address => Binding.Address;
        public string Status => Binding.Status;
        public string Header => Name + " · " + Status;
        public string VersionDetail => $"服务端 {ServerVersion ?? "待连接"} · 客户端 {AppVersion}";
        public string Detail => Binding.Error ?? SyncError ?? (Status == "在线" ? $"{Members.Count(m => m.State == "在线")} 台设备在线 · {Members.Count} 台已登记" + (SupportsResources ? "" : " · 资源功能需要服务端 0.2.0") : "保留上次名单，等待服务器连接。");
        internal bool SupportsResources;
        internal bool SupportsStorage;
        internal string? ServerVersion;
        internal string? SyncError;
        internal CancellationTokenSource? PublicationCancellation;
        internal Task? PublicationWorker;
        internal WorkspaceOwnerCatalog[] Catalogs = [];
        public ObservableCollection<ResourceTreeNode> ResourceTree { get; } = [];
        private ServerMemberRow? selectedMember;
        public ServerMemberRow? SelectedMember { get => selectedMember; set { selectedMember = value; RebuildResources(); Notify(); } }
        internal ResourceTreeNode? SelectedResource;
        private readonly string? ownDeviceId;
        public bool CanManageStored => Status == "在线" && SelectedMember?.DeviceId == ownDeviceId && SelectedResource?.RemoteRow?.Resource.ServerStored == true;
        public bool CanDownload => Status == "在线" && SelectedResource?.RemoteRow?.Status == "可下载";
        public string ResourceDetail => SelectedResource?.RemoteRow is { } row ? $"{row.Name}\n{row.Kind} · {row.Size} · {(row.Resource.ServerStored ? "服务器存储" : "本机发布")} · {row.Status}\n{row.Note}" : "选择左侧设备，再选择资源查看详情。";
        internal void SetCatalogs(WorkspaceOwnerCatalog[] catalogs) { Catalogs = catalogs; Update(Status, Binding.Error); }
        private void RebuildResources()
        {
            var selected = SelectedResource?.Key; ResourceTree.Clear(); SelectedResource = null;
            var data = Catalogs.FirstOrDefault(c => c.Owner == selectedMember?.DeviceId);
            if (data is null) return;
            var groups = BuildGroupTree(data.Catalog.Groups, ResourceTree, false);
            foreach (var item in data.Catalog.Resources)
            {
                var row = new ResourceRow(item, item.Name, KindText(item.Kind), ModeText(item.Mode), SizeText(item.Size), Status != "在线" ? "服务器离线" : !data.Online && !item.ServerStored ? "发布者离线" : item.Available ? "可下载" : "原文件不可用", item.Note);
                var node = new ResourceTreeNode { Key = item.Id, Name = item.Name, GroupId = item.GroupId, RemoteRow = row, IsFolder = item.Kind == ResourceKind.Folder, IsSelected = selected == item.Id };
                if (groups.TryGetValue(item.GroupId, out var parent)) parent.Children.Add(node); else ResourceTree.Add(node);
                if (node.IsSelected) SelectedResource = node;
            }
        }
        public ObservableCollection<ServerMemberRow> Members { get; } = [];
        internal CancellationTokenSource? Cancellation;
        internal Task? Worker;
        internal WorkspaceSession? Session;
        public ServerTabRow(ServerBinding binding, string? ownDeviceId = null) { this.ownDeviceId = ownDeviceId; Binding = binding; Update("待连接", null); }
        internal void Update(string status, string? error, WorkspaceSnapshot? snapshot = null)
        {
            Binding = Binding with { Status = status, Error = error, Cached = snapshot ?? Binding.Cached };
            var selected = selectedMember?.DeviceId;
            Members.Clear();
            foreach (var member in (Binding.Cached?.Members ?? []).OrderByDescending(m => m.Online).ThenBy(m => m.Profile.Nickname))
                Members.Add(new(member.Profile.DeviceId, member.Profile.Nickname, member.Profile.Version, status == "在线" ? member.Online ? "在线" : "离线" : "缓存 · 待核对", member.LastSeenUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), AvatarImage(member.Profile.Avatar), Catalogs.FirstOrDefault(c => c.Owner == member.Profile.DeviceId)?.Catalog.Resources.Count ?? 0));
            selectedMember = Members.FirstOrDefault(m => m.DeviceId == selected) ?? Members.FirstOrDefault(); RebuildResources(); Notify();
        }
        internal void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed record ServerMemberRow(string DeviceId, string Nickname, string Version, string State, string LastSeen, ImageSource? Avatar, int ResourceCount = 0);
}

internal sealed class ServerAddressDialog : Window
{
    internal ServerAddressDialog(ServerBinding? binding, Func<string, string, Task> save)
    {
        Title = binding is null ? "添加服务器" : "编辑服务器"; Width = 490; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "服务器名称" }); var name = new TextBox { Text = binding?.Name ?? "我的服务器", MaxLength = 80, Margin = new Thickness(0, 6, 0, 14) }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "公网 HTTPS 地址" }); var address = new TextBox { Text = binding?.Address ?? "https://", Margin = new Thickness(0, 6, 0, 14) }; panel.Children.Add(address);
        panel.Children.Add(new TextBlock { Text = "输入地址后直接加入，无需邀请码或账号。", TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "保存并连接", IsDefault = true, Padding = new Thickness(16, 8, 16, 8) };
        var saving = false; Closing += (_, e) => { if (saving) e.Cancel = true; };
        confirm.Click += async (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("请填写服务器名称。");
                var normalized = WorkspaceProtocol.NormalizeAddress(address.Text); saving = true; confirm.IsEnabled = cancel.IsEnabled = false; error.Text = "正在核对服务器…";
                await save(name.Text.Trim(), normalized); saving = false; DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex is OperationCanceledException ? "服务器响应超时，请核对地址。" : ex.Message; }
            finally { saving = false; confirm.IsEnabled = cancel.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons); Content = panel;
    }
}
