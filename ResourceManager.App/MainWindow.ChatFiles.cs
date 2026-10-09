using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private bool CanSendPrivateFiles(string? peerId) => !exiting && !preparingChatResource &&
        peerId is not null && store.GetPeer(peerId) is not null &&
        store.GetPeerCapabilities(peerId).Contains(NodeDefaults.PrivateResourceCapability, StringComparer.Ordinal);

    private static string[] ChatDropPaths(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return [];
        return data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths
            ? paths.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }

    private void ChatFiles_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        // Leave ordinary text editing alone, including dragging selected text inside the composer.
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = CanSendPrivateFiles(SelectedChatPeerId) && ChatDropPaths(e.Data).Length > 0 &&
            (e.AllowedEffects & System.Windows.DragDropEffects.Copy) != 0
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
    }

    private void ChatFiles_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = System.Windows.DragDropEffects.None;
        var peerId = SelectedChatPeerId;
        if (!CanSendPrivateFiles(peerId)) { SetStatus("请先选择支持私发的设备，并等待当前准备完成。"); return; }
        var paths = ChatDropPaths(e.Data);
        if (paths.Length == 0) return;
        // Freeze both recipient and paths here; changing conversations while the menu is open cannot redirect a batch.
        var menu = CreatePrivateDropMenu(peerId!, paths);
        menu.PlacementTarget = ChatDropZone;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Effects = System.Windows.DragDropEffects.Copy;
    }

    private System.Windows.Controls.ContextMenu CreatePrivateDropMenu(string peerId, string[] paths)
    {
        paths = paths.ToArray();
        var menu = new System.Windows.Controls.ContextMenu();
        var name = store.GetPeer(peerId)?.Nickname ?? peerId;
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = $"向 {name} 私发 {paths.Length} 项", IsEnabled = false });
        foreach (var mode in new[] { PublishMode.Reference, PublishMode.Copy })
        {
            var item = new System.Windows.Controls.MenuItem { Header = mode == PublishMode.Reference
                ? "以引用发送（不占副本空间）" : "以副本发送（复制到缓存）" };
            item.Click += async (_, _) => await SendPrivateChatBatchAsync(peerId, paths, mode);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "取消" });
        return menu;
    }

    private async Task SendPrivateChatBatchAsync(string peerId, string[] paths, PublishMode mode)
    {
        if (!CanSendPrivateFiles(peerId) || !Enum.IsDefined(mode)) return;
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        preparingChatResource = true;
        using var cancellation = new CancellationTokenSource();
        chatResourceCancellation = cancellation;
        ChatPreparationPanel.Visibility = Visibility.Visible;
        RefreshChatHeader();
        var currentItem = -1;
        var sent = 0;
        var canceled = false;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < paths.Length; i++)
            {
                if (exiting || cancellation.IsCancellationRequested) { canceled = true; break; }
                currentItem = i;
                var itemIndex = i;
                var path = paths[i];
                var label = $"{i + 1}/{paths.Length} · {Path.GetFileName(path)}";
                ChatPreparationText.Text = label + (mode == PublishMode.Copy ? " · 正在统计并复制…" : " · 正在创建引用…");
                var lastProgress = DateTime.MinValue;
                var progress = new Progress<long>(bytes =>
                {
                    if (!ReferenceEquals(chatResourceCancellation, cancellation) || currentItem != itemIndex ||
                        cancellation.IsCancellationRequested || !preparingChatResource ||
                        (DateTime.UtcNow - lastProgress).TotalMilliseconds < 150) return;
                    lastProgress = DateTime.UtcNow;
                    ChatPreparationText.Text = $"{label} · 已复制 {bytes / 1048576.0:F1} MiB";
                });
                try
                {
                    chatResourcePreparation = Task.Run(() => chat.QueuePrivateResource(peerId, path, mode, cancellation.Token, progress));
                    await chatResourcePreparation;
                    sent++;
                }
                catch (OperationCanceledException) { canceled = true; break; }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)}：{ex.Message}");
                    AppLog.Write($"批量私发失败：{path}", ex);
                }
            }
            if (!exiting)
            {
                RefreshChatConversations();
                if (SelectedChatPeerId == peerId) RefreshChatTimeline();
                SetStatus($"私发：{sent} 项已入队，{failures.Count} 项失败" +
                    (canceled ? "；已取消后续准备，已入队项目保留。" : "。") +
                    (failures.Count == 0 ? "" : string.Join("；", failures.Take(3))));
            }
        }
        finally
        {
            preparingChatResource = false; chatResourcePreparation = null;
            chatResourceCancellation = null;
            ChatPreparationPanel.Visibility = Visibility.Collapsed;
            RefreshChatHeader();
        }
        if (!exiting && sent > 0) await PumpChatSafeAsync();
    }
}
