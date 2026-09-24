using System.Windows;
using System.Windows.Threading;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class ReminderWindow : Window
{
    private readonly DispatcherTimer autoCloseTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public ReminderWindow(PeerInfo sender, RemoteResource resource, DateTimeOffset sentUtc)
    {
        InitializeComponent();
        SenderText.Text = $"{sender.Nickname}（{sender.Ip}:{sender.Port}）建议你下载这个资源：";
        ResourceNameText.Text = resource.Name;
        ResourceMetaText.Text = $"{(resource.Kind == ResourceKind.File ? "文件" : "文件夹")} · {SizeText(resource.Size)}";
        NoteText.Text = string.IsNullOrEmpty(resource.Note) ? "" : $"备注：{resource.Note}";
        NoteText.Visibility = string.IsNullOrEmpty(resource.Note) ? Visibility.Collapsed : Visibility.Visible;
        SentText.Text = $"发送时间：{sentUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        autoCloseTimer.Tick += AutoCloseTimer_Tick;
        Loaded += (_, _) => autoCloseTimer.Start();
        Closed += (_, _) => autoCloseTimer.Stop();
    }

    public event EventHandler? DownloadRequested;
    public event EventHandler? MuteRequested;

    private void Download_Click(object sender, RoutedEventArgs e) => DownloadRequested?.Invoke(this, EventArgs.Empty);
    private void Mute_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(this, EventArgs.Empty);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AutoCloseTimer_Tick(object? sender, EventArgs e)
    {
        autoCloseTimer.Stop();
        Close();
    }

    private static string SizeText(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
}
