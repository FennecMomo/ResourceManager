using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        NodeDefaults.UseDataDirectory(Path.GetFullPath("dist/notification-checks/data-" + Guid.NewGuid().ToString("N")));
        var store = new NodeStore();
        foreach (var peer in new[] { "a", "b" })
        {
            store.UpsertPeer(new PeerInfo(peer, "127.0.0.1", 9, "隔离设备 " + peer, null, null));
            store.SavePeerCapabilities(peer, [NodeDefaults.ChatCapability]);
        }
        ChatMessage Receive(string peer, string? id = null)
        {
            var messageId = id ?? Guid.NewGuid().ToString("N");
            store.AcceptChatMessage(new ChatMessageRequest("1", messageId, peer, store.GetSettings().Profile.DeviceId, DateTimeOffset.UtcNow, "Text", "测试未读消息"), peer, null, DateTimeOffset.UtcNow);
            return store.GetChatMessage(peer, messageId, false)!;
        }
        Receive("a");
        Receive("b");
        store.SetChatMutedUntil("b", DateTimeOffset.UtcNow.AddHours(1));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var foreground = false;
        var activations = 0;
        var toastCount = 0;
        var iconStates = new List<(bool Lit, string Text)>();
        var choice = SettingsLeaveChoice.KeepEditing;
        var services = new SettingsEditorServices(() => false, _ => { }, (_, _) => Task.FromResult(true), _ => choice,
            (message, error) => throw new Exception(message, error));
        var notifications = new ChatNotificationServices(() => foreground, () => { activations++; foreground = true; },
            _ => toastCount++, (lit, text) => iconStates.Add((lit, text)));
        var window = new MainWindow(false, services, false, () => Task.CompletedTask, notifications);
                var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                T Control<T>(string id) => (T)window.FindName(id);
                object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
                long Unread(string peer) => store.GetChatConversations().Single(c => c.PeerId == peer).Unread;
                var nav = Control<ListBox>("NavList");
                var list = Control<ListBox>("ChatConversationList");
                var badge = Control<Border>("ChatUnreadBadge");
                var total = Control<TextBlock>("ChatUnreadText");
                void Refresh() => Call("RefreshChatConversations");
                void Select(string peer) => list.SelectedItem = window.ChatConversations.Single(c => c.PeerId == peer);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var originalIcon = (System.Drawing.Icon)typeof(MainWindow).GetField("appIcon", Private)!.GetValue(window)!;
                using (var unreadIcon = (System.Drawing.Icon)typeof(MainWindow).GetMethod("CreateUnreadIcon", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [originalIcon])!)
                using (var bitmap = unreadIcon.ToBitmap())
                {
                    var red = 0;
                    for (var x = 18; x < 32; x++) for (var y = 0; y < 14; y++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.R > 180 && pixel.G < 100 && pixel.B < 120) red++;
                    }
                    Require(red > 30, "generated tray icon contains visible red unread dot");
                }
                Require(total.Text == "2" && badge.Visibility == Visibility.Visible && iconStates.Last().Lit, "startup restores unread badges and unmuted flashing");
                Require(toastCount == 0 && activations == 0, "startup does not replay popups or activate window");
                Require(new NodeStore().GetChatConversations().Sum(c => c.Unread) == 2, "unread survives database reopen");
                Call("TickChatNotification");
                Require(!iconStates.Last().Lit, "flash alternates to normal icon");
                Call("TickChatNotification");
                Require(iconStates.Last().Lit && iconStates.Last().Text.Contains("2"), "flash returns to red icon with unread tooltip");
                Control<Button>("ChatConversationsToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Control<Border>("ChatCollapsedUnreadDot").Visibility == Visibility.Visible, "collapsed conversation rail retains unread dot");
                store.SetChatMutedUntil("a", DateTimeOffset.UtcNow.AddHours(1));
                Refresh();
                Require(!iconStates.Last().Lit && total.Text == "2", "all muted stops flashing but preserves badges");
                var muted = Receive("b"); Call("OnChatReceived", muted);
                Require(toastCount == 0 && Unread("b") == 2 && !iconStates.Last().Lit, "muted incoming messages stay unread without toast or flashing");
                store.SetChatMutedUntil("a", DateTimeOffset.UtcNow.AddMilliseconds(30));
                Refresh();
                await Task.Delay(80);
                Call("TickChatNotification");
                Require((bool)typeof(MainWindow).GetField("chatFlashing", Private)!.GetValue(window)!, "mute expiry restores flashing for existing unread");
                nav.SelectedIndex = 3;
                Select("a");
                Require(Unread("a") == 1, "background selected conversation does not mark read");
                foreground = true;
                Call("ReadVisibleChat");
                Require(Unread("a") == 0 && total.Text == "2" && !iconStates.Last().Lit, "foreground activation reads visible peer only; remaining muted unread does not flash");
                var active = Receive("a"); Call("OnChatReceived", active);
                Require(Unread("a") == 0 && toastCount == 0, "new message in active conversation is immediately read without toast");
                foreground = false;
                var hidden = Receive("a"); Call("OnChatReceived", hidden);
                Require(Unread("a") == 1 && toastCount == 1 && activations == 0, "hidden conversation remains unread and notifies without activating");
                Receive("a", hidden.MessageId); Refresh();
                Require(Unread("a") == 1, "duplicate delivery does not inflate unread count");
                nav.SelectedIndex = 0;
                foreground = true;
                Call("ReadVisibleChat");
                Require(Unread("a") == 1, "foreground non-chat page cannot clear unread");
                nav.SelectedIndex = 7;
                Control<TextBox>("NicknameBox").Text = "未保存资料";
                await window.OpenUnreadChatAsync();
                Require(Control<TabControl>("Tabs").SelectedIndex == 7 && Unread("a") == 1, "canceled settings navigation preserves page and unread");
                choice = SettingsLeaveChoice.Discard;
                await window.OpenUnreadChatAsync();
                Require(Control<TabControl>("Tabs").SelectedIndex == 3 && ((ChatConversationRow)list.SelectedItem).PeerId == "a" && Unread("a") == 0,
                    "tray action opens latest unread and marks it read after navigation");
                Require(Unread("b") == 2, "opening one unread conversation leaves others unread");
                foreground = false;
                var another = Receive("a"); Call("OnChatReceived", another);
                var newest = Receive("b"); Call("OnChatReceived", newest);
                store.QueueChatMessage("a", "Text", "outgoing must not change unread ordering", null, null, DateTimeOffset.UtcNow.AddDays(1));
                Require(store.GetLatestUnreadChatPeer() == "b", "latest unread selection ignores newer outgoing messages");
                for (var i = 0; i < 100; i++) Receive("a");
                Refresh();
                Require(total.Text == "99+" && window.ChatConversations.Single(c => c.PeerId == "a").UnreadText == "99+", "sidebar and conversation counts cap at 99+");
                store.ClearChatConversation("a"); Refresh();
                Require(total.Text == "3" && !iconStates.Last().Lit, "clearing conversation updates total and flashing");
                await window.OpenChatNotificationAsync("b");
                Require(Unread("b") == 0 && badge.Visibility == Visibility.Collapsed && Control<Border>("ChatCollapsedUnreadDot").Visibility == Visibility.Collapsed && !iconStates.Last().Lit,
                    "reading all messages removes every badge and restores normal tray icon");
                var before = activations;
                await window.OpenUnreadChatAsync();
                Require(activations == before + 1, "tray with no unread still opens main window");
                var previousToasts = toastCount;
                Call("OnChatReceived", Receive("a"));
                Require(Unread("a") == 1 && toastCount == previousToasts + 1 && ((ChatConversationRow)list.SelectedItem).PeerId == "b",
                    "foreground different conversation remains unread and notifies without switching selection");
                Require(!window.IsVisible && !((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(window)!).Visible,
                    "all interactions isolated with no desktop window or tray icon");
                Console.WriteLine($"PASS: {checks} isolated notification checks.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { await (Task)typeof(MainWindow).GetMethod("ExitAsync", Private)!.Invoke(window, null)!; }
        });
        app.Run();
        return result;
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new Exception(check);
        checks++;
        Console.WriteLine("PASS " + check);
    }
}
