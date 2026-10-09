using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Task)> activeUploads = [];
    public ObservableCollection<UploadDisplay> Uploads { get; } = [];
    private void RefreshUploads()
    {
        Uploads.Clear();
        foreach (var job in store.GetUploads())
        {
            var status = activeUploads.ContainsKey(job.Id) || job.Status is "已完成" or "已中断" or "已暂停" ? job.Status : "已暂停";
            Uploads.Add(new(job.Id, $"{job.Spec.Name} · {store.GetServerBindings().FirstOrDefault(b => b.Id == job.Server)?.Name ?? "服务器已移除"}\n{status} · {SizeText(job.Sent)} / {SizeText(job.Total)}{(job.Error is null ? "" : "\n" + job.Error)}"));
        }
    }
    private void UploadMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ServerTabs.SelectedItem is not ServerTabRow row || row.Session is null || !row.SupportsStorage) { SetStatus("请连接支持上传的服务端（0.3.0 或更新版本）。"); return; }
        var menu = new ContextMenu();
        var file = new MenuItem { Header = "上传文件…" }; file.Click += (_, _) => ChooseUpload(row, false); menu.Items.Add(file);
        var folder = new MenuItem { Header = "上传文件夹…" }; folder.Click += (_, _) => ChooseUpload(row, true); menu.Items.Add(folder);
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private void ChooseUpload(ServerTabRow row, bool folder)
    {
        string[] paths;
        if (folder)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择上传到服务器的文件夹" };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return; paths = [dialog.SelectedPath];
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择上传到服务器的文件", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return; paths = dialog.FileNames;
        }
        var permission = new StoredPermissionDialog(row.Binding.Cached?.Members ?? [], new(GroupAccess.Public, [])) { Owner = this, Icon = Icon };
        editingServer = true;
        try
        {
            if (permission.ShowDialog() != true) return;
            var manager = new WorkspaceUploadManager(store, workspaceClient!);
            foreach (var path in paths) { var job = manager.Create(row.Binding, path, permission.Permission.Access, permission.Permission.Allowed); StartUpload(job.Id); }
            RefreshUploads(); SetStatus("已创建上传任务；服务器页的“上传任务”可查看进度、暂停或继续。");
        }
        catch (Exception ex) { ShowError("创建上传失败", ex); }
        finally { editingServer = false; }
    }
    private void StartUpload(string id)
    {
        if (activeUploads.ContainsKey(id) || exiting) return;
        var job = store.GetUploads().FirstOrDefault(j => j.Id == id); if (job is null || job.Status == "已完成") return;
        var row = Servers.FirstOrDefault(s => s.Binding.Id == job.Server);
        if (row?.Session is null || !row.SupportsStorage) { SetStatus("请先连接此任务的服务器（需要服务端 0.3.0）。"); return; }
        var cancellation = new CancellationTokenSource();
        async Task Run()
        {
            await Task.Yield();
            try
            {
                var progress = new Progress<UploadJob>(_ => RefreshUploads());
                await Task.Run(() => new WorkspaceUploadManager(store, workspaceClient!).RunAsync(id, row.Binding, () => row.Session, progress, cancellation.Token));
                if (row.Session is not null) row.SetCatalogs(await workspaceClient!.CatalogsAsync(row.Binding, row.Session, cancellation.Token));
                SetStatus("上传完成：" + job.Spec.Name); RefreshFavoritesView();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("上传已中断：" + ex.Message); }
            finally { activeUploads.Remove(id); cancellation.Dispose(); RefreshUploads(); }
        }
        var task = Run(); activeUploads[id] = (cancellation, task); RefreshUploads();
    }
    private async Task StopUploadsAsync()
    {
        var jobs = activeUploads.Values.ToArray(); foreach (var job in jobs) job.Cancellation.Cancel(); await Task.WhenAll(jobs.Select(j => j.Task));
    }
    private void UploadTasks_Click(object sender, RoutedEventArgs e)
    {
        RefreshUploads();
        var window = new Window { Title = "服务器上传任务", Width = 690, Height = 480, Owner = this, Icon = Icon, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { Margin = new Thickness(20) }; grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var list = new ListBox { ItemsSource = Uploads, DisplayMemberPath = "Text", SelectedValuePath = "Id" }; grid.Children.Add(list);
        string? selectedId = null; list.SelectionChanged += (_, _) => { if (list.SelectedValue is string id) selectedId = id; };
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Add(string title, RoutedEventHandler click) { var b = new Button { Content = title, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) }; b.Click += click; buttons.Children.Add(b); }
        Add("暂停", (_, _) => { if (selectedId is { } id && activeUploads.TryGetValue(id, out var active)) active.Cancellation.Cancel(); });
        Add("继续", (_, _) => { if (selectedId is { } id) StartUpload(id); });
        Add("删除任务 / 服务器副本", async (_, _) =>
        {
            if (selectedId is not { } id) return;
            var job = store.GetUploads().FirstOrDefault(j => j.Id == id); if (job is null) return;
            var row = Servers.FirstOrDefault(s => s.Binding.Id == job.Server);
            if (row?.Session is null) { SetStatus("服务器未连接，无法删除服务器副本；任务已保留。"); return; }
            if (System.Windows.MessageBox.Show(window, "删除此任务以及服务器上的临时文件或已上传副本？本地源文件会保留。", "删除上传", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try
            {
                if (activeUploads.TryGetValue(id, out var active)) { active.Cancellation.Cancel(); await active.Task; }
                await workspaceClient!.DeleteStoredAsync(row.Binding, row.Session, id, default); store.RemoveUpload(id); RefreshUploads();
                row.SetCatalogs(await workspaceClient.CatalogsAsync(row.Binding, row.Session, default)); RefreshFavoritesView();
            }
            catch (Exception ex) { ShowError("删除上传失败", ex); }
        });
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons); window.Content = grid;
        editingServer = true; try { window.ShowDialog(); } finally { editingServer = false; }
    }
    private void ServerFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerTabRow row } || row.SelectedMember is not { } member || row.SelectedResource?.RemoteRow?.Resource is not { } resource) return;
        store.SaveServerFavorite(new(member.DeviceId, resource.Id, resource.Name, resource.Kind, row.Binding.ServerId, resource.ServerStored)); RefreshFavoritesView(); SetStatus("已加入服务器收藏。");
    }
    private void AppendServerFavorites()
    {
        foreach (var favorite in store.GetServerFavorites())
        {
            var server = Servers.FirstOrDefault(s => s.Binding.ServerId == favorite.ServerId); var catalog = server?.Catalogs.FirstOrDefault(c => c.Owner == favorite.PeerId);
            var resource = catalog?.Catalog.Resources.FirstOrDefault(r => r.Id == favorite.ResourceId && r.ServerStored == favorite.ServerStored);
            var state = server is null ? "服务器已移除" : server.Status != "在线" ? "服务器离线" : resource is null ? "已撤销或无权访问" : !favorite.ServerStored && catalog?.Online != true ? "发布者离线" : resource.Available ? "可下载" : "原文件不可用";
            var owner = server?.Binding.Cached?.Members.FirstOrDefault(m => m.Profile.DeviceId == favorite.PeerId)?.Profile.Nickname ?? "未知设备";
            Favorites.Add(new(favorite, favorite.Name, $"{server?.Name ?? "已移除服务器"} / {owner} / {(favorite.ServerStored ? "服务器存储" : "本机发布")}", KindText(favorite.Kind), state, resource?.Note ?? ""));
        }
    }
    private void DownloadServerFavorite(Favorite favorite, bool saveAs = false)
    {
        var row = Servers.FirstOrDefault(s => s.Binding.ServerId == favorite.ServerId); var catalog = row?.Catalogs.FirstOrDefault(c => c.Owner == favorite.PeerId);
        var resource = catalog?.Catalog.Resources.FirstOrDefault(r => r.Id == favorite.ResourceId && r.ServerStored == favorite.ServerStored); if (row is null || resource is null) return;
        var owner = row.Binding.Cached?.Members.FirstOrDefault(m => m.Profile.DeviceId == favorite.PeerId)?.Profile.Nickname ?? "未知设备";
        try { var job = ChooseDownloadJob(new(favorite.PeerId, "", 0, owner, null, null), resource, saveAs); if (job is null) return; store.SaveServerDownload(job.Id, row.Binding.Id, owner, favorite.ServerStored); RefreshDownloadsView(); Tabs.SelectedIndex = 5; QueueDownload(job.Id); }
        catch (Exception ex) { ShowError("创建下载失败", ex); }
    }
    private async void DeleteStored_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerTabRow row } || row.Session is null || row.SelectedResource?.RemoteRow?.Resource is not { ServerStored: true } resource) { SetStatus("请选择服务器存储中的资源。"); return; }
        if (row.SelectedMember?.DeviceId != store.GetSettings().Profile.DeviceId) { SetStatus("只能删除自己上传的资源。"); return; }
        if (System.Windows.MessageBox.Show(this, $"删除服务器上的“{resource.Name}”？正在下载该资源的任务也会中断。本地文件会保留。", "删除服务器资源", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            await workspaceClient!.DeleteStoredAsync(row.Binding, row.Session, resource.Id, default); store.RemoveUpload(resource.Id); RefreshUploads();
            row.SetCatalogs(await workspaceClient.CatalogsAsync(row.Binding, row.Session, default)); RefreshFavoritesView();
        }
        catch (Exception ex) { ShowError("删除资源失败", ex); }
    }
    private async void StoredPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerTabRow row } || row.Session is null || row.SelectedResource?.RemoteRow?.Resource is not { ServerStored: true } resource) { SetStatus("本机发布资源请在“我的发布”中修改分组权限；此处用于已上传资源。"); return; }
        if (row.SelectedMember?.DeviceId != store.GetSettings().Profile.DeviceId) { SetStatus("只能修改自己上传资源的权限。"); return; }
        editingServer = true;
        try
        {
            var current = await workspaceClient!.GetStoredStateAsync(row.Binding, row.Session, resource.Id, default);
            var dialog = new StoredPermissionDialog(row.Binding.Cached?.Members ?? [], new(current.Spec.Access, current.Spec.Allowed)) { Owner = this, Icon = Icon };
            if (dialog.ShowDialog() != true) return;
            await workspaceClient.StoredPermissionAsync(row.Binding, row.Session, resource.Id, dialog.Permission, default);
            var job = store.GetUploads().FirstOrDefault(j => j.Id == resource.Id); if (job is not null) store.SaveUpload(job with { Spec = job.Spec with { Access = dialog.Permission.Access, Allowed = dialog.Permission.Allowed } });
            row.SetCatalogs(await workspaceClient.CatalogsAsync(row.Binding, row.Session, default)); RefreshFavoritesView();
        }
        catch (Exception ex) { ShowError("修改权限失败", ex); }
        finally { editingServer = false; }
    }
    public sealed record UploadDisplay(string Id, string Text);
}

internal sealed class StoredPermissionDialog : Window
{
    private readonly ComboBox access = new() { Margin = new Thickness(0, 10, 0, 14) };
    private readonly List<(string Id, CheckBox Box)> devices = [];
    public StoredPermission Permission => new(new[] { GroupAccess.Public, GroupAccess.Private, GroupAccess.AllowList }[access.SelectedIndex], devices.Where(d => d.Box.IsChecked == true).Select(d => d.Id).ToArray());
    public StoredPermissionDialog(IReadOnlyList<WorkspaceMember> members, StoredPermission current)
    {
        Title = "服务器资源访问权限"; Width = 500; Height = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var grid = new Grid { Margin = new Thickness(24) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        access.ItemsSource = new[] { "公开 · 此服务器成员可访问", "私有 · 仅自己可访问", "白名单 · 指定设备可访问" }; access.SelectedIndex = current.Access == GroupAccess.Public ? 0 : current.Access == GroupAccess.Private ? 1 : 2; grid.Children.Add(access);
        var panel = new StackPanel();
        foreach (var member in members)
        {
            var box = new CheckBox { Content = member.Profile.Nickname + "\n" + member.Profile.DeviceId, IsChecked = current.Allowed.Contains(member.Profile.DeviceId), Margin = new Thickness(0, 0, 0, 12) }; devices.Add((member.Profile.DeviceId, box)); panel.Children.Add(box);
        }
        panel.IsEnabled = access.SelectedIndex == 2; access.SelectionChanged += (_, _) => panel.IsEnabled = access.SelectedIndex == 2;
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; var save = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(16, 8, 16, 8) }; save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) }); buttons.Children.Add(save); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}
