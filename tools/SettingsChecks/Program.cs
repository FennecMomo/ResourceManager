using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

// Runs real WPF event handlers on an STA dispatcher, never shows a window or tray icon,
// uses a fresh database and substitutes registry/network/dialog side effects.
internal static class Program
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Output = Path.GetFullPath("dist/settings-checks");

    [STAThread]
    private static int Main()
    {
        NodeDefaults.UseDataDirectory(Path.Combine(Output, "data-" + Guid.NewGuid().ToString("N")));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var autoStart = false;
        var registryFails = false;
        var prompts = 0;
        var errors = new List<string>();
        var choice = SettingsLeaveChoice.KeepEditing;
        Func<Task<bool>> network = () => Task.FromResult(true);
        var services = new SettingsEditorServices(() => autoStart,
            value => { if (registryFails && value) throw new IOException("simulated registry failure"); autoStart = value; },
            (_, _) => network(), _ => { prompts++; return choice; }, (message, _) => errors.Add(message));
        var window = new MainWindow(false, services, desktopIntegration: false);
        var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                T Control<T>(string name) => (T)window.FindName(name);
                bool Dirty() => (bool)typeof(MainWindow).GetProperty("HasUnsavedSettings", Private)!.GetValue(window)!;
                object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
                async Task Navigate(int page)
                {
                    Control<ListBox>("NavList").SelectedIndex = page;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                var store = new NodeStore();
                var name = Control<TextBox>("NicknameBox");
                var port = Control<TextBox>("ListenPortBox");
                var tabs = Control<TabControl>("Tabs");
                var nav = Control<ListBox>("NavList");
                var originalName = name.Text;
                var originalPort = port.Text;
                await Navigate(7);
                Require(!Dirty() && !window.IsVisible, "initial clean and hidden");
                name.Text = "draft";
                await Navigate(0);
                Require(tabs.SelectedIndex == 7 && nav.SelectedIndex == 7 && name.Text == "draft", "cancel keeps both selectors and draft");
                name.Text = originalName;
                var before = prompts;
                await Navigate(0);
                Require(tabs.SelectedIndex == 0 && prompts == before, "reverted edit needs no prompt");
                await Navigate(7);
                foreach (var id in new[] { "CloseToTrayBox", "AutoStartBox" })
                {
                    var box = Control<CheckBox>(id);
                    box.IsChecked = !box.IsChecked;
                    Require(Dirty(), id + " dirty");
                    box.IsChecked = !box.IsChecked;
                    Require(!Dirty(), id + " reverted");
                }
                typeof(MainWindow).GetField("pendingAvatar", Private)!.SetValue(window, new byte[] { 1, 2, 3 });
                Require(Dirty(), "avatar dirty");
                choice = SettingsLeaveChoice.Discard;
                name.Text = "discard";
                port.Text = "bad";
                await Navigate(1);
                Require(tabs.SelectedIndex == 1 && !Dirty() && name.Text == originalName && port.Text == originalPort, "discard restores all fields");
                await Navigate(7);
                choice = SettingsLeaveChoice.Save;
                port.Text = "65536";
                await Navigate(2);
                Require(tabs.SelectedIndex == 7 && port.Text == "65536" && errors.Count == 1, "invalid port blocks navigation without losing draft");
                port.Text = originalPort;
                name.Text = "   ";
                await Navigate(2);
                Require(tabs.SelectedIndex == 7 && name.Text == "   ", "invalid nickname stays");
                name.Text = "  Saved profile  ";
                await Navigate(2);
                Require(tabs.SelectedIndex == 2 && !Dirty() && store.GetSettings().Profile.Nickname == "Saved profile", "save and leave persists normalized profile");
                await Navigate(7);
                Control<CheckBox>("AutoStartBox").IsChecked = true;
                registryFails = true;
                await Navigate(3);
                Require(tabs.SelectedIndex == 7 && Dirty() && !autoStart, "autostart failure retains draft");
                registryFails = false;
                Call("DiscardSettingsEdits");

                var applying = new TaskCompletionSource<bool>();
                network = () => applying.Task;
                name.Text = "first save";
                nav.SelectedIndex = 4;
                Require(tabs.SelectedIndex == 7 && !Control<Button>("SaveSettingsButton").IsEnabled, "save awaits network on settings page");
                name.Text = "second edit";
                nav.SelectedIndex = 5;
                Require(tabs.SelectedIndex == 7 && nav.SelectedIndex == 7, "concurrent navigation cannot bypass save");
                applying.SetResult(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(tabs.SelectedIndex == 7 && Dirty() && name.Text == "second edit" && store.GetSettings().Profile.Nickname == "first save", "edits during save are not marked saved");
                Call("DiscardSettingsEdits");
                network = () => Task.FromException<bool>(new IOException("simulated port failure"));
                name.Text = "persisted before network failure";
                await Navigate(0);
                Require(tabs.SelectedIndex == 7 && name.Text == store.GetSettings().Profile.Nickname && errors.Last().Contains("设置已保存"), "network failure accurately reports persisted settings and stays");
                network = () => Task.FromResult(true);

                var gateway = store.SaveGateway("Router one", "192.168.12.1");
                var second = store.SaveGateway("Router two", "192.168.13.1");
                Call("RefreshGatewaysView", gateway.Id);
                var gatewayName = Control<TextBox>("GatewayNameBox");
                var gatewayIp = Control<TextBox>("GatewayIpBox");
                gatewayName.Text = "router draft";
                gatewayIp.Text = "invalid ip";
                Call("RefreshGatewaysView", second.Id);
                Require(gatewayName.Text == "router draft" && gatewayIp.Text == "invalid ip" && Dirty(), "background refresh preserves router draft and selection");
                var refusal = await window.HandleLocalControlAsync(new LocalControlRequest(1, "shutdown", Environment.ProcessId), default);
                Require(!refusal.Success && refusal.Error == "unsaved_settings", "control shutdown protects router draft");
                await Navigate(0);
                Require(tabs.SelectedIndex == 7 && gatewayIp.Text == "invalid ip", "invalid router save blocks leaving");
                choice = SettingsLeaveChoice.KeepEditing;
                Control<ListBox>("GatewayList").SelectedItem = window.Gateways.First(row => row.Gateway.Id == second.Id);
                Require(((GatewayRow)Control<ListBox>("GatewayList").SelectedItem).Gateway.Id == gateway.Id && gatewayName.Text == "router draft", "router selection cancel preserves original draft");
                gatewayIp.Text = "192.168.12.2";
                choice = SettingsLeaveChoice.Save;
                await Navigate(0);
                Require(tabs.SelectedIndex == 0 && !Dirty() && store.GetGateways().Single(g => g.Id == gateway.Id).WanIp == "192.168.12.2", "save and leave persists router draft without scanning");
                await Navigate(7);
                gatewayName.Text = "discard router";
                choice = SettingsLeaveChoice.Discard;
                await Navigate(0);
                Require(!Dirty() && gatewayName.Text == "router draft", "discard router restores baseline");
                await Navigate(7);
                name.Text = "top save";
                gatewayName.Text = "independent draft";
                Require(await (Task<bool>)Call("SaveSettingsAsync")! && Dirty() && gatewayName.Text == "independent draft", "top save leaves independently saved router draft intact");
                Call("DiscardSettingsEdits");

                gatewayName.Text = "saved when switching";
                choice = SettingsLeaveChoice.Save;
                Control<ListBox>("GatewayList").SelectedItem = window.Gateways.First(row => row.Gateway.Id == second.Id);
                Require(!Dirty() && gatewayName.Text == "Router two" && store.GetGateways().Single(g => g.Id == gateway.Id).Name == "saved when switching",
                    "switching router saves original before loading target");
                gatewayName.Text = "discard on switch";
                choice = SettingsLeaveChoice.Discard;
                Control<ListBox>("GatewayList").SelectedItem = window.Gateways.First(row => row.Gateway.Id == gateway.Id);
                Require(!Dirty() && gatewayName.Text == "saved when switching" && store.GetGateways().Single(g => g.Id == second.Id).Name == "Router two",
                    "switching router discards only draft");
                Call("RemoveGateway_Click", window, new RoutedEventArgs());
                Require(!Dirty() && gatewayName.Text == "" && gatewayIp.Text == "", "removing router clears editor baseline");
                gatewayName.Text = "New router";
                gatewayIp.Text = "192.168.14.1";
                name.Text = "combined save";
                choice = SettingsLeaveChoice.Save;
                await Navigate(0);
                Require(tabs.SelectedIndex == 0 && !Dirty() && store.GetSettings().Profile.Nickname == "combined save" &&
                    store.GetGateways().Any(g => g.Name == "New router" && g.WanIp == "192.168.14.1"), "save and leave persists profile plus new router");
                await Navigate(7);

                var root = (FrameworkElement)window.Content;
                var scroll = Control<ScrollViewer>("SettingsScrollViewer");
                var save = Control<Button>("SaveSettingsButton");
                foreach (var size in new[] { (1260, 780), (1010, 630) })
                {
                    Render(root, size.Item1, size.Item2, "top");
                    var position = save.TranslatePoint(new Point(), root);
                    scroll.ScrollToEnd();
                    Render(root, size.Item1, size.Item2, "bottom");
                    var after = save.TranslatePoint(new Point(), root);
                    Require(Math.Abs(position.Y - after.Y) < 1 && after.Y >= 0 && after.Y + save.ActualHeight < size.Item2 && scroll.VerticalOffset > 0,
                        $"save remains visible after scrolling at {size}");
                    scroll.ScrollToTop();
                }
                Require(!window.IsVisible && !((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(window)!).Visible,
                    "entire test stayed invisible with no tray icon");
                Console.WriteLine($"PASS: {checks} isolated settings checks; no live data, registry, network or desktop interaction.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { await (Task)typeof(MainWindow).GetMethod("ExitAsync", Private)!.Invoke(window, null)!; }
        });
        app.Run();
        return result;
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new Exception(check);
        checks++;
        Console.WriteLine("PASS " + check);
    }

    private static void Render(FrameworkElement root, int width, int height, string suffix)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 249, 252)), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, $"settings-{width}-{suffix}.png"));
        encoder.Save(stream);
    }
}
