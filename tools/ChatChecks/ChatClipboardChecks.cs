using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    private static async Task CheckChatClipboardAsync(MainWindow window, NodeStore store)
    {
        const string peer = "clipboard-peer", other = "clipboard-other";
        foreach (var id in new[] { peer, other })
        {
            store.UpsertPeer(new PeerInfo(id, "127.0.0.1", 9, id, null, null));
            store.SavePeerCapabilities(id, [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        }
        typeof(MainWindow).GetMethod("RefreshChatConversations", Private)!.Invoke(window, null);
        var conversations = (ListBox)window.FindName("ChatConversationList");
        void Select(string? id) => conversations.SelectedItem = window.ChatConversations.FirstOrDefault(row => row.PeerId == id);
        var input = (TextBox)window.FindName("ChatInput");
        var fixtureRoot = Path.Combine(store.DataDirectory, "clipboard-input");
        Directory.CreateDirectory(fixtureRoot);
        Select(peer);
        input.Text = "保留正文草稿";
        Require(!await window.PasteChatImageAsync(new DataObject(DataFormats.UnicodeText, "普通粘贴文字")) && input.Text == "保留正文草稿",
            "ordinary text paste is left to the editor without changing the draft or sending attachments");

        var mutable = new WriteableBitmap(12, 8, 96, 96, PixelFormats.Bgra32, null);
        var originalPixels = Enumerable.Repeat((byte)0x33, 12 * 8 * 4).ToArray();
        mutable.WritePixels(new Int32Rect(0, 0, 12, 8), originalPixels, 12 * 4, 0);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, mutable);
        data.SetData(DataFormats.UnicodeText, "C:\\do-not-paste-this-image-path.png");
        var pasteEvent = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText)
        { RoutedEvent = DataObject.PastingEvent };
        input.RaiseEvent(pasteEvent);
        var snapshotSend = window.ChatClipboardPreparationTask;
        Select(other);
        mutable.WritePixels(new Int32Rect(0, 0, 12, 8), new byte[12 * 8 * 4], 12 * 4, 0);
        await snapshotSend;
        var message = store.GetChatMessages(peer).Single();
        var resource = store.GetPrivateResource(message.ResourceId!, peer)!;
        var image = BitmapFrame.Create(new Uri(resource.SourcePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var copiedPixels = new byte[12 * 8 * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(copiedPixels, 12 * 4, 0);
        Require(pasteEvent.CommandCancelled && input.Text == "保留正文草稿" && store.GetChatMessages(other).Count == 0,
            "image paste cancels text-path insertion and freezes the recipient before conversation changes");
        Require(resource.Mode == PublishMode.Copy && resource.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                image.PixelWidth == 12 && image.PixelHeight == 8 && copiedPixels.SequenceEqual(originalPixels),
            "clipboard bitmap pixels are snapshotted and sent as an independent managed PNG copy");
        var temporaryRoot = Path.Combine(store.DataDirectory, "chat-clipboard");
        Require(Directory.Exists(temporaryRoot) && !Directory.EnumerateFileSystemEntries(temporaryRoot).Any() && File.Exists(resource.SourcePath),
            "successful bitmap sending removes only its temporary PNG and retains the private managed copy");

        Select(peer);
        var originalImage = Path.Combine(fixtureRoot, "original.png");
        File.Copy(resource.SourcePath, originalImage);
        var paths = new[] { originalImage };
        var fileData = new DataObject(DataFormats.FileDrop, paths);
        var originalCount = store.GetChatMessages(peer).Count;
        var fileSend = window.PasteChatImageAsync(fileData);
        paths[0] = Path.Combine(fixtureRoot, "changed-after-paste.png");
        Select(other);
        await fileSend;
        var originalMessage = store.GetChatMessages(peer).Last();
        var originalResource = store.GetPrivateResource(originalMessage.ResourceId!, peer)!;
        Require(store.GetChatMessages(peer).Count == originalCount + 1 && store.GetChatMessages(other).Count == 0 &&
                originalResource.Mode == PublishMode.Copy && File.Exists(originalImage) &&
                File.ReadAllBytes(originalImage).SequenceEqual(File.ReadAllBytes(originalResource.SourcePath)),
            "image file paste freezes paths and recipient and copies the original without deleting it");

        var beginDrop = typeof(MainWindow).GetMethod("TryBeginChatImageSend", Private)!;
        var dropCount = store.GetChatMessages(peer).Count;
        Require((bool)beginDrop.Invoke(window, [new DataObject(DataFormats.Bitmap, image), peer, true])!,
            "a raw bitmap drag payload uses direct image sending");
        await window.ChatClipboardPreparationTask;
        Require(store.GetChatMessages(peer).Count == dropCount + 1 &&
                store.GetPrivateResource(store.GetChatMessages(peer).Last().ResourceId!, peer)!.Mode == PublishMode.Copy,
            "raw bitmap drops create a managed image copy without a file-mode popup");

        Select(null);
        var beforeRejected = store.GetChatMessages(peer).Count;
        Require(await window.PasteChatImageAsync(data) && store.GetChatMessages(peer).Count == beforeRejected && input.Text == "保留正文草稿",
            "image paste without a recipient is handled without inserting fallback text or queuing a file");
        Select(peer);
        store.SavePeerCapabilities(peer, [NodeDefaults.ChatCapability]);
        Require(await window.PasteChatImageAsync(data) && store.GetChatMessages(peer).Count == beforeRejected,
            "a recipient without private-file capability cannot receive clipboard images");
        store.SavePeerCapabilities(peer, [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);

        var missing = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(fixtureRoot, "missing.png") });
        Require(await window.PasteChatImageAsync(missing) && store.GetChatMessages(peer).Count == beforeRejected &&
                !Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "missing image files fail without queueing or leaving temporary images");
        var textFile = Path.Combine(fixtureRoot, "document.txt");
        File.WriteAllText(textFile, "ordinary attachment");
        Require(!await window.PasteChatImageAsync(new DataObject(DataFormats.FileDrop, new[] { textFile })),
            "non-image file payloads retain the existing file-send flow");
        var imageNamedFolder = Path.Combine(fixtureRoot, "folder.png");
        Directory.CreateDirectory(imageNamedFolder);
        Require(!await window.PasteChatImageAsync(new DataObject(DataFormats.FileDrop, new[] { imageNamedFolder })),
            "a folder with an image-like name retains the existing folder-send flow");
        Require(await window.PasteChatImageAsync(new DataObject(DataFormats.FileDrop, new[] { originalImage, textFile })) &&
                store.GetChatMessages(peer).Count == beforeRejected && File.Exists(textFile),
            "mixed image and non-image clipboard files are rejected together without sending paths as text");
        Require(!(bool)beginDrop.Invoke(window, [new DataObject(DataFormats.FileDrop, new[] { originalImage, textFile }), peer, true])!,
            "mixed file drops keep the existing reference-or-copy confirmation flow");

        var large = Path.Combine(fixtureRoot, "large.png");
        using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(MainWindow.MaxChatImageBytes + 1);
        Require(await window.PasteChatImageAsync(new DataObject(DataFormats.FileDrop, new[] { large })) &&
                store.GetChatMessages(peer).Count == beforeRejected && File.Exists(large), "oversized image files are rejected while preserving the original source");
        var tooWide = BitmapSource.Create(4001, 4000, 96, 96, PixelFormats.Indexed1,
            new BitmapPalette([Colors.Black, Colors.White]), new byte[((4001 + 7) / 8) * 4000], (4001 + 7) / 8);
        Require(await window.PasteChatImageAsync(new DataObject(DataFormats.Bitmap, tooWide)) && store.GetChatMessages(peer).Count == beforeRejected,
            "clipboard pixel limits reject oversized low-bit-depth images before cloning or encoding");
        var interrupted = window.PasteChatImageAsync(new DataObject(DataFormats.Bitmap, image));
        var preparingTask = window.ChatClipboardPreparationTask;
        var duplicate = window.PasteChatImageAsync(new DataObject(DataFormats.Bitmap, image));
        Require(ReferenceEquals(preparingTask, window.ChatClipboardPreparationTask),
            "a second paste cannot replace the tracked in-flight preparation task");
        store.SavePeerCapabilities(peer, [NodeDefaults.ChatCapability]);
        await Task.WhenAll(interrupted, duplicate);
        Require(store.GetChatMessages(peer).Count == beforeRejected && !Directory.EnumerateFileSystemEntries(temporaryRoot).Any(),
            "losing recipient capability during bitmap preparation removes the generated PNG without enqueuing it");
        store.SavePeerCapabilities(peer, [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        Require(!window.IsVisible && input.Text == "保留正文草稿", "clipboard tests use injected data only and never show a window or modify the real clipboard");
    }
}
