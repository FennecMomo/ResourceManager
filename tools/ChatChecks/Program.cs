using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    private static readonly string Output = Path.GetFullPath("dist/chat-checks");
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        NodeDefaults.UseDataDirectory(Path.Combine(Output, "data-" + Guid.NewGuid().ToString("N")));
        var store = new NodeStore();
        Require(!store.GetChatConversationsCollapsed(), "new installation defaults to expanded conversations");
        store.SaveChatConversationsCollapsed(true);
        store.UpsertPeer(new PeerInfo("peer", "127.0.0.1", 9, "隔离测试设备", null, null));
        store.SavePeerCapabilities("peer", [NodeDefaults.ChatCapability]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var services = new SettingsEditorServices(() => false, _ => throw new Exception("unexpected registry write"),
            (_, _) => throw new Exception("unexpected network restart"), _ => SettingsLeaveChoice.KeepEditing,
            (message, error) => throw new Exception(message, error));
        var pumpCount = 0;
        var window = new MainWindow(false, services, false, () => { pumpCount++; return Task.CompletedTask; });
        var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                T Control<T>(string id) => (T)window.FindName(id);
                var root = (FrameworkElement)window.Content;
                var input = Control<TextBox>("ChatInput");
                var conversations = Control<ListBox>("ChatConversationList");
                var toggle = Control<Button>("ChatConversationsToggle");
                var send = Control<Button>("ChatSendButton");
                var nav = Control<ListBox>("NavList");
                void Layout(int width = 1260, int height = 780)
                {
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    root.UpdateLayout();
                }
                void Toggle() => toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Compose(RoutedEvent phase)
                {
                    input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                        new TextComposition(InputManager.Current, input, "中文")) { RoutedEvent = phase });
                }
                nav.SelectedIndex = 3;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(conversations.Visibility == Visibility.Collapsed && toggle.ToolTip.ToString() == "展开会话栏", "constructor restores saved collapsed state");
                Toggle();
                Require(conversations.Visibility == Visibility.Visible && !new NodeStore().GetChatConversationsCollapsed(), "expand is immediately persisted");
                conversations.SelectedItem = window.ChatConversations.Single(c => c.PeerId == "peer");
                Require(send.IsEnabled, "capable selected peer enables sending");
                Layout();
                var heights = new List<double>();
                for (var lines = 1; lines <= 7; lines++)
                {
                    input.Text = string.Join('\n', Enumerable.Range(1, lines).Select(i => $"第 {i} 行"));
                    Layout();
                    heights.Add(input.ActualHeight);
                    Require(input.LineCount == lines, $"input renders {lines} explicit lines");
                }
                Require(heights.Take(5).Zip(heights.Skip(1).Take(4)).All(p => p.Second > p.First + 5), "input grows on every line from one through five");
                Console.WriteLine("Input heights: " + string.Join(", ", heights));
                Require(Math.Abs(heights[4] - heights[5]) < 1 && Math.Abs(heights[4] - heights[6]) < 1, "height stays capped at five lines");
                var contentHost = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                Require(contentHost.ComputedVerticalScrollBarVisibility == Visibility.Visible && contentHost.ScrollableHeight > 0, "extra lines scroll inside input");
                input.ScrollToEnd();
                Layout();
                Require(input.VerticalOffset > 0, "last lines remain reachable");
                input.Clear();
                Layout();
                Require(Math.Abs(input.ActualHeight - heights[0]) < 1, "clearing returns to one line");

                input.Text = string.Concat(Enumerable.Repeat("自动折行文字", 20));
                Layout();
                Require(input.LineCount > 1 && input.ActualHeight > heights[0], "soft wrapping also grows input");
                var expandedInputWidth = input.ActualWidth;
                var expandedLines = input.LineCount;
                var draft = input.Text;
                Toggle();
                Layout();
                Console.WriteLine($"Input width {expandedInputWidth} -> {input.ActualWidth}, lines {expandedLines} -> {input.LineCount}");
                Require(input.ActualWidth > expandedInputWidth + 150 && input.LineCount < expandedLines, "collapse widens chat and reflows draft");
                Require(input.Text == draft && ((ChatConversationRow)conversations.SelectedItem).PeerId == "peer", "collapse preserves draft and selected conversation");
                Require(new NodeStore().GetChatConversationsCollapsed(), "collapse survives store reopen");
                nav.SelectedIndex = 1;
                nav.SelectedIndex = 3;
                Layout();
                Require(conversations.Visibility == Visibility.Collapsed && input.Text == draft, "tab changes preserve collapse and draft");
                Render(root, 1260, 780, "collapsed");
                Toggle();
                Layout(1010, 630);
                input.Text = string.Join('\n', Enumerable.Range(1, 8).Select(i => $"较小窗口，第 {i} 行"));
                Layout(1010, 630);
                var sendPosition = send.TranslatePoint(new Point(), root);
                Require(sendPosition.Y >= 0 && sendPosition.Y + send.ActualHeight <= 630 && Control<ListBox>("ChatMessageList").ActualHeight > 50,
                    "small window retains sending controls and message area at five-line cap");
                Render(root, 1010, 630, "expanded-five-lines");

                input.Text = "第一行\n第二行";
                var before = store.GetChatMessages("peer").Count;
                Require(!window.HandleChatInputKey(Key.Enter, ModifierKeys.Shift, false) && input.Text.Contains('\n'), "Shift+Enter passes through to multiline editor");
                Require(!window.HandleChatInputKey(Key.Enter, ModifierKeys.Control, false), "modified Enter is not mistaken for send");
                Require(!window.HandleChatInputKey(Key.ImeProcessed, ModifierKeys.None, false), "IME-processed key never sends");
                Compose(TextCompositionManager.PreviewTextInputStartEvent);
                Require(!window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false) && store.GetChatMessages("peer").Count == before, "composition confirming Enter never sends");
                Compose(TextCompositionManager.PreviewTextInputUpdateEvent);
                Require(!window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false), "composition updates retain guard");
                Compose(TextCompositionManager.PreviewTextInputEvent);
                Require(!window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false), "completion and confirming Enter in same dispatch do not send");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                input.Text = "第一行\n第二行";
                Require(window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false), "plain Enter is handled before TextBox consumes it");
                Require(store.GetChatMessages("peer").Count == before + 1 && store.GetChatMessages("peer").Last().Text == "第一行\n第二行" && input.Text == "" && pumpCount == 1,
                    "Enter queues exactly one multiline message and clears input");
                input.Text = "重复按键";
                Require(window.HandleChatInputKey(Key.Enter, ModifierKeys.None, true) && store.GetChatMessages("peer").Count == before + 1 && input.Text == "重复按键", "held Enter cannot repeatedly send");
                input.Text = "  \n  ";
                window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false);
                Require(store.GetChatMessages("peer").Count == before + 1, "blank input does not send");
                input.Text = "保留草稿";
                conversations.SelectedItem = null;
                window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false);
                Require(input.Text == "保留草稿" && store.GetChatMessages("peer").Count == before + 1, "no recipient preserves draft");
                conversations.SelectedItem = window.ChatConversations.Single(c => c.PeerId == "peer");
                store.SavePeerCapabilities("peer", []);
                typeof(MainWindow).GetMethod("RefreshChatHeader", Private)!.Invoke(window, null);
                window.HandleChatInputKey(Key.Enter, ModifierKeys.None, false);
                Require(!send.IsEnabled && input.Text == "保留草稿" && store.GetChatMessages("peer").Count == before + 1, "unsupported recipient cannot bypass disabled send");
                await CheckDeviceRefreshAsync(window, store);
                await CheckAiBridgeAsync(window, store);
                await CheckChatFilesAsync(window, store);
                Require(!window.IsVisible && !((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(window)!).Visible,
                    "all checks stayed hidden with no tray icon or desktop input");
                Console.WriteLine($"PASS: {checks} isolated chat checks; IME events are simulated, no live input method or desktop is operated.");
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

    private static void Render(FrameworkElement root, int width, int height, string suffix)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 249, 252)), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, $"chat-{suffix}.png"));
        encoder.Save(stream);
    }
}
