using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly Func<Task>? chatPumpOverride;
    private bool chatConversationsCollapsed;
    private bool chatInputComposing;
    private int chatCompositionGeneration;

    private void InitializeChatEditor()
    {
        ApplyChatConversationLayout(store.GetChatConversationsCollapsed());
        TextCompositionManager.AddPreviewTextInputStartHandler(ChatInput, ChatCompositionStarted);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(ChatInput, ChatCompositionStarted);
        TextCompositionManager.AddPreviewTextInputHandler(ChatInput, ChatCompositionCompleted);
        ChatInput.LostKeyboardFocus += (_, _) =>
        {
            chatCompositionGeneration++;
            chatInputComposing = false;
        };
    }

    private void ChatCompositionStarted(object sender, TextCompositionEventArgs e)
    {
        chatCompositionGeneration++;
        chatInputComposing = true;
    }

    private void ChatCompositionCompleted(object sender, TextCompositionEventArgs e)
    {
        if (!chatInputComposing) return;
        var generation = chatCompositionGeneration;
        // Some IMEs finish composition before delivering the confirming Enter. Keep the
        // guard for the remainder of this input dispatch, without swallowing the IME event.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (generation == chatCompositionGeneration) chatInputComposing = false;
        });
    }

    private void ApplyChatConversationLayout(bool collapsed)
    {
        chatConversationsCollapsed = collapsed;
        ChatConversationsColumn.Width = new GridLength(collapsed ? 48 : 270);
        ChatConversationPanel.Padding = new Thickness(collapsed ? 6 : 13);
        ChatConversationHeading.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ChatConversationList.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ChatConversationsToggle.Content = collapsed ? "›" : "‹";
        var label = collapsed ? "展开会话栏" : "收起会话栏";
        ChatConversationsToggle.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(ChatConversationsToggle, label);
        RefreshChatNotifications();
    }

    private void ChatConversationsToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var collapsed = !chatConversationsCollapsed;
            store.SaveChatConversationsCollapsed(collapsed);
            ApplyChatConversationLayout(collapsed);
        }
        catch (Exception ex) { ShowError("保存会话栏显示状态失败", ex); }
    }

    private readonly List<ChatToastWindow> chatToastWindows = [];
    private bool chatRefreshing;
    private bool preparingChatResource;
    private CancellationTokenSource? chatResourceCancellation;
    private Task<ChatMessage>? chatResourcePreparation;
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
        RefreshChatNotifications();
    }

    private void RefreshChatTimeline()
    {
        if (ChatMessageList is null) return;
        ChatMessages.Clear();
        var peerId = SelectedChatPeerId;
        if (peerId is null) { RefreshChatHeader(); return; }
        foreach (var message in store.GetChatMessages(peerId)) ChatMessages.Add(new ChatMessageRow(message, store.GetCachedChatProgress(peerId, message.MessageId),
            store.GetPeerCapabilities(peerId).Contains(NodeDefaults.ChatProgressCapability)));
        if (IsChatForeground)
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
        ChatResourceButton.IsEnabled = canChat && !preparingChatResource;
        ChatMuteButton.IsEnabled = peer is not null || conversation is not null;
        ChatClearButton.IsEnabled = conversation is not null && !preparingChatResource;
        ChatMuteButton.Content = conversation?.MutedUntilUtc > DateTimeOffset.UtcNow ? "取消静音" : "静音 1 小时";
    }

    private void ChatConversation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!chatRefreshing) RefreshChatTimeline();
    }

    private void ChatMessage_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ChatInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HandleChatInputKey(e.Key, Keyboard.Modifiers, e.IsRepeat)) e.Handled = true;
    }

    internal bool HandleChatInputKey(Key key, ModifierKeys modifiers, bool repeated)
    {
        // ImeProcessed and composing Enter belong to the input method. Shift+Enter
        // remains unhandled so the multiline TextBox inserts its normal newline.
        if (key != Key.Enter || chatInputComposing || modifiers != ModifierKeys.None) return false;
        if (!repeated) ChatSend_Click(ChatInput, new RoutedEventArgs());
        return true;
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        var peerId = SelectedChatPeerId;
        if (peerId is null || !ChatSendButton.IsEnabled || chatInputComposing || string.IsNullOrWhiteSpace(ChatInput.Text)) return;
        try
        {
            chat.QueueText(peerId, ChatInput.Text);
            ChatInput.Clear();
            RefreshChatTimeline();
            await PumpChatSafeAsync();
        }
        catch (Exception ex) { ShowError("发送聊天消息失败", ex); }
    }

    private void ChatResource_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = ChatResourceButton };
        foreach (var mode in new[] { PublishMode.Reference, PublishMode.Copy })
        foreach (var folder in new[] { false, true })
        {
            var label = mode == PublishMode.Reference ? "引用（不占副本空间）" : "副本（复制到缓存）";
            var item = new System.Windows.Controls.MenuItem { Header = $"私发{(folder ? "文件夹" : "文件")} · {label}…" };
            item.Click += (_, _) => SendPrivateChatResource(folder, mode);
            menu.Items.Add(item);
        }
        var published = new System.Windows.Controls.MenuItem { Header = "发送已发布资源卡片…" };
        published.Click += ChatPublishedResource_Click;
        menu.Items.Add(new Separator());
        menu.Items.Add(published);
        var cleanup = new System.Windows.Controls.MenuItem { Header = "清理中断残留（保留有效附件）" };
        cleanup.Click += ChatCleanup_Click;
        menu.Items.Add(new Separator());
        menu.Items.Add(cleanup);
        menu.IsOpen = true;
    }

    private async void SendPrivateChatResource(bool folder, PublishMode mode)
    {
        var peerId = SelectedChatPeerId;
        if (peerId is null || preparingChatResource || exiting) return;
        if (!store.GetPeerCapabilities(peerId).Contains(NodeDefaults.PrivateResourceCapability, StringComparer.Ordinal))
        {
            SetStatus("对方版本不支持私发资源，请先升级到 0.4.4 或更新版本并刷新连接。");
            return;
        }
        string path;
        if (folder)
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "选择私发文件夹（不会公开发布）" };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            path = picker.SelectedPath;
        }
        else
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择私发文件（不会公开发布）", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            path = picker.FileName;
        }
        preparingChatResource = true;
        chatResourceCancellation = new CancellationTokenSource();
        ChatPreparationPanel.Visibility = Visibility.Visible;
        ChatPreparationText.Text = mode == PublishMode.Copy ? "正在统计并复制，可随时取消…" : "正在创建引用…";
        var lastProgress = DateTime.MinValue;
        var progress = new Progress<long>(bytes =>
        {
            if ((DateTime.UtcNow - lastProgress).TotalMilliseconds < 150) return;
            lastProgress = DateTime.UtcNow;
            ChatPreparationText.Text = $"已复制 {bytes / 1048576.0:F1} MiB";
        });
        RefreshChatHeader();
        SetStatus(mode == PublishMode.Copy ? "正在准备私发副本…" : "引用原路径；移动或删除原文件后对方将无法下载。");
        try
        {
            chatResourcePreparation = Task.Run(() => chat.QueuePrivateResource(peerId, path, mode, chatResourceCancellation.Token, progress));
            await chatResourcePreparation;
            if (exiting) return;
            RefreshChatTimeline();
            SetStatus("私发资源已加入聊天队列，不会出现在公开发布中。");
            await PumpChatSafeAsync();
        }
        catch (OperationCanceledException) { SetStatus("已取消私发准备，未完成副本已清理。"); }
        catch (Exception ex) { ShowError("私发资源失败", ex); }
        finally
        {
            preparingChatResource = false; chatResourcePreparation = null;
            chatResourceCancellation?.Dispose(); chatResourceCancellation = null;
            ChatPreparationPanel.Visibility = Visibility.Collapsed;
            RefreshChatHeader();
        }
    }

    private async void ChatPublishedResource_Click(object sender, RoutedEventArgs e)
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

    private void ChatPreparationCancel_Click(object sender, RoutedEventArgs e) => chatResourceCancellation?.Cancel();

    private async void ChatCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (preparingChatResource) { SetStatus("请先取消或等待当前私发准备。"); return; }
        try { var count = await Task.Run(store.CleanOrphanPrivateCopies); SetStatus($"已清理 {count} 个无记录的私发残留；有效附件保持可下载。"); }
        catch (Exception ex) { ShowError("清理残留失败", ex); }
    }

    private async void ChatCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessageRow row) return;
        if (row.Message.Kind == "PrivateResource" && System.Windows.MessageBox.Show(this,
            "撤销后对方不能继续下载；本机副本将删除，引用原文件保持不变。已被对方下载的文件不会删除。", "撤销私发并清理",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { await Task.Run(() => chat.Cancel(row.Message.PeerId, row.Message.MessageId)); RefreshChatTimeline(); }
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
            var resource = await client.GetResourceAsync(peer, row.Message.ResourceId);
            if (resource is null || !resource.Available) { SetStatus("资源已撤销或原文件不可用。"); return; }
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
        if (System.Windows.MessageBox.Show("清空此会话在本机的全部消息及私发副本？对方的聊天记录和已下载文件会保留，但无法再下载这些私发资源，未发送消息也会清除。",
                "清空本机聊天记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            store.ClearChatConversation(peerId);
            RefreshChatTimeline();
            RefreshChatConversations();
        }
        catch (Exception ex) { ShowError("清空聊天记录失败", ex); }
    }

    private void OnChatReceived(ChatMessage message)
    {
        if (exiting) return;
        var active = IsChatForeground && SelectedChatPeerId == message.PeerId;
        if (active)
        {
            store.MarkChatRead(message.PeerId);
            RefreshChatTimeline();
        }
        RefreshChatConversations();
        var muted = store.GetChatConversations().FirstOrDefault(item => item.PeerId == message.PeerId)?.MutedUntilUtc > DateTimeOffset.UtcNow;
        if (!active && !muted)
        {
            if (chatNotificationServices is { } services) services.ShowToast(message);
            else if (chatDesktopIntegration) ShowChatToast(message);
        }
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
        var toast = new ChatToastWindow(name, message.Kind is "Resource" or "PrivateResource"
            ? $"分享了资源：{message.ResourceName}" : message.Text ?? "新消息");
        chatToastWindows.Add(toast);
        toast.Loaded += (_, _) => PositionReminderWindows();
        toast.Closed += (_, _) => { chatToastWindows.Remove(toast); PositionReminderWindows(); };
        toast.OpenRequested += async (_, _) =>
        {
            if (await OpenChatNotificationAsync(message.PeerId)) toast.Close();
        };
        toast.Show();
    }

    private async Task PumpChatSafeAsync()
    {
        try
        {
            if (chatPumpOverride is { } pump) await pump();
            else await chat.PumpAsync(updateCancellation.Token);
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { AppLog.Write("聊天队列发送失败", ex); }
    }
}

public sealed record ChatConversationRow(string PeerId, string Name, string Status, long Unread)
{
    public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
}

public sealed record ChatMessageRow(ChatMessage Message, ChatProgressReceipt? Progress = null, bool SupportsProgress = false)
{
    public string Author => Message.Outgoing ? "我" : "对方";
    public string Time => Message.SentUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string Body => Message.Kind switch
    {
        "Resource" => $"📦 资源卡片：{Message.ResourceName ?? Message.ResourceId}",
        "PrivateResource" => $"📎 私发资源：{Message.ResourceName ?? "资源"}",
        _ => Message.Text ?? ""
    };
    public string StateText => Message.Outgoing ? Message.State switch
    {
        "Queued" => "排队中" + (Message.Error is null ? "" : $" · {Message.Error}"),
        "Sending" => "发送中",
        "Delivered" => Progress is null ? "已送达 · " + (SupportsProgress ? "等待状态回执" : "对方版本不支持已读/下载回执")
            : $"已送达 · {(Progress.Read ? "已读" : "未读")}" +
              (Progress.DownloadState is null ? "" : $" · 附件{Progress.DownloadState}" +
                  (Progress.DownloadState == "下载中" && Progress.TotalBytes > 0 ? $" {100.0 * Progress.DownloadedBytes / Progress.TotalBytes:F0}%" : "")) +
              $" · 上次回执 {Progress.ObservedUtc.ToLocalTime():HH:mm:ss}",
        "Failed" => "失败" + (Message.Error is null ? "" : $" · {Message.Error}"),
        "Canceled" => "已取消",
        _ => Message.State
    } : "";
    public string CancelLabel => Message.Kind == "PrivateResource" ? "撤销并清理" : "取消发送";
    public Visibility CancelVisibility => Message.Outgoing && Message.State != "Canceled" && (Message.State == "Queued" || Message.Kind == "PrivateResource") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => Message.Outgoing && Message.State == "Failed" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadVisibility => !Message.Outgoing && Message.Kind is "Resource" or "PrivateResource" ? Visibility.Visible : Visibility.Collapsed;
}
