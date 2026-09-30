using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ResourceManager.Core;
using Brushes = System.Windows.Media.Brushes;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource storageCancellation = new();
    private readonly DispatcherTimer storageTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool measuringStorage;
    private bool restartForStorage;
    private FileSystemWatcher? storageWatcher;
    private volatile bool storageDirty = true;
    private DateTimeOffset nextStorageScan;
    private long? measuredProgramBytes;

    private void InitializeStorage()
    {
        StoragePathText.Text = store.DataDirectory;
        var previous = StorageBootstrap.Locations.Read()?.PreviousDirectory;
        OldStorageButton.Visibility = previous is null ? Visibility.Collapsed : Visibility.Visible;
        storageTimer.Tick += async (_, _) => await RefreshStorageUsageAsync();
        storageTimer.Start();
        storageWatcher = new FileSystemWatcher(store.DataDirectory)
        {
            IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        storageWatcher.Changed += (_, _) => storageDirty = true;
        storageWatcher.Created += (_, _) => storageDirty = true;
        storageWatcher.Deleted += (_, _) => storageDirty = true;
        storageWatcher.Renamed += (_, _) => storageDirty = true;
        storageWatcher.Error += (_, _) => storageDirty = true;
        _ = RefreshStorageUsageAsync();
    }

    private async Task RefreshStorageUsageAsync()
    {
        if (measuringStorage || exiting) return;
        // Disk availability is refreshed every minute; full scans are limited to once per five minutes.
        measuringStorage = true;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(store.DataDirectory)!);
            var available = drive.AvailableFreeSpace;
            var total = drive.TotalSize;
            DiskUsedText.Text = $"磁盘已用  {StorageLocationDialog.FormatBytes(total - available)}";
            DiskFreeText.Text = $"剩余  {StorageLocationDialog.FormatBytes(available)}";
            DiskUsageBar.Value = total > 0 ? 100d * (total - available) / total : 0;
            var lowSpace = new StorageUsage(total, available, 0).LowSpace;
            DiskFreeText.Foreground = lowSpace ? Brushes.Firebrick : Brushes.SlateGray;
            DiskUsageBar.Foreground = lowSpace ? Brushes.Firebrick : Brushes.SteelBlue;
            StorageWarningText.Text = lowSpace ? "存储盘剩余空间较低，请及时清理。" : "本程序占用包含在磁盘已用中。";
            if (storageDirty && DateTimeOffset.UtcNow >= nextStorageScan)
            {
                storageDirty = false;
                var usage = await Task.Run(() => StorageLocation.Measure(store.DataDirectory, storageCancellation.Token));
                measuredProgramBytes = usage.ProgramBytes;
                ProgramSizeText.Text = $"本程序  {StorageLocationDialog.FormatBytes(usage.ProgramBytes)}";
                nextStorageScan = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            StorageUsagePanel.ToolTip = $"{store.DataDirectory}\n总容量：{total:N0} 字节\n磁盘已用：{total - available:N0} 字节\n本程序：{measuredProgramBytes?.ToString("N0") ?? "正在统计"} 字节\n剩余：{available:N0} 字节";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            storageDirty = true;
            ProgramSizeText.Text = "本程序  暂时无法统计";
            StorageWarningText.Text = "存储位置不可用或统计失败";
            StorageUsagePanel.ToolTip = ex.Message;
        }
        finally { measuringStorage = false; }
    }

    private async void ChangeStorage_Click(object sender, RoutedEventArgs e)
    {
        if (exiting) return;
        if (stagedUpdate is not null || checkingUpdate)
        {
            System.Windows.MessageBox.Show(this, "请先完成本次软件更新，再迁移存储位置。", "暂时无法迁移");
            return;
        }
        var dialog = new StorageLocationDialog(store.DataDirectory,
            "选择新的空文件夹。确认后会重启程序，在共享服务启动前复制并校验资料，成功后切换。原目录保留。", true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var target = dialog.SelectedPath;
            if (System.Windows.MessageBox.Show(this, $"将存储位置迁移到：\n{target}\n\n程序会暂停下载、停止共享并重启。是否继续？",
                    "迁移存储位置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!await CanExitWithSettingsAsync()) return;
            var configuration = StorageBootstrap.Locations.Read() ?? new StorageConfiguration(store.DataDirectory, true);
            StorageBootstrap.Locations.ScheduleMigration(configuration, target);
            restartForStorage = true;
            await ExitAsync();
        }
        catch (Exception ex) { ShowError("无法迁移存储位置", ex); }
    }

    private void OpenOldStorage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var previous = StorageBootstrap.Locations.Read()?.PreviousDirectory;
            if (previous is null || !Directory.Exists(previous)) return;
            if (System.Windows.MessageBox.Show(this, $"旧目录：\n{previous}\n\n请先确认新位置的资料完整。引用发布的原文件和自行选择的下载路径可能仍在旧目录中，请保留这些文件。\n\n打开旧目录检查并手动清理？",
                    "清理旧位置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { previous } });
        }
        catch (Exception ex) { ShowError("无法打开旧目录", ex); }
    }
}
