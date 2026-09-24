using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly List<ChatToastWindow> chatToastWindows = [];
    private bool chatRefreshing;
    public ObservableCollection<ChatConversationRow> ChatConversations { get; } = [];
    public ObservableCollection<ChatMessageRow> ChatMessages { get; } = [];

    private string? SelectedChatPeerId => (ChatConversationList?.SelectedItem as ChatConversationRow)?.PeerId;

    private void RefreshChatConversations()
    {
        if (ChatConversationList is null) return;
        var selected = SelectedChatPeerId;
        var stored = store.GetChatConversations().ToDictionary(item => item.PeerId, StringComparer.Ordinal);
        var peers = store.GetPeers().ToDictionary(item => item.DeviceId, StringComparer.Ordinal);
        chatRefreshing = true;
        try
        {
            ChatConversations.Clear();
            foreach (var conversation in stored.Values.OrderByDescending(item => item.LastMessageUtc))
            {
                var peer = peers.GetValueOrDefault(conversation.PeerId);
                ChatConversations.Add(new ChatConversationRow(conversation.PeerId,
                    peer?.Nickname ?? conversation.Nickname,
                    conversation.Removed ? "已移除设备" : peerStatus.GetValueOrDefault(conversation.PeerId, "未检查"),
                    conversation.Unread));
            }
            foreach (var peer in peers.Values.Where(item => !stored.ContainsKey(item.DeviceId)))
                ChatConversations.Add(new ChatConversationRow(peer.DeviceId, peer.Nickname,
                    peerStatus.GetValueOrDefault(peer.DeviceId, "未检查"), 0));
            ChatConversationList.SelectedItem = ChatConversations.FirstOrDefault(item => item.PeerId == selected);
        }
        finally { chatRefreshing = false; }
        RefreshChatHeader();
    }

    private void RefreshChatTimeline()
    {
        if (ChatMessageList is null) return;
        ChatMessages.Clear();
        var peerId = SelectedChatPeerId;
        if (peerId is null) { RefreshChatHeader(); return; }
        foreach (var message in store.GetChatMessages(peerId)) ChatMessages.Add(new ChatMessageRow(message));
        if (Tabs.SelectedIndex == 3 && IsVisible && WindowState != WindowState.Minimized && IsActive)
        {
            store.MarkChatRead(peerId);
            RefreshChatConversations();
        }
        if (ChatMessages.Count > 0) ChatMessageList.ScrollIntoView(ChatMessages[^1]);
        RefreshChatHeader();
    }

    private void RefreshChatHeader()
    {
        if (ChatPeerTitle is null) return;
        var peerId = SelectedChatPeerId;
        var peer = peerId is null ? null : store.GetPeer(peerId);
        var conversation = peerId is null ? null : store.GetChatConversations().FirstOrDefault(item => item.PeerId == peerId);
        var canChat = peer is not null && store.GetPeerCapabilities(peerId!).Contains(NodeDefaults.ChatCapability,
            StringComparer.Ordinal);
        ChatPeerTitle.Text = peer?.Nickname ?? conversation?.Nickname ?? "选择一个设备开始聊天";
        ChatPeerHint.Text = peer is null
            ? conversation is null ? "只对支持 0.3.7 聊天的设备开放" : "设备已移除；重新连接相同设备 ID 后可恢复会话"
            : canChat ? $"{peerStatus.GetValueOrDefault(peer.DeviceId, "未检查")} · 离线时消息在本机排队"
                : "此设备尚不支持 0.3.7 聊天";
        ChatSendButton.IsEnabled = canChat;
        ChatResourceButton.IsEnabled = canChat;
        ChatMuteButton.IsEnabled = peer is not null || conversation is not null;
        ChatClearButton.IsEnabled = conversation is not null;
        ChatMuteButton.Content = conversation?.MutedUntilUtc > DateTimeOffset.UtcNow ? "取消静音" : "静音 1 小时";
    }

    private void ChatConversation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!chatRefreshing) RefreshChatTimeline();
    }

    private void ChatMessage_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        ChatSend_Click(sender, new RoutedEventArgs());
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        var peerId = SelectedChatPeerId;
        if (peerId is null) return;
        try
        {
            chat.QueueText(peerId, ChatInput.Text);
            ChatInput.Clear();
            RefreshChatTimeline();
            await PumpChatSafeAsync();
        }
        catch (Exception ex) { ShowError("发送聊天消息失败", ex); }
    }

    private async void ChatResource_Click(object sender, RoutedEventArgs e)
    {
        var peerId = SelectedChatPeerId;
        if (peerId is null) return;
        var available = catalog.List().Where(item => item.Available).ToArray();
        if (available.Length == 0) { SetStatus("没有可用的已发布资源。"); return; }
        var picker = new Window
        {
            Owner = this, Icon = Icon, Title = "发送资源卡片", Width = 430, Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(246, 248, 251))
        };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var button = new System.Windows.Controls.Button { Content = "发送资源卡片", Margin = new Thickness(0, 12, 0, 0), Height = 38,
            IsEnabled = false, Style = (Style)FindResource("BaseButton") };
        DockPanel.SetDock(button, Dock.Bottom);
        panel.Children.Add(button);
        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = available, DisplayMemberPath = "Name", BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            ItemContainerStyle = (Style)FindResource("CardItem")
        };
        list.SelectionChanged += (_, _) => button.IsEnabled = list.SelectedItem is not null;
        button.Click += (_, _) => { picker.DialogResult = true; picker.Close(); };
        panel.Children.Add(list);
        picker.Content = panel;
        if (picker.ShowDialog() != true || list.SelectedItem is not RemoteResource resource) return;
        try
        {
            chat.QueueResource(peerId, resource.Id);
            RefreshChatTimeline();
            await PumpChatSafeAsync();
        }
        catch (Exception ex) { ShowError("发送资源卡片失败", ex); }
    }

    private void ChatCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessageRow row) return;
        try { chat.Cancel(row.Message.PeerId, row.Message.MessageId); RefreshChatTimeline(); }
        catch (Exception ex) { ShowError("取消发送失败", ex); }
    }

    private async void ChatRetry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessageRow row) return;
        try
        {
            chat.Retry(row.Message.PeerId, row.Message.MessageId);
            RefreshChatTimeline();
            await PumpChatSafeAsync();
        }
        catch (Exception ex) { ShowError("重试发送失败", ex); }
    }

    private async void ChatDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessageRow row ||
            row.Message.Outgoing || row.Message.ResourceId is null) return;
        var peer = store.GetPeer(row.Message.PeerId);
        if (peer is null) { SetStatus("该设备已移除，重新连接后才能下载资源。"); return; }
        try
        {
            var resources = await client.GetResourcesAsync(peer);
            var resource = resources.FirstOrDefault(item => item.Id == row.Message.ResourceId && item.Available);
            if (resource is null) { SetStatus("资源已撤销或原文件不可用。"); return; }
            BeginDownload(peer, resource);
        }
        catch (Exception ex) { ShowError("核对资源状态失败", ex); }
    }

    private void ChatMute_Click(object sender, RoutedEventArgs e)
    {
        var peerId = SelectedChatPeerId;
        if (peerId is null) return;
        var peer = store.GetPeer(peerId);
        if (peer is not null) store.EnsureChatConversation(peerId, peer.Nickname);
        var muted = store.GetChatConversations().FirstOrDefault(item => item.PeerId == peerId)?.MutedUntilUtc > DateTimeOffset.UtcNow;
        store.SetChatMutedUntil(peerId, muted ? null : DateTimeOffset.UtcNow.AddHours(1));
        RefreshChatConversations();
        RefreshChatHeader();
    }

    private void ChatClear_Click(object sender, RoutedEventArgs e)
    {
        var peerId = SelectedChatPeerId;
        if (peerId is null) return;
        if (System.Windows.MessageBox.Show("清空此会话在本机的全部消息？不会删除对方的记录，未发送消息也会清除。",
                "清空本机聊天记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        store.ClearChatConversation(peerId);
        RefreshChatTimeline();
        RefreshChatConversations();
    }

    private void OnChatReceived(ChatMessage message)
    {
        if (exiting) return;
        var active = Tabs.SelectedIndex == 3 && SelectedChatPeerId == message.PeerId && IsVisible &&
                     WindowState != WindowState.Minimized && IsActive;
        if (active)
        {
            store.MarkChatRead(message.PeerId);
            RefreshChatTimeline();
        }
        RefreshChatConversations();
        var muted = store.GetChatConversations().FirstOrDefault(item => item.PeerId == message.PeerId)?.MutedUntilUtc > DateTimeOffset.UtcNow;
        if (!active && !muted) ShowChatToast(message);
    }

    private void OnChatChanged(ChatMessage message)
    {
        if (exiting) return;
        RefreshChatConversations();
        if (SelectedChatPeerId == message.PeerId) RefreshChatTimeline();
    }

    private void ShowChatToast(ChatMessage message)
    {
        var name = store.GetPeer(message.PeerId)?.Nickname ??
                   store.GetChatConversations().FirstOrDefault(item => item.PeerId == message.PeerId)?.Nickname ?? "设备";
        var toast = new ChatToastWindow(name, message.Kind == "Resource"
            ? $"分享了资源：{message.ResourceName}" : message.Text ?? "新消息");
        chatToastWindows.Add(toast);
        toast.Loaded += (_, _) => PositionReminderWindows();
        toast.Closed += (_, _) => { chatToastWindows.Remove(toast); PositionReminderWindows(); };
        toast.OpenRequested += (_, _) =>
        {
            ShowWindow();
            Tabs.SelectedIndex = 3;
            RefreshChatConversations();
            ChatConversationList.SelectedItem = ChatConversations.FirstOrDefault(item => item.PeerId == message.PeerId);
            RefreshChatTimeline();
            toast.Close();
        };
        toast.Show();
    }

    private async Task PumpChatSafeAsync()
    {
        try { await chat.PumpAsync(updateCancellation.Token); }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { AppLog.Write("聊天队列发送失败", ex); }
    }
}

public sealed record ChatConversationRow(string PeerId, string Name, string Status, long Unread)
{
    public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record ChatMessageRow(ChatMessage Message)
{
    public string Author => Message.Outgoing ? "我" : "对方";
    public string Time => Message.SentUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string Body => Message.Kind == "Resource" ? $"📦 资源卡片：{Message.ResourceName ?? Message.ResourceId}" : Message.Text ?? "";
    public string StateText => Message.Outgoing ? Message.State switch
    {
        "Queued" => "排队中" + (Message.Error is null ? "" : $" · {Message.Error}"),
        "Sending" => "发送中",
        "Delivered" => "已送达",
        "Failed" => "失败" + (Message.Error is null ? "" : $" · {Message.Error}"),
        "Canceled" => "已取消",
        _ => Message.State
    } : "";
    public Visibility CancelVisibility => Message.Outgoing && Message.State == "Queued" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => Message.Outgoing && Message.State == "Failed" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadVisibility => !Message.Outgoing && Message.Kind == "Resource" ? Visibility.Visible : Visibility.Collapsed;
}
