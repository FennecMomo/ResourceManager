using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;

namespace ResourceManager.App;

internal sealed class StorageLocationDialog : Window
{
    private readonly TextBox pathBox;
    public string SelectedPath => StorageLocation.Normalize(pathBox.Text.Trim());

    public StorageLocationDialog(string initialPath, string description, bool migration = false)
    {
        Title = migration ? "更改存储位置" : "确认存储位置";
        Width = 610;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Foreground = new SolidColorBrush(Color.FromRgb(27, 43, 67));
        Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Style TargetType="Button">
                    <Setter Property="Background" Value="#EAF1FA"/>
                    <Setter Property="Foreground" Value="#1B2B43"/>
                    <Setter Property="Cursor" Value="Hand"/>
                    <Setter Property="FontSize" Value="13"/>
                    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                        <Border x:Name="Frame" Background="{TemplateBinding Background}" CornerRadius="8"
                                BorderBrush="#D6E2F2" BorderThickness="1" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.8"/></Trigger>
                            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#2867D9"/></Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate></Setter.Value></Setter>
                </Style>
                <Style TargetType="TextBox"><Setter Property="BorderBrush" Value="#CCD9EA"/><Setter Property="BorderThickness" Value="1"/></Style>
            </ResourceDictionary>
            """);
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18) });
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                var button = new Button
                {
                    Content = $"{drive.Name}    剩余 {FormatBytes(drive.AvailableFreeSpace)} / 总容量 {FormatBytes(drive.TotalSize)}",
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
                };
                var root = drive.RootDirectory.FullName;
                button.Click += (_, _) => pathBox!.Text = Path.Combine(root, "ResourceManager");
                panel.Children.Add(button);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        pathBox = new TextBox { Text = initialPath, Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(pathBox);
        var browse = new Button { Content = "选择文件夹…", HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择 ResourceManager 存储位置" };
            if (picker.ShowDialog(this) == true) pathBox.Text = picker.FolderName;
        };
        panel.Children.Add(browse);
        panel.Children.Add(new TextBlock
        {
            Text = "数据库、聊天记录、发布副本、反馈附件、Git 协作资料、日志和更新包将保存在这里。普通下载仍使用你选择的下载位置。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 16, 0, 18)
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0) };
        var confirm = new Button { Content = migration ? "确认位置" : "确认并继续", IsDefault = true, Padding = new Thickness(18, 8, 18, 8),
            Background = new SolidColorBrush(Color.FromRgb(40, 103, 217)), Foreground = Brushes.White };
        confirm.Click += (_, _) =>
        {
            try { _ = SelectedPath; DialogResult = true; }
            catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "存储位置不可用"); }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
    }

    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0.0} MB";
}

internal static class StorageBootstrap
{
    public static readonly StorageLocation Locations = new(StorageLocation.DefaultConfigurationPath);

    public static async Task<bool> PrepareAsync()
    {
        while (true)
        {
            StorageConfiguration? configuration = null;
            try
            {
                configuration = Locations.Read();
                if (configuration is null)
                {
                    var legacy = NodeDefaults.LegacyDataDirectory;
                    var hasLegacy = File.Exists(Path.Combine(legacy, "resources.db"));
                    var dialog = new StorageLocationDialog(legacy, hasLegacy
                        ? "发现旧版资料。可继续使用原位置，或选择空文件夹迁移已有数据。原数据会保留。"
                        : "首次使用请确认存储位置。可以使用默认目录，也可以选择其他磁盘。");
                    if (dialog.ShowDialog() != true) return false;
                    var chosen = dialog.SelectedPath;
                    if (hasLegacy && !chosen.Equals(legacy, StringComparison.OrdinalIgnoreCase))
                        configuration = Locations.ScheduleMigration(new StorageConfiguration(legacy, true), chosen);
                    else
                    {
                        if (!chosen.Equals(legacy, StringComparison.OrdinalIgnoreCase) && Directory.Exists(chosen) &&
                            Directory.EnumerateFileSystemEntries(chosen).Any() && !File.Exists(Path.Combine(chosen, "resources.db")))
                            throw new IOException("请选择空文件夹，或包含 ResourceManager 原资料的目录。");
                        StorageLocation.ValidateWritable(chosen);
                        configuration = new StorageConfiguration(chosen, File.Exists(Path.Combine(chosen, "resources.db")));
                        Locations.Save(configuration);
                    }
                }
                if (configuration.Pending is not null)
                {
                    using var cancellation = new CancellationTokenSource();
                    var label = new TextBlock { Text = "正在准备迁移…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
                    var cancel = new Button { Content = "取消迁移", Margin = new Thickness(24, 0, 24, 16), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                    cancel.Click += (_, _) => { cancellation.Cancel(); cancel.IsEnabled = false; label.Text = "正在停止，原数据保持不变…"; };
                    var content = new StackPanel();
                    content.Children.Add(label);
                    content.Children.Add(cancel);
                    var progressWindow = new Window
                    {
                        Title = "迁移存储位置", Width = 550, SizeToContent = SizeToContent.Height, Content = content,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize
                    };
                    var running = true;
                    progressWindow.Closing += (_, e) => e.Cancel = running;
                    progressWindow.Show();
                    try
                    {
                        var progress = new Progress<string>(text => label.Text = text);
                        configuration = await Task.Run(() => Locations.CompleteMigrationAsync(configuration, progress, cancellation.Token));
                    }
                    finally { running = false; progressWindow.Close(); }
                    System.Windows.MessageBox.Show($"存储位置已切换到：\n{configuration.Directory}\n\n原数据保留在：\n{configuration.PreviousDirectory}\n\n确认资料完整后，可在设置中打开旧目录，自行清理不再需要的文件。", "迁移完成");
                }
                StorageLocation.ValidateCurrent(configuration);
                NodeDefaults.UseDataDirectory(configuration.Directory);
                return true;
            }
            catch (Exception ex)
            {
                var result = System.Windows.MessageBox.Show($"{ex.Message}\n\n尚未启动共享服务。\n“是”：重试；“否”：选择已有数据目录；“取消”：退出。",
                    "存储位置不可用", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel) return false;
                if (result == MessageBoxResult.Yes) continue;
                var dialog = new StorageLocationDialog(configuration?.Directory ?? NodeDefaults.LegacyDataDirectory,
                    "请选择已有资料所在的目录。恢复时必须包含 resources.db，不会自动创建空资料。");
                if (dialog.ShowDialog() != true) return false;
                try
                {
                    var restored = new StorageConfiguration(dialog.SelectedPath, true);
                    StorageLocation.ValidateCurrent(restored);
                    Locations.Save(restored);
                }
                catch (Exception restoreError) { System.Windows.MessageBox.Show(restoreError.Message, "无法使用所选资料"); }
            }
        }
    }
}
