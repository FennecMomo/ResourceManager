using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private ChatImageService? chatImages;
    internal ChatImageService? ChatImagesOverride { get; set; }
    internal Action<BitmapSource>? ChatImageClipboardOverride { get; set; }
    private ChatImageService ChatImages => ChatImagesOverride ?? (chatImages ??= new ChatImageService(store, client));
    public Func<ChatMessage, CancellationToken, Task<BitmapSource>> ChatImageLoader => (message, token) => ChatImages.LoadThumbnailAsync(message, token);
    public Func<ChatMessage, CancellationToken, Task> ChatImageCopy => CopyChatImageAsync;
    public Func<ChatMessage, CancellationToken, Task> ChatImageSaveAs => SaveChatImageAsAsync;

    private void InitializeChatImages() => Closed += (_, _) => StopChatImages();
    private void StopChatImages() { chatImages?.Dispose(); ChatImagesOverride?.Dispose(); }
    private void ClearChatImageCache(string peerId) => ChatImages.ClearConversation(peerId);

    private async Task CopyChatImageAsync(ChatMessage message, CancellationToken token)
    {
        try
        {
            var original = await ChatImages.LoadOriginalAsync(message, token);
            token.ThrowIfCancellationRequested();
            ChatImages.ValidateMessage(message);
            if (exiting) return;
            if (ChatImageClipboardOverride is { } copy) copy(original); else System.Windows.Clipboard.SetImage(original);
            SetStatus("图像已复制到剪贴板。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || exiting) { }
        catch (Exception ex) { SetStatus("复制图像失败：" + ex.Message); }
    }

    private async Task SaveChatImageAsAsync(ChatMessage message, CancellationToken token)
    {
        try
        {
            ChatImages.ValidateMessage(message);
            if (!message.Outgoing)
            {
                var peer = store.GetPeer(message.PeerId) ?? throw new FileNotFoundException("设备已移除。");
                var resource = await client.GetResourceAsync(peer, message.ResourceId!, token);
                token.ThrowIfCancellationRequested();
                if (resource is null || !resource.Available) throw new FileNotFoundException("资源已撤销或原文件不可用。");
                if (!exiting) BeginDownload(peer, resource, true);
                return;
            }
            var source = ChatImages.GetOutgoingResource(message);
            var metadata = new ResourceCatalog(store).Describe(source);
            if (!metadata.Available) throw new FileNotFoundException("原图片不可用。");
            var destination = ChooseDownloadDestination(metadata, true);
            if (destination is null) return;
            var name = destination.FileName ?? source.Name;
            if (name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains(':'))
                throw new InvalidDataException("保存文件名无效。");
            var directory = Path.GetFullPath(destination.Directory);
            var saved = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                await using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
                for (var index = 1; ; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.Combine(directory, index == 1 ? name : $"{stem} ({index}){extension}");
                    FileStream output;
                    try { output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous); }
                    catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { continue; }
                    try { await using (output) await input.CopyToAsync(output, token).ConfigureAwait(false); }
                    catch { if (File.Exists(target)) File.Delete(target); throw; }
                    return target;
                }
            }, token);
            if (!exiting) SetStatus("图片已另存为：" + saved);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || exiting) { }
        catch (Exception ex) { SetStatus("图片另存为失败：" + ex.Message); }
    }
}
