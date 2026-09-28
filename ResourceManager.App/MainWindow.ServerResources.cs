using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TreeView = System.Windows.Controls.TreeView;

namespace ResourceManager.App;

public partial class MainWindow
{
    private void PublishToServer_Click(object sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not ResourceTreeNode node || !node.IsGroup && node.LocalRow is null) { SetStatus("请选择一个发布资源或分组。"); return; }
        if (Servers.Count == 0) { SetStatus("请先在服务器页添加服务器。"); Tabs.SelectedIndex = 8; return; }
        var kind = node.IsGroup ? "group" : "resource"; var id = node.IsGroup ? node.GroupId : node.LocalRow!.Resource.Id;
        var dialog = new ServerPublicationDialog(store, kind, id, node.Name) { Owner = this, Icon = Icon };
        editingServer = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
            foreach (var choice in dialog.Choices) store.SetServerPublication(choice.Id, kind, id, choice.Enabled);
            SetStatus("服务器发布设置已保存，在线服务器将在数秒内同步；离线服务器重连后同步。");
        }
        catch (Exception ex) { ShowError("服务器发布设置失败", ex); }
        finally { editingServer = false; }
    }
    private void ServerResource_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView { DataContext: ServerTabRow row }) { row.SelectedResource = e.NewValue as ResourceTreeNode; row.Notify(); }
    }
    private void ServerDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerTabRow row } || row.SelectedMember is not { } member || row.SelectedResource?.RemoteRow is not { } selected) return;
        if (row.Status != "在线" || member.State != "在线" && !selected.Resource.ServerStored || !selected.Resource.Available) { SetStatus("服务器、发布者或资源当前不可用。"); return; }
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择服务器资源的下载保存目录" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            var job = downloader.CreateJob(WorkspacePeer(member), selected.Resource, dialog.SelectedPath);
            store.SaveServerDownload(job.Id, row.Binding.Id, member.Nickname, selected.Resource.ServerStored);
            RefreshDownloadsView(); Tabs.SelectedIndex = 5; QueueDownload(job.Id);
        }
        catch (Exception ex) { ShowError("创建下载失败", ex); }
    }
    private static PeerInfo WorkspacePeer(ServerMemberRow member) => new(member.DeviceId, "", 0, member.Nickname, null, null);
    private string DownloadSourceName(DownloadJob job)
    {
        var source = store.GetServerDownload(job.Id);
        if (source is null) return "设备直连 / " + (store.GetPeer(job.PeerId)?.Nickname ?? "未知设备");
        var binding = store.GetServerBindings().FirstOrDefault(b => b.Id == source.Value.Server);
        return $"{(source.Value.Stored ? "服务器存储" : "服务器发布者")} / {binding?.Name ?? "已移除服务器"} / {source.Value.OwnerName}";
    }
    private async Task RunResourceDownloadAsync(string id, IProgress<DownloadJob> progress, CancellationToken token)
    {
        var source = store.GetServerDownload(id);
        if (source is null) { await downloader.RunAsync(id, progress, token); return; }
        var job = store.GetDownload(id)!;
        var row = Servers.FirstOrDefault(s => s.Binding.Id == source.Value.Server) ?? throw new IOException("此任务对应的服务器已移除。");
        if (row.Session is null || row.Status != "在线") throw new IOException("服务器尚未连接，请连接后继续任务。");
        using var transport = new WorkspaceResourceClient(store, workspaceClient!, row.Binding, () => row.Session, job.PeerId, serverStored: source.Value.Stored);
        await transport.SelectRouteAsync(token);
        SetStatus($"正在从 {row.Name} / {source.Value.OwnerName} 下载（{transport.RouteName}）。");
        var peer = new PeerInfo(job.PeerId, "", 0, source.Value.OwnerName, null, null);
        var manager = new DownloadManager(store, transport, _ => peer);
        await Task.Run(() => manager.RunAsync(id, progress, token), token);
    }
}

internal sealed class ServerPublicationDialog : Window
{
    private readonly List<(string Id, CheckBox Box)> choices = [];
    internal IEnumerable<(string Id, bool Enabled)> Choices => choices.Select(c => (c.Id, c.Box.IsChecked == true));
    internal ServerPublicationDialog(NodeStore store, string kind, string id, string name)
    {
        Title = "发布到服务器 · " + name; Width = 520; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var grid = new Grid { Margin = new Thickness(24) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "勾选要发布到的服务器，取消勾选可撤销。分组会包含其当前及后续新增资源；资源单独设置优先。文件保留在本机，下载时本机须在线。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var list = new StackPanel(); foreach (var binding in store.GetServerBindings())
        {
            var box = new CheckBox { Content = binding.Name + "\n" + binding.Address, IsChecked = store.IsServerPublished(binding.Id, kind, id), Margin = new Thickness(0, 0, 0, 14) };
            choices.Add((binding.Id, box)); list.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "保存发布范围", IsDefault = true, Padding = new Thickness(16, 8, 16, 8) }; save.Click += (_, _) => DialogResult = true; buttons.Children.Add(save); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}
