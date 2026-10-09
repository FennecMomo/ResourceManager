using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    private static async Task CheckChatFilesAsync(MainWindow window, NodeStore store)
    {
        var send = typeof(MainWindow).GetMethod("SendPrivateChatBatchAsync", Private)!;
        Task Batch(string peer, string[] paths, PublishMode mode) => (Task)send.Invoke(window, [peer, paths, mode])!;
        var root = Path.Combine(store.DataDirectory, "batch-input");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first.txt"); var second = Path.Combine(root, "second.txt");
        File.WriteAllText(first, "first content"); File.WriteAllText(second, "second content");
        var folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "folder content");
        var before = store.GetChatMessages("peer").Count;
        await Batch("peer", [first, second, first, Path.Combine(root, "missing.txt")], PublishMode.Reference);
        var added = store.GetChatMessages("peer").Skip(before).ToArray();
        Require(added.Length == 2 && added.All(m => store.GetPrivateResource(m.ResourceId!, "peer")!.Mode == PublishMode.Reference),
            "multiselect batch deduplicates paths, continues after missing files and queues separate references");
        Require(((TextBlock)window.FindName("StatusText")).Text.Contains("1 项失败"), "partial failures appear in batch summary");
        await Batch("peer", [first, folder], PublishMode.Copy);
        var copied = store.GetChatMessages("peer").TakeLast(2).Select(m => store.GetPrivateResource(m.ResourceId!, "peer")!).ToArray();
        Require(copied.All(r => r.Mode == PublishMode.Copy) && File.ReadAllText(copied[0].SourcePath) == "first content" &&
            File.ReadAllText(Path.Combine(copied[1].SourcePath, "inside.txt")) == "folder content", "mixed file and folder batch creates real private copies");
        var parse = typeof(MainWindow).GetMethod("ChatDropPaths", BindingFlags.Static | BindingFlags.NonPublic)!;
        var data = new DataObject(DataFormats.FileDrop, new[] { first, folder, first });
        var dropped = (string[])parse.Invoke(null, [data])!;
        Require(dropped.SequenceEqual(new[] { first, folder }) && ((Border)window.FindName("ChatDropZone")).AllowDrop,
            "chat drop zone accepts deduplicated native file-drop payloads including folders");
        Require(((string[])parse.Invoke(null, [new DataObject(DataFormats.Text, "draft")])!).Length == 0,
            "text dragging remains composer editing rather than attachment sending");
        var original = new[] { second };
        var menu = (ContextMenu)typeof(MainWindow).GetMethod("CreatePrivateDropMenu", Private)!.Invoke(window, ["peer", original])!;
        original[0] = "changed-after-drop";
        store.UpsertPeer(new PeerInfo("other", "127.0.0.1", 9, "Other", null, null));
        store.SavePeerCapabilities("other", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        typeof(MainWindow).GetMethod("RefreshChatConversations", Private)!.Invoke(window, null);
        ((ListBox)window.FindName("ChatConversationList")).SelectedItem = window.ChatConversations.Single(r => r.PeerId == "other");
        before = store.GetChatMessages("peer").Count;
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 200 && (bool)typeof(MainWindow).GetField("preparingChatResource", Private)!.GetValue(window)!; i++) await Task.Delay(10);
        Require(store.GetChatMessages("peer").Count == before + 1 && store.GetChatMessages("other").Count == 0 &&
            store.GetChatMessages("peer").Last().ResourceName == "second.txt", "drop confirmation freezes recipient and paths across conversation changes");
        var chat = (ChatService)typeof(MainWindow).GetField("chat", Private)!.GetValue(window)!;
        void Cancel(ChatMessage message)
        {
            if (message.PeerId == "peer") ((CancellationTokenSource?)typeof(MainWindow).GetField("chatResourceCancellation", Private)!.GetValue(window))?.Cancel();
        }
        before = store.GetChatMessages("peer").Count;
        chat.MessageChanged += Cancel;
        try { await Batch("peer", [first, second], PublishMode.Copy); }
        finally { chat.MessageChanged -= Cancel; }
        Require(store.GetChatMessages("peer").Count == before + 1 && File.Exists(first) && File.Exists(second),
            "cancel stops remaining batch items and retains queued item and source files");
        Require(!(bool)typeof(MainWindow).GetField("preparingChatResource", Private)!.GetValue(window)! &&
            ((FrameworkElement)window.FindName("ChatPreparationPanel")).Visibility == Visibility.Collapsed, "batch completion and cancellation restore preparation controls");
        await Batch("unknown", [first], PublishMode.Reference);
        Require(store.GetChatMessages("unknown").Count == 0, "batch rejects an unknown recipient without enqueuing files");
    }
}
