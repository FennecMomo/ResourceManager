using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    private static async Task CheckChatTextCopyAsync(MainWindow window, NodeStore store)
    {
        var copied = new List<string>();
        var sink = typeof(MainWindow).GetField("chatTextClipboardWriter", Private)!;
        sink.SetValue(window, (Action<string>)copied.Add);
        try
        {
            ((ListBox)window.FindName("NavList")).SelectedIndex = 3;
            var message = new ChatMessage("copy-fixture", "peer", true, "Text", "可复制的中文 🙂\n第二行保持换行", null,
                null, DateTimeOffset.Now, null, "Delivered", null, 1, null);
            window.ChatMessages.Clear();
            window.ChatMessages.Add(new ChatMessageRow(message));
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1260, 780));
            root.Arrange(new Rect(0, 0, 1260, 780));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var list = (ListBox)window.FindName("ChatMessageList");
            var text = ChatTextVisuals<TextBox>(list).Single(box => box.Name == "MessageText");
            Require(text.IsReadOnly && text.TextWrapping == TextWrapping.Wrap && text.Text == message.Text,
                "real message bubbles expose selectable read-only multiline text");
            var menu = text.ContextMenu;
            menu.PlacementTarget = text;
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(copied.Count == 1 && copied[0] == text.Text,
                "message copy menu copies the whole bubble when no range is selected without touching the real clipboard");
            text.Select(0, 3);
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(copied.Count == 2 && copied[1] == "可复制",
                "message copy menu respects a selected text range");
            text.Clear();
            Require(!window.CopyChatMessageText(text) && copied.Count == 2, "empty message copying does not replace clipboard content");
            Render(root, 1260, 780, "selectable-text");
        }
        finally { sink.SetValue(window, null); }

        var preparing = typeof(MainWindow).GetField("preparingChatClipboardImage", Private)!;
        preparing.SetValue(window, true);
        try
        {
            var status = await window.HandleLocalControlAsync(new LocalControlRequest(1, "status", Environment.ProcessId), default);
            var state = System.Text.Json.JsonSerializer.SerializeToElement(status.Data);
            var shutdown = await window.HandleLocalControlAsync(new LocalControlRequest(1, "shutdown", Environment.ProcessId), default);
            Require(status.Success && state.GetProperty("preparingPrivateResource").GetBoolean() &&
                !shutdown.Success && shutdown.Error == "preparing_private_resource" && shutdown.AfterResponse is null,
                "background shutdown reports and preserves an in-progress clipboard image preparation");
        }
        finally { preparing.SetValue(window, false); }
    }

    private static IEnumerable<T> ChatTextVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in ChatTextVisuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
