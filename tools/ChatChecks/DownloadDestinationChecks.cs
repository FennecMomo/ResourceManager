using System.IO;
using System.Windows.Controls;
using ResourceManager.App;
using ResourceManager.Core;
internal static partial class Program
{
    private static void CheckDownloadDestination(MainWindow window, NodeStore store)
    {
        var field = typeof(MainWindow).GetField("downloadDestinationPicker", Private)!;
        var method = typeof(MainWindow).GetMethod("ChooseDownloadJob", Private)!;
        var peer = store.GetPeer("peer")!;
        var file = new RemoteResource("save-test", "original.txt", ResourceKind.File, PublishMode.Reference, 4, DateTimeOffset.UtcNow, true);
        var before = store.GetDownloads().Count;
        field.SetValue(window, new Func<RemoteResource, bool, DownloadDestination?>((_, _) => null));
        Require(method.Invoke(window, [peer, file, true]) is null && store.GetDownloads().Count == before,
            "cancel Save As creates no task or download reservation");
        var destination = Path.Combine(store.DataDirectory, "chosen-save-directory");
        var wasSaveAs = false;
        field.SetValue(window, new Func<RemoteResource, bool, DownloadDestination?>((r, saveAs) =>
        { wasSaveAs = saveAs; return new(destination, r.Kind == ResourceKind.File ? "chosen.txt" : null); }));
        var job = (DownloadJob)method.Invoke(window, [peer, file, true])!;
        Require(wasSaveAs && job.TargetPath == Path.Combine(destination, "chosen.txt") && job.ResourceName == "original.txt",
            "Save As uses chosen name and directory while preserving remote metadata");
        var folder = (DownloadJob)method.Invoke(window, [peer, file with { Id = "folder", Name = "folder", Kind = ResourceKind.Folder }, true])!;
        Require(folder.TargetPath == Path.Combine(destination, "folder"), "folder Save As preserves resource directory structure");
        field.SetValue(window, null);
    }
}
