using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using ResourceManager.Core;

namespace ResourceManager.App;

/// <summary>Reads existing chat attachments through their original resource authorization.</summary>
internal sealed class ChatImageService(NodeStore store, IResourceClient client) : IDisposable
{
    internal const int MaxImageBytes = 20 * 1024 * 1024;
    private const long MaxPixels = 16_000_000;
    private readonly SemaphoreSlim slots = new(2, 2);
    private readonly CancellationTokenSource lifetime = new();
    private static readonly object CacheGate = new();
    private readonly Dictionary<string, long> conversationEpochs = [];

    public static bool IsImageMessage(ChatMessage message) => message.Kind is "Resource" or "PrivateResource" &&
        message.ResourceId is not null && IsImageName(message.ResourceName) && message.State != "Canceled";

    internal static bool IsImageName(string? name) => Path.GetExtension(name ?? "").ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff";

    internal Task<BitmapSource> LoadThumbnailAsync(ChatMessage message, CancellationToken token) => LoadAsync(message, true, token);
    internal Task<BitmapSource> LoadOriginalAsync(ChatMessage message, CancellationToken token) => LoadAsync(message, false, token);

    private async Task<BitmapSource> LoadAsync(ChatMessage message, bool thumbnail, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await slots.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var bytes = await ReadOriginalAsync(message, deadline.Token).ConfigureAwait(false);
            var result = await Task.Run(() => Decode(bytes, thumbnail), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            ValidateMessage(message);
            return result;
        }
        finally { slots.Release(); }
    }

    internal void ValidateMessage(ChatMessage message)
    {
        var saved = store.GetChatMessage(message.PeerId, message.MessageId, message.Outgoing);
        if (!IsImageMessage(message) || saved is null || !IsImageMessage(saved) ||
            saved.ResourceId != message.ResourceId || saved.Kind != message.Kind || store.GetPeer(message.PeerId) is null)
            throw new FileNotFoundException("这条图片消息已撤销、清空或设备已移除。");
    }

    internal LocalResource GetOutgoingResource(ChatMessage message)
    {
        ValidateMessage(message);
        if (!message.Outgoing) throw new InvalidOperationException("该图片来自对方设备。");
        var resource = message.Kind == "PrivateResource"
            ? store.GetPrivateResource(message.ResourceId!, message.PeerId) : store.GetResource(message.ResourceId!);
        if (resource is null || resource.Kind != ResourceKind.File ||
            (message.Kind == "Resource" && !store.CanAccessGroup(resource.GroupId, message.PeerId)))
            throw new FileNotFoundException("图片已撤销或该会话没有访问权限。");
        return resource;
    }

    private async Task<byte[]> ReadOriginalAsync(ChatMessage message, CancellationToken token)
    {
        ValidateMessage(message);
        if (message.Outgoing)
        {
            var source = GetOutgoingResource(message);
            await using var local = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (local.Length > MaxImageBytes) throw new InvalidDataException("图片超过 20 MiB，请使用下载或另存为。");
            return await ReadBoundedAsync(local, token).ConfigureAwait(false);
        }

        var peer = store.GetPeer(message.PeerId) ?? throw new FileNotFoundException("设备已移除。");
        var resource = await client.GetResourceAsync(peer, message.ResourceId!, token).ConfigureAwait(false);
        if (resource is null || !resource.Available || resource.Kind != ResourceKind.File)
            throw new FileNotFoundException("图片已撤销或原文件不可用。");
        if (resource.Size < 1 || resource.Size > MaxImageBytes)
            throw new InvalidDataException("图片超过预览范围，请使用下载或另存为。");
        var cacheDirectory = EnsureCacheDirectory();
        long epoch;
        lock (CacheGate) epoch = conversationEpochs.GetValueOrDefault(message.PeerId);
        var identity = $"{message.PeerId}\0{message.MessageId}\0{message.ResourceId}\0{resource.ModifiedUtc:O}\0{resource.Size}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var cachePath = Path.Combine(cacheDirectory, PeerCacheKey(message.PeerId) + "-" + key + ".image");
        if (File.Exists(cachePath))
        {
            RejectLink(cachePath);
            await using var cached = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (cached.Length == resource.Size) return await ReadBoundedAsync(cached, token).ConfigureAwait(false);
        }
        using var response = await client.OpenFileAsync(peer, message.ResourceId!, "", 0, null, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && (length > MaxImageBytes || length != resource.Size))
            throw new InvalidDataException("图片大小发生变化，请重试或使用下载。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
        if (bytes.LongLength != resource.Size) throw new InvalidDataException("图片传输不完整，请重试或使用下载。");
        ValidateMessage(message);
        var temporary = Path.Combine(cacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous)) await output.WriteAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (CacheGate)
            {
                if (conversationEpochs.GetValueOrDefault(message.PeerId) != epoch)
                    throw new OperationCanceledException("会话图片缓存已清空。");
                ValidateMessage(message);
                RejectLink(cachePath);
                File.Move(temporary, cachePath, true);
                try { TrimCache(cacheDirectory, cachePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return bytes;
    }

    private string EnsureCacheDirectory()
    {
        var root = Path.Combine(store.DataDirectory, "chat-image-cache");
        RejectLink(store.DataDirectory);
        Directory.CreateDirectory(root);
        RejectLink(root);
        return root;
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("图片缓存不能使用链接路径。");
    }

    private static void TrimCache(string directory, string keep)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.image")
            .Where(file => IsCacheFileName(file.Name) &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0).OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Length);
        var count = files.Length;
        foreach (var file in files)
        {
            if (total <= 100L * 1024 * 1024 && count <= 100) break;
            if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { var length = file.Length; file.Delete(); total -= length; count--; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string PeerCacheKey(string peerId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peerId))).ToLowerInvariant();
    private static bool IsCacheFileName(string name) => name.Length == 135 && name[64] == '-' &&
        name[..64].All(Uri.IsHexDigit) && name.Substring(65, 64).All(Uri.IsHexDigit) && name.EndsWith(".image", StringComparison.Ordinal);

    internal void ClearConversation(string peerId)
    {
        lock (CacheGate)
        {
            conversationEpochs[peerId] = conversationEpochs.GetValueOrDefault(peerId) + 1;
            var directory = Path.Combine(store.DataDirectory, "chat-image-cache");
            if (!Directory.Exists(directory)) return;
            RejectLink(store.DataDirectory); RejectLink(directory);
            foreach (var path in Directory.EnumerateFiles(directory, PeerCacheKey(peerId) + "-*.image"))
            {
                if (!IsCacheFileName(Path.GetFileName(path))) continue;
                RejectLink(path); File.Delete(path);
            }
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaxImageBytes) throw new InvalidDataException("图片超过 20 MiB，请使用下载或另存为。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    internal static BitmapSource Decode(byte[] data, bool thumbnail)
    {
        if (data.Length < 4 || data.Length > MaxImageBytes || !HasRasterSignature(data))
            throw new InvalidDataException("图片格式不受支持，请使用下载或另存为。");
        using var input = new MemoryStream(data, false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new InvalidDataException("图片尺寸超过预览范围，请使用下载或另存为。");
        var ratio = thumbnail ? Math.Max(1d, Math.Max(frame.PixelWidth / 480d, frame.PixelHeight / 320d)) : 1d;
        input.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (thumbnail) image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth / ratio));
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool HasRasterSignature(byte[] data) => data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
        (data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) ||
        data.AsSpan().StartsWith("GIF87a"u8) || data.AsSpan().StartsWith("GIF89a"u8) ||
        data.AsSpan().StartsWith("BM"u8) || data.AsSpan().StartsWith(new byte[] { 73, 73, 42, 0 }) ||
        data.AsSpan().StartsWith(new byte[] { 77, 77, 0, 42 });

    public void Dispose() => lifetime.Cancel();
}
