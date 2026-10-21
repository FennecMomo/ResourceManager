using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    internal const long MaxChatImageBytes = 20L * 1024 * 1024;
    internal const long MaxChatBitmapPixels = 16_000_000;
    private bool preparingChatClipboardImage;
    internal Task ChatClipboardPreparationTask { get; private set; } = Task.CompletedTask;
    internal Func<System.Windows.IDataObject?>? ChatClipboardReader { get; set; }

    private void InitializeChatClipboard()
    {
        // A TextBox will not execute Paste for an image-only clipboard. Read it only
        // on the actual Ctrl+V gesture, while leaving its ordinary text paste intact.
        ChatInput.PreviewKeyDown += ChatClipboard_PreviewKeyDown;
        System.Windows.DataObject.AddPastingHandler(ChatInput, ChatClipboard_Pasting);
    }

    private void ChatClipboard_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            var data = ChatClipboardReader is { } reader ? reader() : System.Windows.Clipboard.GetDataObject();
            if (data is not null && TryBeginChatImageSend(data, SelectedChatPeerId, false)) e.Handled = true;
        }
        catch (Exception ex)
        {
            e.Handled = true;
            SetStatus("无法读取剪贴板图片，请稍后重试。");
            AppLog.Write("读取聊天剪贴板失败", ex);
        }
    }

    private void ChatClipboard_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TryBeginChatImageSend(e.DataObject, SelectedChatPeerId, false)) e.CancelCommand();
    }

    internal async Task<bool> PasteChatImageAsync(System.Windows.IDataObject data)
    {
        var handled = TryBeginChatImageSend(data, SelectedChatPeerId, false);
        if (handled) await ChatClipboardPreparationTask;
        return handled;
    }

    private static bool IsChatImagePath(string path) => !Directory.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private static bool HasChatBitmap(System.Windows.IDataObject data) => data.GetDataPresent(System.Windows.DataFormats.Bitmap, true);

    private sealed record ChatImageSnapshot(string[] Paths, BitmapSource? Bitmap, string? Error = null);

    private static ChatImageSnapshot? CaptureChatImage(System.Windows.IDataObject data, bool dropped)
    {
        if (data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var paths = ChatDropPaths(data);
            if (paths.Length == 0 || !paths.Any(IsChatImagePath)) return null;
            if (!paths.All(IsChatImagePath)) return dropped ? null : new([], null, "图片与其他文件请一起拖入聊天区，再选择发送方式。");
            return paths.Length > 16 ? new([], null, "一次最多发送 16 张图片。") : new(paths.ToArray(), null);
        }
        if (!HasChatBitmap(data)) return null;
        if (data.GetData(System.Windows.DataFormats.Bitmap, true) is not BitmapSource bitmap)
            return new([], null, "剪贴板图片格式无法读取，请保存图片后拖入发送。");
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > MaxChatBitmapPixels)
            return new([], null, "图片尺寸过大，最多支持 1600 万像素。");
        // The clipboard may change as soon as this event returns. Clone on the UI
        // thread before starting any asynchronous preparation or switching peers.
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        var source = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        source.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var snapshot = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32,
            null, pixels, bitmap.PixelWidth * 4);
        snapshot.Freeze();
        return new([], snapshot);
    }

    private bool TryBeginChatImageSend(System.Windows.IDataObject data, string? peerSnapshot, bool dropped)
    {
        ChatImageSnapshot? snapshot;
        try { snapshot = CaptureChatImage(data, dropped); }
        catch (Exception ex)
        {
            SetStatus("无法读取图片，请重新复制或拖入后重试。");
            AppLog.Write("获取聊天图片快照失败", ex);
            return true;
        }
        if (snapshot is null) return false;
        if (preparingChatClipboardImage || !CanSendPrivateFiles(peerSnapshot))
        {
            SetStatus("请先选择支持私发的设备，并等待当前准备完成。");
            return true;
        }
        ChatClipboardPreparationTask = SendCapturedChatImageAsync(peerSnapshot!, snapshot);
        return true;
    }

    private async Task SendCapturedChatImageAsync(string peerSnapshot, ChatImageSnapshot snapshot)
    {
        preparingChatClipboardImage = true;
        RefreshChatHeader();
        string? temporaryDirectory = null;
        string? temporaryImage = null;
        try
        {
            if (snapshot.Error is { } error) throw new InvalidDataException(error);
            var paths = snapshot.Paths;
            if (snapshot.Bitmap is { } bitmap)
            {
                temporaryDirectory = Path.Combine(store.DataDirectory, "chat-clipboard", "paste-" + Guid.NewGuid().ToString("N"));
                temporaryImage = Path.Combine(temporaryDirectory, "聊天图片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                var destination = temporaryImage;
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(temporaryDirectory);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    encoder.Save(stream);
                    if (stream.Length > MaxChatImageBytes) throw new InvalidDataException("图片文件过大，单张最多支持 20 MiB。");
                });
                paths = [temporaryImage];
            }
            else
            {
                await Task.Run(() =>
                {
                    foreach (var path in paths)
                    {
                        var file = new FileInfo(path);
                        if (!file.Exists) throw new FileNotFoundException("图片不存在或已经移动。", path);
                        if (file.Length > MaxChatImageBytes) throw new InvalidDataException("图片文件过大，单张最多支持 20 MiB。");
                    }
                });
            }
            if (exiting) return;
            if (!CanSendPrivateFiles(peerSnapshot))
            {
                SetStatus("图片尚未发送：目标设备已不可用，或正在准备其他文件。");
                return;
            }
            await SendPrivateChatBatchAsync(peerSnapshot, paths, PublishMode.Copy);
        }
        catch (Exception ex)
        {
            if (!exiting) SetStatus("图片发送失败：" + ex.Message);
            AppLog.Write("准备聊天图片失败", ex);
        }
        finally
        {
            // Only this operation's generated PNG and empty directory are removed.
            // Original dropped files and the private resource's managed copy stay intact.
            if (temporaryImage is not null)
            {
                try { File.Delete(temporaryImage); }
                catch (Exception ex) { AppLog.Write("清理聊天图片临时文件失败", ex); }
            }
            if (temporaryDirectory is not null)
            {
                try { Directory.Delete(temporaryDirectory, false); }
                catch (Exception ex) { AppLog.Write("清理聊天图片临时目录失败", ex); }
            }
            preparingChatClipboardImage = false;
            RefreshChatHeader();
        }
    }
}
