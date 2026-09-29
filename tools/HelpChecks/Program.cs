using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main()
    {
        NodeDefaults.UseDataDirectory(Path.GetFullPath(Path.Combine("dist", "help-checks", "data-" + Guid.NewGuid().ToString("N"))));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var result = 0;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var services = new SettingsEditorServices(() => false,
                    _ => throw new InvalidOperationException("unexpected registry write"),
                    (_, _) => throw new InvalidOperationException("unexpected network restart"),
                    _ => SettingsLeaveChoice.KeepEditing,
                    (message, error) => throw new Exception(message, error));
                var main = new MainWindow(false, services, false, () => Task.CompletedTask);
                var root = (FrameworkElement)main.Content;
                root.Measure(new Size(1010, 670));
                root.Arrange(new Rect(0, 0, 1010, 670));
                root.UpdateLayout();

                var button = (Button)main.FindName("HelpButton");
                var identity = (TextBlock)main.FindName("IdentityText");
                var header = (Grid)VisualTreeHelper.GetParent(button);
                var identityPanel = (StackPanel)VisualTreeHelper.GetParent(identity);
                Require(Grid.GetColumn(identityPanel) == 1 && Grid.GetColumn(button) == 2, "help button follows local identity in header");
                var identityRight = identityPanel.TranslatePoint(new Point(identityPanel.ActualWidth, 0), header).X;
                var buttonLeft = button.TranslatePoint(new Point(0, 0), header).X;
                Require(button.ActualWidth >= 70 && identityRight + 10 <= buttonLeft, "help button and local identity do not overlap at minimum window size");

                var help = new HelpWindow();
                var expected = new[] { "设备", "我的发布", "Git 协作", "聊天", "收藏", "下载", "反馈", "设置", "服务器" };
                Require(help.Chapters.Select(c => c.Title).SequenceEqual(expected), "all nine navigation modules have matching help chapters");
                Require(help.Chapters.All(c => c.Illustration.Count == 3 && c.Steps.Count >= 3 && !string.IsNullOrWhiteSpace(c.Tip)),
                    "every chapter includes an illustrated flow, steps, and a note");
                var helpRoot = (FrameworkElement)help.Content;
                helpRoot.Measure(new Size(800, 580));
                helpRoot.Arrange(new Rect(0, 0, 800, 580));
                helpRoot.UpdateLayout();
                var list = (ListBox)help.FindName("ChapterList");
                var title = (TextBlock)help.FindName("ChapterTitle");
                Console.WriteLine($"Selected chapter: {list.SelectedIndex}; rendered title: {title.Text ?? "<null>"}");
                Require(list.SelectedIndex == 0 && title.Text == "设备", "help opens at the first chapter");
                list.SelectedIndex = 8;
                helpRoot.UpdateLayout();
                Require(title.Text == "服务器", "selecting a chapter changes displayed content");
                Require(!main.IsVisible && !help.IsVisible, "layout checks never show a window");
                Console.WriteLine($"Help checks passed: {checks}");
                help.Close();
                main.Close();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                result = 1;
            }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
}
