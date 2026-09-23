using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class SendReminderDialog : Window
{
    public PeerInfo? SelectedPeer { get; private set; }

    public SendReminderDialog(string resourceText, IReadOnlyList<PeerInfo> candidates)
    {
        InitializeComponent();
        ResourceText.Text = resourceText;
        PeerList.ItemsSource = candidates
            .Select(peer => new CandidateRow(peer, peer.Nickname, $"{peer.Ip}:{peer.Port}"))
            .ToArray();
    }

    private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SendButton.IsEnabled = PeerList.SelectedItem is CandidateRow;

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (PeerList.SelectedItem is not CandidateRow row) return;
        SelectedPeer = row.Peer;
        DialogResult = true;
    }

    private sealed record CandidateRow(PeerInfo Peer, string Nickname, string Address);
}
