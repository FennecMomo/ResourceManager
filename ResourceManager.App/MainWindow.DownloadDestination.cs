using System.IO;
using System.Windows;
using ResourceManager.Core;
namespace ResourceManager.App;

public sealed record DownloadDestination(string Directory, string? FileName = null);

public partial class MainWindow
{
    // Injected only by isolated checks; production uses the standard Windows dialogs.
    private Func<RemoteResource, bool, DownloadDestination?>? downloadDestinationPicker = null;
    private static bool IsSaveAs(object sender) => sender is FrameworkElement { Tag: "SaveAs" };

    private DownloadDestination? ChooseDownloadDestination(RemoteResource resource, bool saveAs)
    {
        if (downloadDestinationPicker is { } picker) return picker(resource, saveAs);
        if (saveAs && resource.Kind == ResourceKind.File)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "另存为（重名自动编号，不覆盖已有文件）", FileName = resource.Name,
                DefaultExt = Path.GetExtension(resource.Name), Filter = "所有文件|*.*",
                AddExtension = true, OverwritePrompt = false, CheckPathExists = true
            };
            return dialog.ShowDialog(this) == true
                ? new(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileName(dialog.FileName)) : null;
        }
        var folder = new Microsoft.Win32.OpenFolderDialog
        {
            Title = saveAs ? "另存为：选择目标目录（保留资源文件夹结构）" : "选择下载保存目录",
            Multiselect = false
        };
        return folder.ShowDialog(this) == true ? new(folder.FolderName) : null;
    }

    private DownloadJob? ChooseDownloadJob(PeerInfo peer, RemoteResource resource, bool saveAs)
    {
        var destination = ChooseDownloadDestination(resource, saveAs);
        return destination is null ? null : downloader.CreateJob(peer, resource, destination.Directory, destination.FileName);
    }
}
