using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    internal Task<LocalControlResponse> HandleLocalControlAsync(LocalControlRequest request, CancellationToken token) =>
        Dispatcher.InvokeAsync(() => HandleLocalControl(request), System.Windows.Threading.DispatcherPriority.Background, token).Task;

    private LocalControlResponse HandleLocalControl(LocalControlRequest request)
    {
        var unsaved = HasUnsavedSettings || savingSettings || resolvingSettingsNavigation;
        var state = new
        {
            protocol = 1,
            processId = Environment.ProcessId,
            executable = Environment.ProcessPath,
            version = AppVersion,
            dataDirectory = store.DataDirectory,
            ready = node.IsRunning,
            exiting,
            windowVisible = IsVisible,
            page = Tabs.SelectedIndex,
            unsavedSettings = unsaved,
            preparingPrivateResource = preparingChatResource,
            activeDownloads = activeDownloads.Count,
            publishedResources = store.GetResources().Count,
            peers = store.GetPeers().Count,
            status = StatusText.Text
        };
        if (request.Command == "status") return new(true, Data: state);
        if (exiting) return new(true, Data: state);
        if (unsaved) return new(false, "unsaved_settings", state);
        if (preparingChatResource) return new(false, "preparing_private_resource", state);
        return new(true, Data: state)
        {
            AfterResponse = () => Dispatcher.BeginInvoke(async () =>
            {
                try { await ExitAsync(); }
                catch (Exception ex) { AppLog.Write("本机控制通道退出失败", ex); }
            })
        };
    }
}
