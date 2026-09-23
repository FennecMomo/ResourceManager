using System.ComponentModel;
using System.Windows;
using ResourceManager.Core;

namespace ResourceManager.App;

public sealed record ReminderTarget(PeerInfo Peer, string Status, bool SupportsReminder, string Note)
{
    public string Detail => !SupportsReminder ? "版本不支持提醒" : Status;
    public string DisplayName => string.IsNullOrEmpty(Note) ? Peer.Nickname : $"{Note}（{Peer.Nickname}）";
}

public partial class SendReminderDialog : Window
{
    private readonly List<TargetRow> rows;

    public IReadOnlyList<PeerInfo> SelectedPeers { get; private set; } = [];

    public SendReminderDialog(string resourceText, IReadOnlyList<ReminderTarget> targets)
    {
        InitializeComponent();
        ResourceText.Text = resourceText;
        rows = targets.Select(target => new TargetRow(target)).ToList();
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateSelection();
        TargetList.ItemsSource = rows;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = rows.Count(row => row.IsChecked);
        SelectionText.Text = count == 0 ? "未选择设备" : $"已选择 {count} 台设备";
        SendButton.IsEnabled = count > 0;
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        SelectedPeers = rows.Where(row => row.IsChecked).Select(row => row.Target.Peer).ToArray();
        if (SelectedPeers.Count == 0) return;
        DialogResult = true;
    }

    private sealed class TargetRow(ReminderTarget target) : INotifyPropertyChanged
    {
        private bool isChecked = target.SupportsReminder && target.Status == "在线";

        public ReminderTarget Target { get; } = target;
        public string DisplayName => Target.DisplayName;
        public string Summary => $"{Target.Peer.Ip}:{Target.Peer.Port}  ·  {Target.Detail}";

        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
