using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ResourceManager.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Path = System.IO.Path;
using Cursors = System.Windows.Input.Cursors;
using WinForms = System.Windows.Forms;

namespace ResourceManager.App;

public partial class MainWindow
{
    private GitCommandRunner? gitRunner;
    private GitRepositoryService? gitRepository;
    private readonly CancellationTokenSource gitCancellation = new();
    private bool gitRefreshBusy;
    private bool gitActionBusy;
    private GitProjectEvent? selectedGitEvent;
    private string? selectedGitCommit;

    public ObservableCollection<GitProjectRow> GitProjectRows { get; } = [];

    private async Task DetectGitAsync(string? preferred = null)
    {
        GitEnvironmentText.Text = "正在检测 Git for Windows…";
        GitMissingPanel.Visibility = Visibility.Collapsed;
        try
        {
            gitRunner = await GitEnvironment.DetectAsync(preferred, gitCancellation.Token);
            gitRepository = gitRunner is null ? null : new GitRepositoryService(gitRunner);
            GitEnvironmentText.Text = gitRunner is null
                ? "未检测到 Git。安装后即可创建、加入和同步协作项目；普通资源共享不受影响。"
                : $"Git 已就绪 · {await gitRunner.RunAsync(null, gitCancellation.Token, "--version")} · {gitRunner.Executable}";
        }
        catch (OperationCanceledException) when (exiting) { return; }
        catch (Exception ex)
        {
            gitRunner = null;
            gitRepository = null;
            GitEnvironmentText.Text = $"检测 Git 失败：{ex.Message}";
        }
        GitMissingPanel.Visibility = gitRunner is null ? Visibility.Visible : Visibility.Collapsed;
        GitCreateButton.IsEnabled = gitRunner is not null && !gitActionBusy;
        UpdateGitProjectActions();
    }

    private async void GitDetect_Click(object sender, RoutedEventArgs e) => await DetectGitAsync();

    private async void GitChooseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Git 程序|git.exe", Title = "选择 git.exe" };
        if (dialog.ShowDialog(this) != true) return;
        await DetectGitAsync(dialog.FileName);
        if (gitRunner?.Executable == dialog.FileName) feedbackStore.SetSetting("git_executable", dialog.FileName);
    }

    private async void GitInstall_Click(object sender, RoutedEventArgs e)
    {
        GitInstallButton.IsEnabled = false;
        GitEnvironmentText.Text = "正在通过 WinGet 安装 Git for Windows；系统可能请求管理员权限…";
        try
        {
            var start = new ProcessStartInfo("winget.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "install", "--id", "Git.Git", "--exact", "--source", "winget",
                         "--silent", "--accept-source-agreements", "--accept-package-agreements" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WinGet。");
            await process.WaitForExitAsync(gitCancellation.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException($"WinGet 安装未完成（退出码 {process.ExitCode}）。");
            await DetectGitAsync();
            if (gitRunner is null) GitEnvironmentText.Text = "安装程序已结束，但尚未找到 git.exe。请点击“重新检测”或手动选择。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GitEnvironmentText.Text = $"一键安装失败：{ex.Message}。可以从 Git 官方网站安装后重新检测。";
            GitMissingPanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (exiting) { }
        finally { GitInstallButton.IsEnabled = true; }
    }

    private void GitDownloadOfficial_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://git-scm.com/install/windows") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("打开 Git 官方下载页失败", ex); }
    }

    private void GitChooseRepository_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog { Description = "选择已有 Git 仓库" };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            GitRepositoryPathBox.Text = dialog.SelectedPath;
            if (string.IsNullOrWhiteSpace(GitProjectNameBox.Text))
                GitProjectNameBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.SelectedPath));
        }
    }

    private async void GitCreate_Click(object sender, RoutedEventArgs e)
    {
        if (gitRepository is null || gitActionBusy) return;
        var name = GitProjectNameBox.Text.Trim();
        if (name.Length is < 1 or > 80) { SetStatus("项目名称须为 1–80 个字符。"); return; }
        gitActionBusy = true;
        UpdateGitProjectActions();
        try
        {
            var inspected = await gitRepository.InspectAsync(GitRepositoryPathBox.Text, gitCancellation.Token);
            if (inspected.Dirty && System.Windows.MessageBox.Show(
                    "此仓库有未提交修改。协作项目只会包含已提交的历史，当前修改不会发送。仍要继续吗？",
                    "创建 Git 协作项目", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var projectId = Guid.NewGuid().ToString("N");
            SetStatus("正在打包 Git 历史及其中的 LFS 文件；大型仓库可能需要几分钟…");
            var bundle = await gitRepository.CreateBundleAsync(inspected.Root, inspected.Branch,
                gitProjects.BundleDirectory, gitCancellation.Token);
            var commits = await gitRepository.RecentCommitsAsync(inspected.Root, inspected.Head, gitCancellation.Token);
            var profile = store.GetSettings().Profile;
            gitProjects.CreateEvent(projectId, name, inspected.Branch, profile.DeviceId, profile.Nickname,
                "Created", inspected.Head, null, commits, bundle);
            gitProjects.SetBinding(new GitProjectBinding(projectId, inspected.Root, inspected.Branch));
            RefreshGitProjectList(projectId);
            var delivered = await SyncGitPeersAsync();
            SetStatus($"协作项目“{name}”已创建；{DeliveryText(delivered)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("创建协作项目失败", ex); }
        finally { gitActionBusy = false; UpdateGitProjectActions(); }
    }

    private async void GitRefresh_Click(object sender, RoutedEventArgs e) => await RefreshGitAsync();

    private async Task RefreshGitAsync()
    {
        if (gitRefreshBusy || exiting) return;
        gitRefreshBusy = true;
        try
        {
            if (gitRunner is null)
                await DetectGitAsync(feedbackStore.GetSetting("git_executable"));
            await SyncGitPeersAsync();
            RefreshGitProjectList();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { GitProjectStatusText.Text = $"刷新协作图失败：{ex.Message}"; }
        finally { gitRefreshBusy = false; }
    }

    private async Task<int> SyncGitPeersAsync()
    {
        var peers = store.GetPeers().Where(peer => peerStatus.GetValueOrDefault(peer.DeviceId) == "在线" &&
            peerCapabilities.GetValueOrDefault(peer.DeviceId)?.Contains("git-collaboration-v1") == true).ToArray();
        var exchanges = peers.Select(async peer =>
        {
            try
            {
                GitLineVersion[]? remoteKnown = null;
                for (var round = 0; round < 20; round++)
                {
                    var outgoing = remoteKnown is null ? [] : gitProjects.GetMissing(remoteKnown);
                    var response = await client.SyncGitAsync(peer, outgoing,
                        gitProjects.GetVersions(), gitCancellation.Token);
                    gitProjects.Merge(response.Events);
                    remoteKnown = response.Known;
                    if (outgoing.Length == 0 && response.Events.Length == 0 && round > 0) break;
                }
                return true;
            }
            catch (Exception) when (!gitCancellation.IsCancellationRequested) { return false; }
        });
        return (await Task.WhenAll(exchanges)).Count(success => success);
    }

    private static string DeliveryText(int peers) => peers == 0
        ? "当前没有可同步的在线设备，记录会在重新连接后补齐。"
        : $"已与 {peers} 台在线设备交换协作记录。";

    private void RefreshGitProjectList(string? preferredProjectId = null)
    {
        var selected = preferredProjectId ?? (GitProjectList?.SelectedItem as GitProjectRow)?.Project.ProjectId;
        GitProjectRows.Clear();
        foreach (var project in gitProjects.GetProjects()) GitProjectRows.Add(new GitProjectRow(project));
        if (GitProjectList is not null)
            GitProjectList.SelectedItem = GitProjectRows.FirstOrDefault(row => row.Project.ProjectId == selected);
        UpdateGitProjectActions();
    }

    private void GitProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedGitEvent = null;
        selectedGitCommit = null;
        UpdateGitProjectActions();
        RenderGitGraph();
    }

    private void UpdateGitProjectActions()
    {
        if (GitJoinButton is null) return;
        var row = GitProjectList.SelectedItem as GitProjectRow;
        var binding = row is null ? null : gitProjects.GetBinding(row.Project.ProjectId);
        GitSelectedProjectText.Text = row?.Name ?? "选择一个协作项目";
        GitProjectStatusText.Text = row is null ? "项目中的每条线属于一台设备。"
            : binding is null ? $"{row.Project.MemberCount} 名成员 · 尚未加入 · 选择保存位置后下载仓库"
            : $"{row.Project.MemberCount} 名成员 · 已加入 · 本地仓库：{binding.RepositoryPath}";
        GitJoinButton.IsEnabled = row is not null && binding is null && gitRepository is not null && !gitActionBusy;
        GitPublishButton.IsEnabled = binding is not null && gitRepository is not null && !gitActionBusy;
        GitSyncButton.IsEnabled = binding is not null && selectedGitEvent is not null &&
                                  selectedGitEvent.MemberId != gitProjects.MemberId && gitRepository is not null && !gitActionBusy;
    }

    private async Task<string> GetGitBundleAsync(GitProjectEvent item)
    {
        if (item.BundleHash is null || item.BundleSize <= 0)
            throw new InvalidOperationException("这个提交点尚无可下载的 Git 数据包。");
        var existing = gitProjects.BundlePath(item.BundleHash);
        if (existing is not null && await GitBundleFile.VerifyAsync(existing, item.BundleHash,
                item.BundleSize, gitCancellation.Token)) return existing;
        var bundle = new GitBundleInfo(item.BundleHash, item.BundleSize,
            Path.Combine(gitProjects.BundleDirectory, item.BundleHash + ".bundle"));
        foreach (var peer in store.GetPeers().Where(peer => peerStatus.GetValueOrDefault(peer.DeviceId) == "在线"))
        {
            try { return await client.DownloadGitBundleAsync(peer, bundle, gitCancellation.Token); }
            catch (Exception) when (!gitCancellation.IsCancellationRequested) { }
        }
        throw new IOException("当前在线设备都没有这个协作包；持有它的成员上线后再试。");
    }

    private async void GitJoin_Click(object sender, RoutedEventArgs e)
    {
        if (GitProjectList.SelectedItem is not GitProjectRow row || gitRepository is null || gitActionBusy) return;
        using var dialog = new WinForms.FolderBrowserDialog { Description = "选择新仓库的上级文件夹" };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
        var safeName = string.Concat(row.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(dialog.SelectedPath, safeName);
        gitActionBusy = true;
        UpdateGitProjectActions();
        try
        {
            var created = gitProjects.GetEvents(row.Project.ProjectId).First(item => item.Kind == "Created");
            var source = gitProjects.GetEvents(row.Project.ProjectId)
                .Where(item => item.MemberId == created.MemberId && item.BundleHash is not null)
                .OrderByDescending(item => item.Sequence).First();
            SetStatus($"正在获取协作包（{SizeText(source.BundleSize)}），包含 Git 历史和 LFS 文件…");
            var bundle = await GetGitBundleAsync(source);
            await gitRepository.CloneAsync(bundle, created.Branch, destination, gitCancellation.Token);
            var profile = store.GetSettings().Profile;
            gitProjects.CreateEvent(row.Project.ProjectId, row.Name, created.Branch, profile.DeviceId,
                profile.Nickname, "Joined", source.CommitHash, source.CommitHash, [],
                new GitBundleInfo(source.BundleHash!, source.BundleSize, bundle));
            gitProjects.SetBinding(new GitProjectBinding(row.Project.ProjectId, destination, created.Branch));
            var delivered = await SyncGitPeersAsync();
            RefreshGitProjectList(row.Project.ProjectId);
            SetStatus($"已加入“{row.Name}”；本地仓库位于 {destination}。{DeliveryText(delivered)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("加入协作项目失败", ex); }
        finally { gitActionBusy = false; UpdateGitProjectActions(); }
    }

    private async void GitPublish_Click(object sender, RoutedEventArgs e)
    {
        if (GitProjectList.SelectedItem is not GitProjectRow row || gitRepository is null || gitActionBusy) return;
        var binding = gitProjects.GetBinding(row.Project.ProjectId);
        if (binding is null) return;
        gitActionBusy = true;
        UpdateGitProjectActions();
        try
        {
            var inspected = await gitRepository.InspectAsync(binding.RepositoryPath, gitCancellation.Token);
            if (inspected.Branch != binding.Branch) throw new InvalidOperationException(
                $"当前在 {inspected.Branch} 分支；请切回协作分支 {binding.Branch} 后推送。");
            var last = gitProjects.GetEvents(row.Project.ProjectId)
                .Where(item => item.MemberId == gitProjects.MemberId).OrderByDescending(item => item.Sequence).First();
            if (inspected.Head == last.CommitHash) { SetStatus("没有新的已提交内容。未提交修改不会进入协作项目。"); return; }
            if (!await gitRepository.IsAncestorAsync(inspected.Root, last.CommitHash, inspected.Head, gitCancellation.Token))
                throw new InvalidOperationException("当前分支改写了已发布历史；请先在 Git 工具中处理，不能强制覆盖协作线。");
            if (inspected.Dirty && System.Windows.MessageBox.Show(
                    "当前仓库还有未提交修改。本次只推送已提交的内容，继续吗？",
                    "推送到协作项目", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetStatus("正在打包新的 Git 历史及 LFS 文件…");
            var bundle = await gitRepository.CreateBundleAsync(inspected.Root, inspected.Branch,
                gitProjects.BundleDirectory, gitCancellation.Token);
            var commits = await gitRepository.RecentCommitsAsync(inspected.Root, inspected.Head, gitCancellation.Token);
            var profile = store.GetSettings().Profile;
            gitProjects.CreateEvent(row.Project.ProjectId, row.Name, binding.Branch, profile.DeviceId,
                profile.Nickname, "Published", inspected.Head, last.CommitHash, commits, bundle);
            var delivered = await SyncGitPeersAsync();
            RefreshGitProjectList(row.Project.ProjectId);
            SetStatus($"提交 {inspected.Head[..8]} 已加入你的协作线；{DeliveryText(delivered)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("推送提交失败", ex); }
        finally { gitActionBusy = false; UpdateGitProjectActions(); }
    }

    private async void GitSync_Click(object sender, RoutedEventArgs e)
    {
        if (GitProjectList.SelectedItem is not GitProjectRow row || selectedGitEvent is null ||
            selectedGitCommit is null || gitRepository is null || gitRunner is null || gitActionBusy) return;
        var binding = gitProjects.GetBinding(row.Project.ProjectId);
        if (binding is null) return;
        gitActionBusy = true;
        UpdateGitProjectActions();
        try
        {
            var inspected = await gitRepository.InspectAsync(binding.RepositoryPath, gitCancellation.Token);
            if (inspected.Branch != binding.Branch || inspected.Dirty)
                throw new InvalidOperationException("请先切到协作分支并处理未提交修改，然后再同步。");
            if (inspected.Head == selectedGitCommit)
            {
                SetStatus("本地仓库已经在这个提交点，无需再次同步。");
                return;
            }
            var bundleEvent = selectedGitEvent.BundleHash is not null ? selectedGitEvent :
                gitProjects.GetEvents(row.Project.ProjectId).Where(item => item.MemberId == selectedGitEvent.MemberId &&
                    item.BundleHash is not null).OrderByDescending(item => item.Sequence).FirstOrDefault()
                ?? throw new InvalidOperationException("这个成员尚无可用的代码包。");
            SetStatus($"正在获取协作包（{SizeText(bundleEvent.BundleSize)}），请稍候…");
            var bundle = await GetGitBundleAsync(bundleEvent);
            await gitRepository.FetchIntoInboxAsync(binding.RepositoryPath, bundle, binding.Branch,
                selectedGitEvent.MemberId, gitCancellation.Token);
            if (!await gitRepository.IsAncestorAsync(binding.RepositoryPath, inspected.Head,
                    selectedGitCommit, gitCancellation.Token))
            {
                SetStatus("双方已有各自的提交。对方内容已进入收件箱分支，请在 Git 工具中检查并合并；本地文件未改动。");
                return;
            }
            await gitRunner.RunAsync(binding.RepositoryPath, gitCancellation.Token, "merge", "--ff-only", selectedGitCommit);
            var profile = store.GetSettings().Profile;
            gitProjects.CreateEvent(row.Project.ProjectId, row.Name, binding.Branch, profile.DeviceId,
                profile.Nickname, "Synced", selectedGitCommit, selectedGitCommit, [], null);
            var delivered = await SyncGitPeersAsync();
            RefreshGitProjectList(row.Project.ProjectId);
            SetStatus($"已同步到 {selectedGitCommit[..8]}，你的下一次提交会从这里继续。{DeliveryText(delivered)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("同步提交失败", ex); }
        finally { gitActionBusy = false; UpdateGitProjectActions(); }
    }

    private void RenderGitGraph()
    {
        if (GitGraphCanvas is null) return;
        GitGraphCanvas.Children.Clear();
        if (GitProjectList.SelectedItem is not GitProjectRow row) return;
        var events = gitProjects.GetEvents(row.Project.ProjectId);
        var members = events.GroupBy(item => item.MemberId).OrderBy(group => group.Min(item => item.CreatedUtc)).ToArray();
        var known = new Dictionary<string, Point>(StringComparer.Ordinal);
        var palette = new[] { "#2867D9", "#18765A", "#8B6015", "#6D4FC2", "#B74650", "#2D8295" };
        var maxX = 180d;
        for (var rowIndex = 0; rowIndex < members.Length; rowIndex++)
        {
            var member = members[rowIndex].OrderBy(item => item.Sequence).ToArray();
            var y = 36 + rowIndex * 76d;
            var color = (SolidColorBrush)new BrushConverter().ConvertFromString(palette[rowIndex % palette.Length])!;
            AddGitLine(94, y, Math.Max(620, maxX + 90), y, Brushes.LightGray, 1);
            AddGitLabel(member[0].MemberName, 12, y - 12, color);
            Point? previous = null;
            var x = 126d;
            foreach (var item in member)
            {
                if (item.Kind is "Joined" or "Synced")
                {
                    var origin = item.SourceHash is not null && known.TryGetValue(item.SourceHash, out var found)
                        ? found : previous;
                    x = Math.Max(x + 62, (origin?.X ?? 90) + 62);
                    var point = new Point(x, y);
                    if (origin is not null) AddGitLine(origin.Value.X, origin.Value.Y, x, y, color, 2);
                    AddGitDot(point, color, false, item, item.CommitHash);
                    previous = point;
                }
                else
                {
                    foreach (var commit in item.Commits.Where(commit => !known.ContainsKey(commit.Hash)))
                    {
                        var parent = commit.Parents.Select(hash => known.TryGetValue(hash, out var position)
                            ? (Point?)position : null).FirstOrDefault(point => point is not null);
                        x = Math.Max(x + 52, (parent?.X ?? 80) + 52);
                        var point = new Point(x, y);
                        if (previous is not null) AddGitLine(previous.Value.X, previous.Value.Y, x, y, color, 2);
                        if (parent is not null && (previous is null || parent.Value.Y != previous.Value.Y))
                            AddGitLine(parent.Value.X, parent.Value.Y, x, y, color, 1);
                        AddGitDot(point, color, true, item, commit.Hash);
                        known[commit.Hash] = point;
                        previous = point;
                    }
                    if (!known.ContainsKey(item.CommitHash))
                    {
                        x += 52;
                        var point = new Point(x, y);
                        if (previous is not null) AddGitLine(previous.Value.X, previous.Value.Y, x, y, color, 2);
                        AddGitDot(point, color, true, item, item.CommitHash);
                        known[item.CommitHash] = point;
                        previous = point;
                    }
                }
                maxX = Math.Max(maxX, x);
            }
        }
        GitGraphCanvas.Width = Math.Max(620, maxX + 90);
        GitGraphCanvas.Height = Math.Max(300, 50 + members.Length * 76);
    }

    private void AddGitLine(double x1, double y1, double x2, double y2, Brush color, double thickness)
    {
        GitGraphCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = color, StrokeThickness = thickness });
    }

    private void AddGitLabel(string label, double left, double top, Brush color)
    {
        var text = new TextBlock { Text = label, Foreground = color, FontWeight = FontWeights.SemiBold,
            Width = 78, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label };
        Canvas.SetLeft(text, left); Canvas.SetTop(text, top);
        GitGraphCanvas.Children.Add(text);
    }

    private void AddGitDot(Point position, Brush color, bool commit, GitProjectEvent item, string hash)
    {
        var ellipse = new Ellipse { Width = 14, Height = 14, Fill = commit ? color : Brushes.White,
            Stroke = color, StrokeThickness = 2, Cursor = Cursors.Hand, ToolTip = $"{item.MemberName} · {hash[..8]}" };
        Canvas.SetLeft(ellipse, position.X - 7); Canvas.SetTop(ellipse, position.Y - 7);
        ellipse.MouseLeftButtonUp += (_, _) =>
        {
            selectedGitEvent = item;
            selectedGitCommit = hash;
            var commitInfo = item.Commits.FirstOrDefault(value => value.Hash == hash);
            GitSelectedPointText.Text = $"{item.MemberName} · {hash[..8]} · " +
                (commitInfo?.Subject ?? (item.Kind == "Joined" ? "加入项目" : item.Kind == "Synced" ? "同步到此提交" : "已发布提交"));
            UpdateGitProjectActions();
        };
        GitGraphCanvas.Children.Add(ellipse);
    }
}

public sealed record GitProjectRow(GitProjectSummary Project)
{
    public string Name => Project.Name;
    public string Summary => $"{(Project.Joined ? "已加入" : "附近项目")} · {Project.MemberCount} 名成员 · 更新于 {Project.LastChangedUtc.ToLocalTime():MM-dd HH:mm}";
}
