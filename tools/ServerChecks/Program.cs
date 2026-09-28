using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App;
using ResourceManager.Core;
using ResourceManager.Server;

internal static class Program
{
    private static readonly string Output = Path.GetFullPath("dist/server-checks");
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    [STAThread]
    private static int Main()
    {
        var rootDir = Path.Combine(Output, "data-" + Guid.NewGuid().ToString("N"));
        NodeDefaults.UseDataDirectory(Path.Combine(rootDir, "client"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null; Host? one = null; Host? two = null;
            try
            {
                one = await Host.Start(Path.Combine(rootDir, "one")); two = await Host.Start(Path.Combine(rootDir, "two"));
                var store = new NodeStore(); using var client = new WorkspaceClient(store, "0.6.1");
                foreach (var host in new[] { one, two })
                {
                    var info = await client.InspectAsync(host.Address);
                    store.SaveServerBinding(new(Guid.NewGuid().ToString("N"), host == one ? "团队服务器" : "测试服务器", host.Address, info.ServerId, info.PublicKey, "待连接", null, null));
                }
                window = new MainWindow(false, null, desktopIntegration: false);
                T Control<T>(string name) => (T)window.FindName(name);
                Task CallAsync(string name, params object[] args) => (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args)!;
                typeof(MainWindow).GetMethod("StartServers", Private)!.Invoke(window, null);
                await Until(() => window.Servers.All(s => s.Status == "在线"));
                Require(window.Servers.Count == 2 && window.Servers.All(s => s.Members.Count == 1), "two persisted bindings connect independently");
                var otherStore = new NodeStore(Path.Combine(rootDir, "other")); using var other = new WorkspaceClient(otherStore, "0.6.1");
                otherStore.SaveSettings("另一台设备", null, 37642, true);
                var row = window.Servers.Single(s => s.Address == one.Address);
                var session = await other.JoinAsync(row.Binding);
                await Until(() => row.Members.Count == 2);
                Require(window.Servers.Single(s => s != row).Members.Count == 1, "pushed roster does not leak to second server");
                Control<ListBox>("NavList").SelectedIndex = 8;
                Control<TabControl>("ServerTabs").SelectedItem = row;
                var root = (FrameworkElement)window.Content;
                foreach (var size in new[] { (1260, 780), (1010, 630) })
                {
                    root.Measure(new Size(size.Item1, size.Item2)); root.Arrange(new Rect(0, 0, size.Item1, size.Item2)); root.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var tab = (TabItem)Control<TabControl>("ServerTabs").ItemContainerGenerator.ContainerFromIndex(0);
                    Require(tab.ActualHeight > 10 && tab.ActualWidth > 50, $"server tabs have visible header layout at {size}");
                    var bitmap = new RenderTargetBitmap(size.Item1, size.Item2, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(0, 0, size.Item1, size.Item2));
                    bitmap.Render(background); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(Output, $"servers-{size.Item1}.png")); encoder.Save(file);
                }
                typeof(MainWindow).GetField("editingServer", Private)!.SetValue(window, true);
                var control = await window.HandleLocalControlAsync(new(1, "shutdown", Environment.ProcessId), default);
                Require(!control.Success && control.Error == "editing_server", "background shutdown protects server editor");
                typeof(MainWindow).GetField("editingServer", Private)!.SetValue(window, false);
                var address = one.Address;
                await one.DisposeAsync(); one = null;
                await Until(() => row.Status.StartsWith("离线"));
                Require(row.Members.Count == 2 && row.Members.All(m => m.State == "缓存 · 待核对"), "offline roster retained and never presented as live");
                one = await Host.Start(Path.Combine(rootDir, "one"), address);
                await Until(() => row.Status == "在线");
                Require(row.Members.Count == 2, "client automatically reconnects to restarted server with original identity");
                var replacement = await client.JoinAsync(row.Binding);
                await Until(() => row.Status == "连接受限");
                await Task.Delay(1500);
                Require(row.Status == "连接受限" && row.Worker!.IsCompleted, "superseded connection stops retries instead of stealing session back");
                await CallAsync("StopServerAsync", row); store.RemoveServerBinding(row.Binding.Id); window.Servers.Remove(row);
                Require(store.GetServerBindings().Count == 1 && window.Servers.Single().Status == "在线", "remove leaves other connection active");
                Require(!window.IsVisible, "all checks ran without opening windows");
                Console.WriteLine($"PASS: {checks} isolated server UI checks.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally
            {
                if (window is not null) await (Task)typeof(MainWindow).GetMethod("StopServersAsync", Private)!.Invoke(window, null)!;
                if (one is not null) await one.DisposeAsync(); if (two is not null) await two.DisposeAsync();
                if (window is not null) await (Task)typeof(MainWindow).GetMethod("ExitAsync", Private)!.Invoke(window, null)!; else app.Shutdown();
            }
        });
        app.Run(); return result;
    }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); while (!condition()) await Task.Delay(50, timeout.Token); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public string Address => app.Urls.Single();
        public static async Task<Host> Start(string path, string address = "http://127.0.0.1:0")
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls(address);
            builder.Services.AddSingleton(new ServerStore(path, "Test")); builder.Services.AddSingleton(new ServerRuntimeOptions(path, "Test", 1, 2, 3, "0.1.0"));
            builder.Services.AddSingleton<WorkspaceHub>(); builder.Services.AddHostedService(s => s.GetRequiredService<WorkspaceHub>());
            var app = builder.Build(); app.MapWorkspace(); await app.StartAsync(); return new(app);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
