using System.Windows;

namespace ResourceManager.App;

public partial class MainWindow
{
    private AiRequestsWindow? aiRequestWindow;
    private readonly HashSet<string> notifiedAiRequests = [];
    private void AiRequests_Click(object sender, RoutedEventArgs e) => ShowAiRequests(true);
    private void ShowAiRequests(bool activate)
    {
        if (aiRequestWindow is null)
        {
            aiRequestWindow = new AiRequestsWindow(store, aiRequests) { Icon = Icon, ShowActivated = activate };
            aiRequestWindow.Closed += (_, _) => aiRequestWindow = null;
            aiRequestWindow.Show();
        }
        else if (activate) aiRequestWindow.Activate();
    }
    private void OnAiRequestsChanged()
    {
        if (exiting) return;
        var pending = aiRequests.ListLocal().Where(s => s.State == "AwaitingApproval").ToArray();
        AiRequestsButton.Content = pending.Length == 0 ? "AI 请求" : $"AI 请求 ({pending.Length})";
        aiRequestWindow?.Refresh();
        // Display authorization without activating or stealing focus from another application.
        if (pending.Any(s => notifiedAiRequests.Add(s.Id))) ShowAiRequests(false);
    }
}
