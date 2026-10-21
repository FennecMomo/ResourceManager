using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    private static async Task CheckChatImagesAsync(MainWindow window, NodeStore store)
    {
        var inputDirectory = Path.Combine(store.DataDirectory, "image-input");
        Directory.CreateDirectory(inputDirectory);
        var originalPath = Path.Combine(inputDirectory, "test-image.png");
        var png = ChatImagePng(960, 640);
        await File.WriteAllBytesAsync(originalPath, png);
        store.SavePeerCapabilities("peer", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        using var localClient = new PeerClient(store);
        var localChat = new ChatService(store, localClient);
        var outgoing = localChat.QueuePrivateResource("peer", originalPath, PublishMode.Reference);
        using var localImages = new ChatImageService(store, localClient);
        var thumbnail = await localImages.LoadThumbnailAsync(outgoing, default);
        var original = await localImages.LoadOriginalAsync(outgoing, default);
        Require(thumbnail.IsFrozen && thumbnail.PixelWidth == 480 && thumbnail.PixelHeight == 320 &&
            original.IsFrozen && original.PixelWidth == 960 && original.PixelHeight == 640,
            "chat thumbnails are bounded independently from original image copying");
        await CheckChatImageLayoutAsync(window, outgoing);
        Require(ChatImageService.IsImageMessage(outgoing) && !ChatImageService.IsImageMessage(outgoing with { ResourceName = "image.svg" }) &&
            !ChatImageService.IsImageMessage(outgoing with { ResourceName = "image.webp" }) && !ChatImageService.IsImageMessage(outgoing with { Kind = "Text" }),
            "only supported raster file messages become image bubbles");
        Require(ChatImageThrows(() => ChatImageService.Decode(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>"), true)),
            "a renamed SVG or XML attachment never enters raster preview rendering");
        Require(await ChatImageThrowsAsync(() => ChatImageService.ReadBoundedAsync(new MemoryStream(new byte[ChatImageService.MaxImageBytes + 1]), default)),
            "actual streamed bytes are capped even without trustworthy content length");
        var huge = BitmapSource.Create(4001, 4000, 96, 96, PixelFormats.Indexed1, BitmapPalettes.BlackAndWhite,
            new byte[((4001 + 7) / 8) * 4000], (4001 + 7) / 8);
        var bmp = new BmpBitmapEncoder(); bmp.Frames.Add(BitmapFrame.Create(huge));
        using (var oversizedPixels = new MemoryStream())
        {
            bmp.Save(oversizedPixels);
            var compactImage = oversizedPixels.ToArray();
            Require(compactImage.Length < ChatImageService.MaxImageBytes && ChatImageThrows(() => ChatImageService.Decode(compactImage, true)),
                "a compact raster exceeding sixteen million source pixels fails before thumbnail decoding");
        }

        BitmapSource? copied = null;
        window.ChatImageClipboardOverride = image => copied = image;
        try
        {
            await window.ChatImageCopy(outgoing, default);
            Require(copied?.PixelWidth == 960 && copied.PixelHeight == 640,
                "explicit copy uses original pixels through an isolated clipboard sink");
        }
        finally { window.ChatImageClipboardOverride = null; }

        var saveDirectory = Path.Combine(store.DataDirectory, "image-save-check");
        Directory.CreateDirectory(saveDirectory);
        await File.WriteAllTextAsync(Path.Combine(saveDirectory, "saved.png"), "keep original target");
        var pickerField = typeof(MainWindow).GetField("downloadDestinationPicker", Private)!;
        var previousPicker = pickerField.GetValue(window);
        var saveAsRequested = false;
        pickerField.SetValue(window, (Func<RemoteResource, bool, DownloadDestination?>)((_, saveAs) =>
        { saveAsRequested = saveAs; return new DownloadDestination(saveDirectory, "saved.png"); }));
        try { await window.ChatImageSaveAs(outgoing, default); }
        finally { pickerField.SetValue(window, previousPicker); }
        Require(saveAsRequested && File.ReadAllText(Path.Combine(saveDirectory, "saved.png")) == "keep original target" &&
            File.ReadAllBytes(Path.Combine(saveDirectory, "saved (2).png")).SequenceEqual(png),
            "outgoing image Save As reuses the destination picker and never overwrites an existing file");

        var previewCalls = 0; var copies = 0; var saves = 0;
        var view = new ChatImageView
        {
            Message = outgoing,
            ImageLoader = (_, _) => { previewCalls++; return Task.FromResult(thumbnail); },
            CopyImage = (message, _) => { Require(message.MessageId == outgoing.MessageId, "image context copy retains its message identity"); copies++; return Task.CompletedTask; },
            SaveImageAs = (_, _) => { saves++; return Task.CompletedTask; }
        };
        Require(previewCalls == 0, "constructing an image control does not preload unrelated conversations");
        await view.LoadAsync();
        Require(view.HasImage && previewCalls == 1, "the rendered image control asynchronously resolves its thumbnail");
        ((MenuItem)view.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ((MenuItem)view.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(copies == 1 && saves == 1, "image context menu exposes copy and Save As through injected actions");
        view.ImageLoader = (_, _) => Task.FromException<BitmapSource>(new InvalidDataException("isolated corrupt image"));
        await view.LoadAsync();
        await view.SaveAsAsync();
        Require(!view.HasImage && view.StatusText.Contains("无法预览") && saves == 2,
            "image decoding failure keeps a readable placeholder and a working Save As action");
        var late = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        view.ImageLoader = (_, token) => { oldToken = token; return late.Task; };
        var oldLoad = view.LoadAsync();
        view.Message = outgoing with { MessageId = "a-different-row" };
        view.ImageLoader = (_, _) => Task.FromResult(original);
        await view.LoadAsync(); late.SetResult(thumbnail); await oldLoad;
        Require(oldToken.IsCancellationRequested && ReferenceEquals(view.LoadedImage, original),
            "recycled image rows cancel old work and reject late results from another message");
        view.ImageLoader = (_, token) => { oldToken = token; return Task.FromCanceled<BitmapSource>(new CancellationToken(true)); };
        await view.LoadAsync(); view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Require(oldToken.IsCancellationRequested, "unloading image rows cancels their lifetime");

        await CheckChatImagePeersAsync(png);
        localChat.Cancel("peer", outgoing.MessageId);
        Require(await ChatImageThrowsAsync(() => localImages.LoadThumbnailAsync(outgoing, default)) && File.Exists(originalPath),
            "revoked outgoing private images stop loading while their reference originals remain intact");
    }

    private static async Task CheckChatImageLayoutAsync(MainWindow window, ChatMessage message)
    {
        ((ListBox)window.FindName("NavList")).SelectedIndex = 3;
        window.ChatMessages.Clear();
        window.ChatMessages.Add(new ChatMessageRow(message));
        var root = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1260, 780), new Size(1010, 630) })
        {
            root.Measure(size); root.Arrange(new Rect(new Point(), size));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var list = (ListBox)window.FindName("ChatMessageList");
            var view = ChatTextVisuals<ChatImageView>(list).Single();
            Require(view.ImageLoader is not null && view.CopyImage is not null && view.SaveImageAs is not null,
                "real chat row binds thumbnail, copy and Save As actions to its owning window");
            await view.LoadAsync(); root.UpdateLayout();
            var origin = view.TranslatePoint(new Point(), list);
            Require(view.HasImage && view.ActualWidth > 100 && view.ActualHeight > 80 &&
                origin.X >= 0 && origin.X + view.ActualWidth <= list.ActualWidth + 1,
                "image bubble loads and fits the actual chat timeline at " + size.Width + "px");
            Require(ChatTextVisuals<TextBox>(list).Single(text => text.Name == "MessageText").Visibility == Visibility.Collapsed &&
                ChatTextVisuals<Button>(list).Where(button => button.Content?.ToString() is "下载资源" or "另存为…")
                    .All(button => button.Visibility == Visibility.Collapsed),
                "image timeline rows use the thumbnail and its menu instead of generic file cards");
            Render(root, (int)size.Width, (int)size.Height, "image-" + (int)size.Width);
        }
    }

    private static async Task CheckChatImagePeersAsync(byte[] png)
    {
        var sender = new NodeStore(Path.Combine(Output, "image-sender-" + Guid.NewGuid().ToString("N")));
        var receiver = new NodeStore(Path.Combine(Output, "image-receiver-" + Guid.NewGuid().ToString("N")));
        var senderPort = ChatImagePort(); var receiverPort = ChatImagePort();
        sender.SaveSettings("图片发送方", null, senderPort, false);
        receiver.SaveSettings("图片接收方", null, receiverPort, false);
        using var senderClient = new PeerClient(sender, supportsChat: true);
        using var receiverClient = new PeerClient(receiver, supportsChat: true);
        var senderChat = new ChatService(sender, senderClient); var receiverChat = new ChatService(receiver, receiverClient);
        using var senderUpnp = new UpnpGatewayClient(); using var receiverUpnp = new UpnpGatewayClient();
        await using var senderDiscovery = new LanDiscoveryService(sender);
        await using var receiverDiscovery = new LanDiscoveryService(receiver);
        await using var senderNode = new PeerNode(sender, senderDiscovery, new UpnpPortMappingManager(sender, senderUpnp), chat: senderChat);
        await using var receiverNode = new PeerNode(receiver, receiverDiscovery, new UpnpPortMappingManager(receiver, receiverUpnp), chat: receiverChat);
        await senderNode.StartAsync(senderPort, "127.0.0.1");
        await receiverNode.StartAsync(receiverPort, "127.0.0.1");
        var senderPeer = await receiverClient.ConnectAsync("127.0.0.1", senderPort);
        var receiverPeer = await senderClient.ConnectAsync("127.0.0.1", receiverPort);
        var file = Path.Combine(sender.DataDirectory, "peer-image.png");
        await File.WriteAllBytesAsync(file, png);
        var queued = senderChat.QueuePrivateResource(receiverPeer.DeviceId, file, PublishMode.Reference);
        await senderChat.PumpAsync();
        var received = receiver.GetChatMessages(senderPeer.DeviceId).Single(message => message.MessageId == queued.MessageId);
        Require(received.Kind == "PrivateResource" && sender.GetChatMessage(receiverPeer.DeviceId, queued.MessageId, true)!.State == "Delivered",
            "two isolated signed peers deliver an existing private image message");
        var counted = new CountChatImageClient(receiverClient);
        using var images = new ChatImageService(receiver, counted);
        var image = await images.LoadThumbnailAsync(received, default);
        await images.LoadThumbnailAsync(received, default);
        Require(image.PixelWidth == 480 && counted.ContentCalls == 1 && counted.MetadataCalls == 2,
            "private image preview uses authenticated resource reads and reuses its local cache after rechecking access");
        Require(receiver.GetDownloads().Count == 0 && receiver.GetLocalChatProgress(senderPeer.DeviceId, received.MessageId)!.DownloadState == "未开始",
            "automatic image previews create no download jobs or completed-download receipts");
        var cache = Path.Combine(receiver.DataDirectory, "chat-image-cache");
        var cached = Directory.GetFiles(cache, "*.image");
        Require(cached.Length == 1 && File.ReadAllBytes(cached[0]).SequenceEqual(png),
            "received originals stay in the dedicated hashed local image cache");

        var outsider = new NodeStore(Path.Combine(Output, "image-outsider-" + Guid.NewGuid().ToString("N")));
        using var outsiderClient = new PeerClient(outsider);
        var outsiderPeer = await outsiderClient.ConnectAsync("127.0.0.1", senderPort);
        outsider.AcceptChatMessage(new ChatMessageRequest(NodeDefaults.ChatCapability, Guid.NewGuid().ToString("N"), senderPeer.DeviceId,
            outsider.GetSettings().Profile.DeviceId, DateTimeOffset.UtcNow, "PrivateResource", ResourceId: queued.ResourceId),
            senderPeer.Nickname, "peer-image.png", DateTimeOffset.UtcNow);
        var forged = outsider.GetChatMessages(senderPeer.DeviceId).Single();
        using var outsiderImages = new ChatImageService(outsider, outsiderClient);
        Require(await ChatImageThrowsAsync(() => outsiderImages.LoadThumbnailAsync(forged, default)) &&
            !Directory.Exists(Path.Combine(outsider.DataDirectory, "chat-image-cache")),
            "a registered third device with a copied resource ID cannot preview another recipient's private image");
        Require(sender.GetResources().Count == 0, "previewing private images never publishes them into the public resource catalog");

        await File.WriteAllTextAsync(Path.Combine(cache, "notes.txt"), "keep unrelated file");
        images.ClearConversation(senderPeer.DeviceId);
        Require(Directory.GetFiles(cache, "*.image").Length == 0 && File.Exists(Path.Combine(cache, "notes.txt")) && File.Exists(file),
            "conversation image cleanup removes only generated cache files and preserves originals and unrelated files");
        senderChat.Cancel(receiverPeer.DeviceId, queued.MessageId);
        Require(await ChatImageThrowsAsync(() => images.LoadThumbnailAsync(received, default)),
            "subsequent private preview reads fail after the sender revokes the attachment");
    }

    private static int ChatImagePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static byte[] ChatImagePng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 140; pixels[i + 2] = 50; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static bool ChatImageThrows(Action action) { try { action(); return false; } catch { return true; } }
    private static async Task<bool> ChatImageThrowsAsync(Func<Task> action) { try { await action(); return false; } catch { return true; } }
    private sealed class CountChatImageClient(IResourceClient inner) : IResourceClient
    {
        internal int MetadataCalls { get; private set; }
        internal int ContentCalls { get; private set; }
        public Task<RemoteResource?> GetResourceAsync(PeerInfo peer, string id, CancellationToken token = default) { MetadataCalls++; return inner.GetResourceAsync(peer, id, token); }
        public Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string id, CancellationToken token = default) => inner.GetFilesAsync(peer, id, token);
        public Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string id, string path, long offset, string? etag, CancellationToken token = default)
        { ContentCalls++; return inner.OpenFileAsync(peer, id, path, offset, etag, token); }
    }
}
