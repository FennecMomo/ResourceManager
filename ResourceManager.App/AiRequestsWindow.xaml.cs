using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class AiRequestsWindow : Window
{
    private readonly NodeStore store;
    private readonly AiRequestService service;
    private sealed record RequestRow(AiSearchSession Session, string Label);
    private sealed record ScopeRow(AiSearchScope Scope, string Label);
    public AiRequestsWindow(NodeStore store, AiRequestService service)
    {
        this.store = store; this.service = service;
        InitializeComponent(); Refresh();
    }
    private static string StateName(string state) => state switch
    {
        "AwaitingApproval" => "等待授权", "Running" => "正在查找", "Sending" => "准备文件副本", "Ready" => "结果可用",
        "Failed" => "失败", "Rejected" => "已拒绝", "Canceled" => "已取消", "Expired" => "已过期", _ => state
    };
    public void Refresh()
    {
        var selectedId = (Requests.SelectedItem as RequestRow)?.Session.Id;
        var scopeId = (Scopes.SelectedItem as ScopeRow)?.Scope.Id;
        var deviceId = (Devices.SelectedItem as PeerInfo)?.DeviceId;
        CliPath.Text = store.GetAiCodexExecutable();
        Requests.ItemsSource = service.ListLocal().Select(s => new RequestRow(s,
            $"{StateName(s.State)} · {store.GetPeer(s.DeviceId)?.Nickname ?? s.DeviceId}\n{s.Question[..Math.Min(s.Question.Length, 38)]}")).ToArray();
        Requests.SelectedItem = Requests.Items.Cast<RequestRow>().FirstOrDefault(r => r.Session.Id == selectedId) ?? Requests.Items.Cast<RequestRow>().FirstOrDefault();
        Scopes.ItemsSource = store.GetAiSearchScopes().Select(s => new ScopeRow(s,
            $"{store.GetPeer(s.DeviceId)?.Nickname ?? s.DeviceId} · {(s.AutoApprove ? "免逐次授权" : "每次询问")} · {s.Directory}\n授权 ID：{s.Id}")).ToArray();
        Scopes.SelectedItem = Scopes.Items.Cast<ScopeRow>().FirstOrDefault(s => s.Scope.Id == scopeId);
        Devices.ItemsSource = store.GetPeers();
        Devices.SelectedItem = Devices.Items.Cast<PeerInfo>().FirstOrDefault(p => p.DeviceId == deviceId) ?? Devices.Items.Cast<PeerInfo>().FirstOrDefault();
        UpdateDetails();
    }
    private void UpdateDetails()
    {
        if (Requests.SelectedItem is not RequestRow row) { Details.Text = "暂无请求"; ApproveButton.IsEnabled = false; return; }
        var session = row.Session;
        Details.Text = $"请求方：{store.GetPeer(session.DeviceId)?.Nickname ?? session.DeviceId}\n设备 ID：{session.DeviceId}\n状态：{StateName(session.State)}\n已批准目录：{session.Directory ?? "尚未授权"}\n到期：{session.ExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n\n{session.Question}\n\n{session.Answer}\n{session.Error}\n\n" +
            string.Join("\n", (session.Files ?? []).Select(f => $"{f.RelativePath} · {f.Size:N0} 字节{(f.MessageId is null ? "" : " · 已加入私发队列")}"));
        var oldId = session.ScopeId ?? (ApprovalScopes.SelectedItem as AiSearchScope)?.Id;
        ApprovalScopes.ItemsSource = store.GetAiSearchScopes().Where(s => s.DeviceId == session.DeviceId).ToArray();
        ApprovalScopes.SelectedItem = ApprovalScopes.Items.Cast<AiSearchScope>().FirstOrDefault(s => s.Id == oldId) ?? ApprovalScopes.Items.Cast<AiSearchScope>().FirstOrDefault();
        ApproveButton.IsEnabled = session.State == "AwaitingApproval";
    }
    private void Run(Action action)
    {
        try { action(); Refresh(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Request_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Details is not null) UpdateDetails(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void ChooseCli_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择已安装的 codex.exe", Filter = "Codex CLI|codex.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) Run(() => { store.SetAiCodexExecutable(dialog.FileName); Status.Text = "已保存 CLI 路径；请确保该 CLI 已由你在本机完成登录。"; });
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择允许此设备查找的目录" };
        if (dialog.ShowDialog(this) == true) FolderPath.Text = dialog.FolderName;
    }
    private void SaveScope_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Devices.SelectedItem is not PeerInfo peer) throw new InvalidOperationException("请先选择设备。");
        store.SaveAiSearchScope(peer.DeviceId, FolderPath.Text, AutoApprove.IsChecked == true);
        Status.Text = "目录授权已保存；未勾选免授权时，仍须逐次允许请求。";
    });
    private void RemoveScope_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Scopes.SelectedItem is ScopeRow row) { service.RemoveScope(row.Scope.Id); Status.Text = "授权已撤销，对应运行中的请求已取消。"; }
    });
    private void Approve_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Requests.SelectedItem is not RequestRow row || ApprovalScopes.SelectedItem is not AiSearchScope scope)
            throw new InvalidOperationException("请先在“目录授权”为此设备配置一个文件夹。");
        service.Approve(row.Session.Id, scope.Id); Status.Text = "已允许，本次请求正在后台查找。";
    });
    private void Cancel_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Requests.SelectedItem is RequestRow row) service.Cancel(row.Session.Id, row.Session.State == "AwaitingApproval");
    });
}
