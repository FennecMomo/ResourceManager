using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly ChatNotificationServices? chatNotificationServices;
    private readonly bool chatDesktopIntegration;
    private readonly DispatcherTimer chatNotificationTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private System.Drawing.Icon? chatUnreadIcon;
    private bool chatNotificationsInitialized, chatFlashing, chatFlashLit, openingChatNotification;
    private long chatUnreadCount;
    private DateTimeOffset? nextChatMuteExpiry;
    private bool IsChatForeground => Tabs.SelectedIndex == 3 &&
        (chatNotificationServices?.IsForeground() ?? (IsVisible && IsActive && WindowState != WindowState.Minimized));

    private void InitializeChatNotifications()
    {
        if (chatDesktopIntegration) chatUnreadIcon = CreateUnreadIcon(appIcon);
        chatNotificationsInitialized = true;
        chatNotificationTimer.Tick += (_, _) => TickChatNotification();
        Activated += (_, _) => ReadVisibleChat();
        StateChanged += (_, _) => ReadVisibleChat();
    }

    private void ReadVisibleChat()
    {
        if (exiting || openingChatNotification || !IsChatForeground || SelectedChatPeerId is null) return;
        RefreshChatTimeline();
    }

    private void RefreshChatNotifications()
    {
        if (!chatNotificationsInitialized || exiting) return;
        var conversations = store.GetChatConversations();
        chatUnreadCount = conversations.Sum(c => c.Unread);
        var now = DateTimeOffset.UtcNow;
        var shouldFlash = conversations.Any(c => c.Unread > 0 && !(c.MutedUntilUtc > now));
        nextChatMuteExpiry = conversations.Where(c => c.Unread > 0 && c.MutedUntilUtc > now)
            .Select(c => c.MutedUntilUtc).Min();
        ChatUnreadBadge.Visibility = chatUnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatUnreadText.Text = chatUnreadCount > 99 ? "99+" : chatUnreadCount.ToString();
        ChatCollapsedUnreadDot.Visibility = chatUnreadCount > 0 && chatConversationsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        var label = chatConversationsCollapsed ? "展开会话栏" : "收起会话栏";
        ChatConversationsToggle.ToolTip = chatUnreadCount > 0 ? $"{label} · {chatUnreadCount} 条未读消息" : label;
        if (chatFlashing != shouldFlash) chatFlashLit = shouldFlash;
        chatFlashing = shouldFlash;
        UpdateChatTrayIcon();
        if (chatFlashing || nextChatMuteExpiry is not null) chatNotificationTimer.Start();
        else chatNotificationTimer.Stop();
    }

    private void TickChatNotification()
    {
        if (exiting) return;
        if (nextChatMuteExpiry <= DateTimeOffset.UtcNow)
        {
            RefreshChatNotifications();
            RefreshChatHeader();
        }
        if (!chatFlashing) return;
        chatFlashLit = !chatFlashLit;
        UpdateChatTrayIcon();
    }

    private void UpdateChatTrayIcon()
    {
        var text = chatUnreadCount == 0 ? "资源管理器" : $"资源管理器 · {chatUnreadCount} 条未读消息";
        var lit = chatFlashing && chatFlashLit;
        if (chatNotificationServices is { } services) services.UpdateTray(lit, text);
        else
        {
            tray.Icon = lit ? chatUnreadIcon ?? appIcon : appIcon;
            tray.Text = text;
        }
    }

    internal async Task OpenUnreadChatAsync()
    {
        var peer = store.GetLatestUnreadChatPeer();
        if (peer is not null) await OpenChatNotificationAsync(peer);
        else ActivateForChatNotification();
    }

    private void ActivateForChatNotification()
    {
        if (chatNotificationServices is { } services) services.ActivateWindow();
        else if (chatDesktopIntegration) ShowWindow();
    }

    internal async Task<bool> OpenChatNotificationAsync(string peerId)
    {
        if (openingChatNotification || exiting) return false;
        openingChatNotification = true;
        try
        {
            ActivateForChatNotification();
            // Await the settings draft decision; a canceled/failed save must never select
            // a hidden chat or mark its messages read behind the settings page.
            if (!await CanNavigateFromSettingsAsync(3)) return false;
            SetSettingsNavigationPage(3);
            currentPageIndex = 3;
            UpdatePageHeader();
            RefreshChatConversations();
            ChatConversationList.SelectedItem = ChatConversations.FirstOrDefault(c => c.PeerId == peerId);
            RefreshChatTimeline();
            return true;
        }
        finally { openingChatNotification = false; }
    }

    private static System.Drawing.Icon CreateUnreadIcon(System.Drawing.Icon original)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.DrawIcon(original, new System.Drawing.Rectangle(0, 0, 32, 32));
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(225, 55, 69));
            graphics.FillEllipse(white, 18, 0, 14, 14);
            graphics.FillEllipse(red, 20, 2, 10, 10);
        }
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyChatIcon(handle); }
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyChatIcon(IntPtr icon);
}

internal sealed record ChatNotificationServices(Func<bool> IsForeground, Action ActivateWindow,
    Action<ChatMessage> ShowToast, Action<bool, string> UpdateTray);
