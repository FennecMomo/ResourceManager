using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? singleInstance;
    private LocalControlServer? localControl;
    private LocalAiBridge? localAiBridge;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) AppLog.Write("未处理的进程异常", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("未观察的后台任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("未处理的界面异常", e.Exception);
    }

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length == 1 && e.Args[0] == "--mcp")
        {
            try { await ResourceManagerMcpHost.RunAsync(); Shutdown(); }
            catch (Exception ex) { AppLog.Write("MCP 进程运行失败", ex); Shutdown(8); }
            return;
        }
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (WindowsDesktopContext.EnsureNativeDesktop(e.Args)) { Shutdown(); return; }
        }
        catch (Exception ex)
        {
            if (background) { AppLog.Write("后台启动环境检查失败", ex); Shutdown(6); return; }
            System.Windows.MessageBox.Show($"无法确认统一的资料位置，程序尚未打开数据库。\n\n{ex.Message}",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(6);
            return;
        }
        var arguments = e.Args.Where(argument => argument != WindowsDesktopContext.RelaunchArgument).ToArray();
        if (arguments.Length == 2 && arguments[0] == "--storage-restart" && int.TryParse(arguments[1], out var parentId))
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (ArgumentException) { }
            catch (TimeoutException) { Shutdown(5); return; }
        }
        if (arguments.Length > 0 && arguments[0] == "--apply-update")
        {
            var code = await ApplyUpdateAsync(arguments.Skip(1).ToArray());
            Shutdown(code);
            return;
        }

        singleInstance = new SingleInstanceCoordinator();
        if (!singleInstance.IsPrimary)
        {
            if (!background && !arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                await SingleInstanceCoordinator.ActivatePrimaryAsync();
            Shutdown();
            return;
        }

        if (background)
        {
            try
            {
                var existing = StorageBootstrap.Locations.ReadForStartup(ex => AppLog.Write("后台启动引导配置不可用", ex))
                    ?? new StorageConfiguration(NodeDefaults.LegacyDataDirectory);
                if (existing.Pending is not null)
                    throw new IOException("存在待处理迁移；请正常打开程序完成迁移或选择新建资料。");
                existing = StorageBootstrap.Locations.PrepareForStartup(existing, StorageBootstrap.RecoveryRoot,
                    ex => AppLog.Write("后台启动原资料无法使用，已切换新资料目录", ex));
                NodeDefaults.UseDataDirectory(existing.Directory);
            }
            catch (Exception ex) { AppLog.Write("后台启动资料检查失败", ex); Shutdown(7); return; }
        }
        else if (!await StorageBootstrap.PrepareAsync()) { Shutdown(); return; }
        var startup = background || arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(startup);
        var storage = StorageBootstrap.Locations.Read()!;
        StorageBootstrap.Locations.Save(storage with { Initialized = true });
        MainWindow = window;
        singleInstance.StartListening(() => Dispatcher.BeginInvoke(window.ShowWindow));
        localControl = new LocalControlServer(window.HandleLocalControlAsync);
        localAiBridge = new LocalAiBridge(window.HandleAiBridgeAsync);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        localControl?.Dispose();
        localAiBridge?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static async Task<int> ApplyUpdateAsync(string[] args)
    {
        if (args.Length is not (3 or 4) || args[2] is not ("restart" or "no-restart")) return 2;
        int? parentProcessId = null;
        if (args.Length == 4)
        {
            if (!int.TryParse(args[3], out var parsedProcessId) || parsedProcessId <= 0) return 2;
            parentProcessId = parsedProcessId;
        }
        var target = Path.GetFullPath(args[0]);
        var expectedHash = args[1].ToLowerInvariant();
        var staged = Environment.ProcessPath is null ? "" : Path.GetFullPath(Environment.ProcessPath);
        if (target == staged || Path.GetExtension(target).ToLowerInvariant() != ".exe" || expectedHash.Length != 64) return 3;
        await using (var stream = File.OpenRead(staged))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (actual != expectedHash) return 3;
        }
        if (parentProcessId is int processId)
        {
            try
            {
                using var parent = Process.GetProcessById(processId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException)
            {
                // The original process completed before the updater began waiting.
            }
            catch (OperationCanceledException)
            {
                return 5;
            }
        }
        if (!await SingleInstanceCoordinator.WaitForPrimaryExitAsync(TimeSpan.FromMinutes(2))) return 5;
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".ResourceManager-update.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(staged, temporary, true);
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try { File.Move(temporary, target, true); break; }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(250); }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { await Task.Delay(250); }
            }
            if (AutoStartManager.IsEnabled()) AutoStartManager.SetEnabled(true, target);
            if (args[2] == "restart") Process.Start(new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target)!, UseShellExecute = true });
            return 0;
        }
        catch { return 4; }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
