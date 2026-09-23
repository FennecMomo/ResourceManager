using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

namespace ResourceManager.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? singleInstance;

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
        if (e.Args.Length > 0 && e.Args[0] == "--apply-update")
        {
            var code = await ApplyUpdateAsync(e.Args.Skip(1).ToArray());
            Shutdown(code);
            return;
        }

        singleInstance = new SingleInstanceCoordinator();
        if (!singleInstance.IsPrimary)
        {
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                await SingleInstanceCoordinator.ActivatePrimaryAsync();
            Shutdown();
            return;
        }

        var startup = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(startup);
        MainWindow = window;
        singleInstance.StartListening(() => Dispatcher.BeginInvoke(window.ShowWindow));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
