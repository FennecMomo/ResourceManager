using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        NodeDefaults.UseDataDirectory(Path.GetFullPath(Path.Combine("dist", "help-checks", "data-" + Guid.NewGuid().ToString("N"))));
        if (args.Contains("--generate-assets"))
        {
            var demoStore = new NodeStore();
            var settings = demoStore.GetSettings();
            demoStore.SaveSettings("示例用户", null, settings.ListenPort, settings.CloseToTray, settings.AutoUpdate);
        }
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

                if (args.Contains("--generate-assets"))
                {
                    ScreenshotGenerator.Generate(main, root);
                    main.Close();
                    return;
                }

                var button = (Button)main.FindName("HelpButton");
                var identity = (TextBlock)main.FindName("IdentityText");
                var header = (Grid)VisualTreeHelper.GetParent(button);
                var identityPanel = (StackPanel)VisualTreeHelper.GetParent(identity);
                Require(Grid.GetColumn(identityPanel) == 1 && Grid.GetColumn(button) == 2, "help button follows local identity in header");
                var identityRight = identityPanel.TranslatePoint(new Point(identityPanel.ActualWidth, 0), header).X;
                var buttonLeft = button.TranslatePoint(new Point(0, 0), header).X;
                Require(button.ActualWidth >= 70 && identityRight + 10 <= buttonLeft, "help button and local identity do not overlap at minimum window size");

                var help = new HelpWindow();
                var expected = new[] { "设备", "我的发布", "Git 协作", "聊天", "收藏", "下载", "反馈", "设置", "服务器", "MCP 与 AI" };
                Require(help.Chapters.Select(c => c.Title).SequenceEqual(expected), "all modules and MCP have matching help chapters");
                Require(help.Chapters.All(c => c.Steps.Count == 3 && c.Steps.All(s => !string.IsNullOrWhiteSpace(s.Screenshot)) && !string.IsNullOrWhiteSpace(c.Tip)),
                    "every chapter includes three screenshot steps and a note");
                Require(help.Chapters.Last().Tools?.Count == 12, "MCP chapter documents every current tool");
                var aiChapter = help.Chapters.Last();
                Require(aiChapter.AiSetupPrompt?.Contains("resource-manager/SKILL.md") == true &&
                    aiChapter.AiSetupPrompt.Contains("--mcp") && aiChapter.AiSetupPrompt.Contains("resource_manager_status"),
                    "MCP chapter includes an AI setup prompt and the embedded skill");
                var installedPath = @"C:\Program Files\ResourceManager\ResourceManager.exe";
                Require(HelpWindow.CreateAiSetupPrompt(installedPath).Contains(installedPath),
                    "AI setup prompt includes the current executable path");
                Require(help.FindName("CopyPromptStatus") is TextBlock,
                    "MCP chapter has copy feedback without a dialog");
                foreach (var chapter in help.Chapters)
                foreach (var step in chapter.Steps)
                {
                    var resource = Application.GetResourceStream(new Uri(step.Screenshot, UriKind.Relative));
                    Require(resource is not null, $"embedded screenshot exists: {step.Screenshot}");
                    using var stream = resource!.Stream;
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    Require(frame.PixelWidth == 1260 && frame.PixelHeight == 820,
                        $"screenshot has expected dimensions: {step.Screenshot}");
                }
                var zoomImage = new BitmapImage(new Uri($"pack://application:,,,{help.Chapters[0].Steps[0].Screenshot}", UriKind.Absolute));
                Require(zoomImage.PixelWidth == 1260, "full-size preview can resolve the embedded image");
                var helpRoot = (FrameworkElement)help.Content;
                helpRoot.Measure(new Size(800, 580));
                helpRoot.Arrange(new Rect(0, 0, 800, 580));
                helpRoot.UpdateLayout();
                if (args.Contains("--render-preview"))
                {
                    helpRoot.Measure(new Size(1020, 760));
                    helpRoot.Arrange(new Rect(0, 0, 1020, 760));
                    helpRoot.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1020, 760, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var dc = background.RenderOpen())
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(0, 0, 1020, 760));
                    bitmap.Render(background);
                    bitmap.Render(helpRoot);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var previewPath = Path.GetFullPath(Path.Combine("dist", "help-checks", "help-preview.png"));
                    Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                    using var preview = File.Create(previewPath);
                    encoder.Save(preview);
                    Console.WriteLine($"Offscreen help preview: {previewPath}");
                }
                var list = (ListBox)help.FindName("ChapterList");
                var title = (TextBlock)help.FindName("ChapterTitle");
                Console.WriteLine($"Selected chapter: {list.SelectedIndex}; rendered title: {title.Text ?? "<null>"}");
                Require(list.SelectedIndex == 0 && title.Text == "设备", "help opens at the first chapter");
                list.SelectedIndex = 9;
                helpRoot.UpdateLayout();
                Require(title.Text == "MCP 与 AI", "selecting a chapter changes displayed content");
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
