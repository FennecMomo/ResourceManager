using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Output = Path.GetFullPath("dist/publication-checks");
    private static int checks;
    [STAThread]
    private static int Main()
    {
        var rootDir = Path.Combine(Output, Guid.NewGuid().ToString("N"));
        NodeDefaults.UseDataDirectory(Path.Combine(rootDir, "data"));
        var folder = Path.Combine(rootDir, "项目文件夹"); Directory.CreateDirectory(Path.Combine(folder, "子文件夹"));
        Directory.CreateDirectory(Path.Combine(folder, ".git")); File.WriteAllText(Path.Combine(folder, ".git", "config"), "not shared");
        var file = Path.Combine(folder, "项目说明.txt"); File.WriteAllText(file, "隔离测试内容");
        File.WriteAllText(Path.Combine(folder, "子文件夹", "资料.txt"), "nested");
        var store = new NodeStore(); var group = store.SaveResourceGroup("项目资料"); var sub = store.SaveResourceGroup("项目 A", group.Id);
        var publishedFolder = store.AddResource(folder, PublishMode.Reference, groupId: sub.Id);
        var publishedFile = store.AddResource(file, PublishMode.Copy);
        store.SetResourceNote(publishedFolder.Id, "项目交付资料");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var settings = new SettingsEditorServices(() => false, _ => { }, (_, _) => Task.FromResult(true), _ => SettingsLeaveChoice.KeepEditing,
            (message, error) => throw new Exception(message, error));
        var window = new MainWindow(false, settings, false, () => Task.CompletedTask);
        var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                T Control<T>(string name) => (T)window.FindName(name);
                IEnumerable<ResourceTreeNode> All(IEnumerable<ResourceTreeNode> nodes)
                { foreach (var n in nodes) { yield return n; foreach (var c in All(n.Children)) yield return c; } }
                var root = (FrameworkElement)window.Content;
                void Layout(int width = 1260, int height = 780)
                {
                    root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Control<ListBox>("NavList").SelectedIndex = 1;
                Layout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var folderNode = All(window.LocalResourceTree).Single(n => n.LocalRow?.Resource.Id == publishedFolder.Id);
                Require(window.LocalResourceTree.Count == 2 && All(window.LocalResourceTree).Count(n => n.IsGroup) == 3, "group hierarchy builds with permanent default group");
                Require(folderNode.Children.Count == 1 && !folderNode.ChildrenLoaded, "published folder loads contents only on expansion");
                await window.LoadPublicationChildrenAsync(folderNode);
                Require(folderNode.Children.Count == 2 && folderNode.Children.All(n => n.Name != ".git"), "folder expands actual files and folders excluding git metadata");
                folderNode.IsExpanded = true; Layout();
                folderNode.IsSelected = true; Layout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(Control<TextBlock>("LocalDetailName").Text == publishedFolder.Name && Control<StackPanel>("LocalResourceActions").Visibility == Visibility.Visible,
                    "selected published folder displays details and publishing actions");
                Require(Control<TextBlock>("LocalDetailInfo").Text.Contains("项目资料 / 项目 A") && Control<TextBox>("LocalNoteBox").Text == "项目交付资料",
                    "details include group path and existing shared note");
                var child = folderNode.Children.Single(n => n.Name == "项目说明.txt"); child.IsSelected = true; Layout();
                Require(Control<TextBlock>("LocalDetailName").Text == child.Name && Control<StackPanel>("LocalResourceActions").Visibility == Visibility.Collapsed &&
                    !Control<Button>("RemoveResourceButton").IsEnabled, "nested file displays details without pretending to be a separately revocable publication");
                var nested = folderNode.Children.Single(n => n.IsFolder); await window.LoadPublicationChildrenAsync(nested);
                Require(nested.Children.Single().Name == "资料.txt", "nested folders expand correctly");
                var defaultNode = window.LocalResourceTree.Single(n => n.GroupId == "default"); defaultNode.IsSelected = true; Layout();
                Require(!Control<Button>("DeleteGroupButton").IsEnabled && !Control<Button>("RenameGroupButton").IsEnabled, "default group mutation controls disabled");
                var groupNode = All(window.LocalResourceTree).Single(n => n.IsGroup && n.GroupId == sub.Id); groupNode.IsSelected = true; Layout();
                Require(Control<Button>("DeleteGroupButton").IsEnabled && Control<Button>("MovePublicationButton").IsEnabled, "ordinary group offers rename move and delete");
                folderNode.IsSelected = true; Layout();
                for (var attempt = 0; attempt < 20 && Control<TextBlock>("LocalDetailInfo").Text.Contains("选中后统计"); attempt++) await Task.Delay(25);
                Require(!Control<TextBlock>("LocalDetailInfo").Text.Contains("选中后统计"), "selected folder size completes asynchronously");
                foreach (var size in new[] { (1260, 780), (1010, 630) })
                {
                    Layout(size.Item1, size.Item2);
                    var tree = Control<TreeView>("LocalGrid"); var publish = Control<Button>("PublishButton");
                    var treePosition = tree.TranslatePoint(new Point(), root); var publishPosition = publish.TranslatePoint(new Point(), root);
                    Require(publishPosition.X > treePosition.X + tree.ActualWidth && publishPosition.X + publish.ActualWidth <= size.Item1 && publishPosition.Y < 180,
                        $"single publish button remains top-right beside the file tree at {size}");
                    Render(root, size.Item1, size.Item2);
                }
                var beforeBatch = store.GetResources().Count;
                var batch = window.PublishPathsAsync([file, folder, Path.Combine(rootDir, "missing-file")], PublishMode.Reference, sub.Id);
                var control = await window.HandleLocalControlAsync(new LocalControlRequest(1, "shutdown", Environment.ProcessId), default);
                // A completed short task may already be idle; a running one must reject background shutdown.
                Require(batch.IsCompleted || (!control.Success && control.Error == "publishing_resources"), "background shutdown respects an in-progress publication");
                var failures = await batch;
                var added = store.GetResources().Where(r => r.Id != publishedFile.Id && r.Id != publishedFolder.Id).ToArray();
                Require(failures.Count == 1 && store.GetResources().Count == beforeBatch + 2 && added.Any(r => r.Kind == ResourceKind.File) && added.Any(r => r.Kind == ResourceKind.Folder) && added.All(r => r.GroupId == sub.Id),
                    "mixed batch follows file/folder flow, preserves target group, and reports only failed paths");
                Require(Control<Button>("PublishButton").IsEnabled, "publish button re-enables after partial batch failure");
                var remote = new ObservableCollection<ResourceTreeNode>();
                var cyclic = MainWindow.BuildGroupTree([new("x", "X", "y", 1, DateTimeOffset.UtcNow), new("y", "Y", "x", 2, DateTimeOffset.UtcNow)], remote);
                Require(remote.Count == 3 && cyclic.Count == 3, "malformed remote cycles are flattened without recursion");
                Require(!window.IsVisible, "all UI checks stayed offscreen");
                Console.WriteLine($"PASS: {checks} isolated publication checks.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { await (Task)typeof(MainWindow).GetMethod("ExitAsync", Private)!.Invoke(window, null)!; }
        });
        app.Run(); return result;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Render(FrameworkElement root, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, $"publications-{width}.png")); encoder.Save(stream);
    }
}
