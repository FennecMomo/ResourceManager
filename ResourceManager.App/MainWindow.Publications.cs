using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    public ObservableCollection<ResourceTreeNode> LocalResourceTree { get; } = [];
    public ObservableCollection<ResourceTreeNode> RemoteResourceTree { get; } = [];
    private readonly Dictionary<string, IReadOnlyList<ResourceGroup>> peerResourceGroups = [];
    private LocalResourceRow? SelectedLocalResource => (LocalGrid?.SelectedItem as ResourceTreeNode)?.LocalRow;
    private ResourceRow? SelectedRemoteResource => (RemoteGrid?.SelectedItem as ResourceTreeNode)?.RemoteRow;
    private string SelectedPublicationGroup => (LocalGrid?.SelectedItem as ResourceTreeNode)?.GroupId ?? NodeStore.DefaultResourceGroupId;
    private bool publishing;
    private Task<LocalResource>? publishingTask;
    private int localDetailsGeneration;
    private CancellationTokenSource? localDetailsCancellation;

    internal static Dictionary<string, ResourceTreeNode> BuildGroupTree(IReadOnlyList<ResourceGroup> groups, ObservableCollection<ResourceTreeNode> roots)
    {
        var unique = groups.Where(g => !string.IsNullOrEmpty(g.Id)).GroupBy(g => g.Id).Select(g => g.First()).ToList();
        if (!unique.Any(g => g.Id == NodeStore.DefaultResourceGroupId)) unique.Insert(0, new(NodeStore.DefaultResourceGroupId, "默认组", null, 0, DateTimeOffset.UnixEpoch));
        var nodes = unique.ToDictionary(g => g.Id, g => new ResourceTreeNode { Key = "group:" + g.Id, Name = g.Name, GroupId = g.Id, IsGroup = true, IsExpanded = true });
        var parents = unique.ToDictionary(g => g.Id, g => g.ParentId);
        foreach (var group in unique.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id))
        {
            var seen = new HashSet<string> { group.Id }; var cursor = group.ParentId; var cycle = false;
            while (cursor is not null && parents.TryGetValue(cursor, out var next)) { if (!seen.Add(cursor)) { cycle = true; break; } cursor = next; }
            if (!cycle && group.ParentId is { } parent && nodes.TryGetValue(parent, out var parentNode)) parentNode.Children.Add(nodes[group.Id]);
            else roots.Add(nodes[group.Id]);
        }
        return nodes;
    }

    private void RefreshLocalView()
    {
        var selectedKey = (LocalGrid.SelectedItem as ResourceTreeNode)?.Key;
        var expanded = FlattenTree(LocalResourceTree).Where(n => n.IsExpanded).Select(n => n.Key).ToHashSet();
        LocalResources.Clear(); LocalResourceTree.Clear();
        var groups = BuildGroupTree(store.GetResourceGroups(), LocalResourceTree);
        foreach (var item in store.GetResources().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id))
        {
            var available = item.Kind == ResourceKind.Folder ? Directory.Exists(item.SourcePath) : File.Exists(item.SourcePath);
            var size = item.Kind == ResourceKind.Folder ? "选中后统计" : "—";
            try { if (item.Kind == ResourceKind.File && available) size = SizeText(new FileInfo(item.SourcePath).Length); }
            catch (IOException) { available = false; }
            catch (UnauthorizedAccessException) { available = false; }
            var row = new LocalResourceRow(item, item.Name, KindText(item.Kind), ModeText(item.Mode), size, available ? "可用" : "原文件不可用", item.SourcePath, item.Note);
            LocalResources.Add(row);
            var node = new ResourceTreeNode { Key = "resource:" + item.Id, Name = item.Name, GroupId = item.GroupId, LocalRow = row,
                Path = item.SourcePath, OwnerResourceId = item.Id, IsFolder = item.Kind == ResourceKind.Folder, IsExpanded = expanded.Contains("resource:" + item.Id) };
            AddFolderPlaceholder(node);
            (groups.GetValueOrDefault(item.GroupId) ?? groups[NodeStore.DefaultResourceGroupId]).Children.Add(node);
        }
        var selection = FlattenTree(LocalResourceTree).FirstOrDefault(n => n.Key == selectedKey) ?? groups[NodeStore.DefaultResourceGroupId];
        selection.IsSelected = true;
        UpdateActions();
    }

    private static IEnumerable<ResourceTreeNode> FlattenTree(IEnumerable<ResourceTreeNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in FlattenTree(node.Children)) yield return child; }
    }

    private void RebuildRemoteTree(string? selectedResource)
    {
        RemoteResourceTree.Clear();
        var peer = (PeersGrid.SelectedItem as PeerRow)?.Peer.DeviceId;
        var groups = BuildGroupTree(peer is null ? [] : peerResourceGroups.GetValueOrDefault(peer) ?? [], RemoteResourceTree);
        foreach (var row in RemoteResources.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Resource.Id))
        {
            var node = new ResourceTreeNode { Key = "resource:" + row.Resource.Id, Name = row.Name, GroupId = row.Resource.GroupId,
                RemoteRow = row, IsFolder = row.Resource.Kind == ResourceKind.Folder, IsSelected = row.Resource.Id == selectedResource };
            (groups.GetValueOrDefault(row.Resource.GroupId) ?? groups[NodeStore.DefaultResourceGroupId]).Children.Add(node);
        }
        PeerEmptyText.Visibility = RemoteResourceTree.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemoteTree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => UpdateActions();

    private async void LocalTree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var generation = ++localDetailsGeneration;
        localDetailsCancellation?.Cancel();
        localDetailsCancellation?.Dispose();
        localDetailsCancellation = null;
        var node = LocalGrid.SelectedItem as ResourceTreeNode;
        if (LocalDetailName is null) return;
        LocalDetailName.Text = node?.Name ?? "选择左侧资源或分组";
        LocalDetailPath.Text = node?.Path ?? "分组只整理发布入口，不移动磁盘文件。";
        LocalResourceActions.Visibility = node?.LocalRow is null ? Visibility.Collapsed : Visibility.Visible;
        LocalNoteBox.Text = node?.LocalRow?.Note ?? "";
        RenameGroupButton.IsEnabled = DeleteGroupButton.IsEnabled = node is { IsGroup: true } && node.GroupId != NodeStore.DefaultResourceGroupId;
        MovePublicationButton.IsEnabled = node?.LocalRow is not null || RenameGroupButton.IsEnabled;
        LocalSendButton.IsEnabled = node?.LocalRow?.CanRemind == true;
        LocalSendButton.DataContext = node?.LocalRow;
        UpdateActions();
        if (node is null) { LocalDetailInfo.Text = "在右上角点击发布，选择文件或文件夹开始共享。"; return; }
        if (node.IsGroup)
        {
            var ids = store.GetResourceGroupSubtree(node.GroupId);
            LocalDetailInfo.Text = $"资源分组\n包含 {store.GetResources().Count(r => ids.Contains(r.GroupId))} 个发布项（含子分组）\n点击发布会添加到这个分组。";
            return;
        }
        var groupName = GroupPath(node.GroupId);
        if (node.LocalRow is not { } row)
        {
            LocalDetailInfo.Text = $"{(node.IsFolder ? "文件夹" : "文件")}\n所属分组：{groupName}\n这是已发布文件夹内的内容，不是独立发布项。";
            if (!node.IsFolder && node.Path is { } path)
                try { LocalDetailInfo.Text += $"\n大小：{SizeText(new FileInfo(path).Length)}\n修改时间：{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}"; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalDetailInfo.Text += "\n文件已不可用，请刷新。"; }
            return;
        }
        LocalDetailInfo.Text = $"{row.Kind} · {row.Mode}\n状态：{row.Status}\n所属分组：{groupName}\n发布时间：{row.Resource.PublishedUtc.LocalDateTime:yyyy-MM-dd HH:mm}\n大小：{row.Size}";
        if (row.Resource.Kind == ResourceKind.Folder)
        {
            localDetailsCancellation = new CancellationTokenSource();
            var token = localDetailsCancellation.Token;
            try
            {
                var info = await Task.Run(() => catalog.Describe(row.Resource, token), token);
                if (generation == localDetailsGeneration && !exiting)
                    LocalDetailInfo.Text = $"{row.Kind} · {row.Mode}\n状态：{(info.Available ? "可用" : "原文件不可用")}\n所属分组：{groupName}\n发布时间：{row.Resource.PublishedUtc.LocalDateTime:yyyy-MM-dd HH:mm}\n大小：{SizeText(info.Size)}";
            }
            catch (OperationCanceledException) { }
        }
    }

    private string GroupPath(string id)
    {
        var groups = store.GetResourceGroups().ToDictionary(g => g.Id);
        var names = new List<string>(); var visited = new HashSet<string>();
        while (groups.TryGetValue(id, out var group) && visited.Add(id)) { names.Insert(0, group.Name); if (group.ParentId is null) break; id = group.ParentId; }
        return string.Join(" / ", names);
    }

    private static void AddFolderPlaceholder(ResourceTreeNode node)
    {
        if (node.IsFolder) node.Children.Add(new() { Key = "placeholder", Name = "正在读取…", GroupId = node.GroupId });
    }

    private async void LocalTree_Expanded(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as TreeViewItem)?.DataContext is ResourceTreeNode node) await LoadPublicationChildrenAsync(node);
    }

    internal async Task LoadPublicationChildrenAsync(ResourceTreeNode node)
    {
        if (!node.IsFolder || node.ChildrenLoaded || node.Path is null || node.OwnerResourceId is null) return;
        node.ChildrenLoaded = true;
        try
        {
            var owner = store.GetResource(node.OwnerResourceId) ?? throw new IOException("发布已撤销。");
            var relative = System.IO.Path.GetRelativePath(owner.SourcePath, node.Path);
            var directory = relative == "." ? owner.SourcePath : catalog.ResolveFile(owner.Id, relative).FullName;
            var children = await Task.Run(() => new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(f => !f.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => (f.Attributes & FileAttributes.Directory) != 0).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ResourceTreeNode { Key = node.Key + "/" + f.Name, Name = f.Name, GroupId = node.GroupId, Path = f.FullName,
                    IsFolder = (f.Attributes & FileAttributes.Directory) != 0, OwnerResourceId = owner.Id }).ToArray());
            if (exiting) return;
            node.Children.Clear();
            foreach (var child in children) { AddFolderPlaceholder(child); node.Children.Add(child); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            node.ChildrenLoaded = false; node.Children.Clear();
            SetStatus("读取文件树失败：" + ex.Message);
            AddFolderPlaceholder(node);
        }
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (publishing || exiting) return;
        PublishButton.ContextMenu.PlacementTarget = PublishButton;
        PublishButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PublishButton.ContextMenu.IsOpen = true;
    }

    private async void PublishFiles_Click(object sender, RoutedEventArgs e)
    {
        if (publishing || exiting) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择要发布的文件", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await ConfirmPublicationAsync(picker.FileNames);
    }

    private async void PublishFolders_Click(object sender, RoutedEventArgs e)
    {
        if (publishing || exiting) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择要发布的文件夹", Multiselect = true };
        if (picker.ShowDialog(this) == true) await ConfirmPublicationAsync(picker.FolderNames);
    }

    private async Task ConfirmPublicationAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || publishing || exiting) return;
        var mode = new PublishModeDialog(paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : $"{paths.Count} 个项目") { Owner = this, Icon = Icon };
        if (mode.ShowDialog() != true) return;
        var failures = await PublishPathsAsync(paths, mode.SelectedMode, SelectedPublicationGroup);
        if (!exiting && failures.Count > 0) System.Windows.MessageBox.Show(this, string.Join("\n", failures), "部分项目发布失败");
    }

    internal async Task<IReadOnlyList<string>> PublishPathsAsync(IReadOnlyList<string> paths, PublishMode mode, string group)
    {
        if (publishing || exiting) throw new InvalidOperationException("发布任务正在运行或程序正在退出。");
        publishing = true; PublishButton.IsEnabled = false;
        var failures = new List<string>(); var count = 0;
        try
        {
            foreach (var path in paths)
            {
                if (exiting) break;
                SetStatus($"正在发布 {System.IO.Path.GetFileName(path)}…");
                try { publishingTask = Task.Run(() => store.AddResource(path, mode, groupId: group)); await publishingTask; count++; }
                catch (Exception ex) { failures.Add($"{System.IO.Path.GetFileName(path)}：{ex.Message}"); }
            }
            if (!exiting)
            {
                RefreshLocalView(); SetStatus($"已发布 {count} 个项目。" + (failures.Count == 0 ? "对方刷新后即可看到。" : $"失败 {failures.Count} 个。"));
            }
            return failures;
        }
        finally { publishing = false; publishingTask = null; PublishButton.IsEnabled = true; }
    }

    private void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GroupEditorDialog("新建分组", store.GetResourceGroups(), "", SelectedPublicationGroup, true, true) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true) return;
        try { store.SaveResourceGroup(dialog.GroupName, dialog.ParentId); RefreshLocalView(); }
        catch (Exception ex) { ShowError("创建分组失败", ex); }
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e) => EditPublicationGroup(true);
    private void MovePublication_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLocalResource is not { } resource) { EditPublicationGroup(false); return; }
        var dialog = new GroupEditorDialog("移动资源", store.GetResourceGroups(), "", resource.Resource.GroupId, false, false) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true || dialog.ParentId is null) return;
        try { store.MoveResourceToGroup(resource.Resource.Id, dialog.ParentId); RefreshLocalView(); }
        catch (Exception ex) { ShowError("移动资源失败", ex); }
    }

    private void EditPublicationGroup(bool rename)
    {
        if (LocalGrid.SelectedItem is not ResourceTreeNode { IsGroup: true } node || node.GroupId == NodeStore.DefaultResourceGroupId) return;
        var groups = store.GetResourceGroups(); var group = groups.First(g => g.Id == node.GroupId);
        var dialog = new GroupEditorDialog(rename ? "重命名分组" : "移动分组", groups, group.Name, group.ParentId, rename, true, store.GetResourceGroupSubtree(group.Id)) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true) return;
        try { store.SaveResourceGroup(rename ? dialog.GroupName : group.Name, dialog.ParentId, group.Id); RefreshLocalView(); }
        catch (Exception ex) { ShowError("修改分组失败", ex); }
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not ResourceTreeNode { IsGroup: true } node || node.GroupId == NodeStore.DefaultResourceGroupId) return;
        var ids = store.GetResourceGroupSubtree(node.GroupId);
        var resources = store.GetResources().Where(r => ids.Contains(r.GroupId)).ToArray();
        var revoke = DeleteResourceGroupDialog.Ask(this, node.Name, ids.Count - 1, resources);
        if (revoke is null) return;
        try { await Task.Run(() => store.DeleteResourceGroup(node.GroupId, revoke.Value)); RefreshLocalView(); }
        catch (Exception ex) { RefreshLocalView(); ShowError("删除分组未完全完成", ex); }
    }

    private void SendSelectedPublication_Click(object sender, RoutedEventArgs e) => SendReminderRow_Click(LocalSendButton, e);
}
