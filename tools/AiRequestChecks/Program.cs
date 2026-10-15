using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.App;
using ResourceManager.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "ResourceManagerAiChecks", Guid.NewGuid().ToString("N"));
        var scopeRoot = Path.Combine(root, "approved"); Directory.CreateDirectory(scopeRoot);
        File.WriteAllText(Path.Combine(scopeRoot, "manual.txt"), "welding instructions");
        if (args.Length == 2 && args[0] == "--codex")
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                var result = new CodexFileFinder().FindAsync(args[1], scopeRoot,
                    "请先用 read 读取 manual.txt，再判断它是否是焊接操作说明。最终回答须包含文件内的原文。", null, deadline.Token).GetAwaiter().GetResult();
                if (!result.RelativePaths.Contains("manual.txt")) throw new Exception("Real CLI did not return the fixture file.");
                if (!result.Answer.Contains("welding instructions", StringComparison.OrdinalIgnoreCase)) throw new Exception("Real CLI did not read the fixture excerpt.");
                Console.WriteLine("PASS: real Codex CLI read the scoped excerpt and returned its exact fixture text.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        var store = new NodeStore(Path.Combine(root, "data"));
        store.UpsertPeer(new PeerInfo("fixture-a", "127.0.0.1", 37642, "隔离请求设备", null, null));
        using var client = new PeerClient(store);
        var chat = new ChatService(store, client);
        var service = new AiRequestService(store, chat, new NeverRun());
        var request = service.Receive("fixture-a", new(Guid.NewGuid().ToString("N"), "请找一份焊接操作说明"));
        var window = new AiRequestsWindow(store, service);
        try
        {
            foreach (var size in new[] { new Size(960, 720), new Size(820, 600) })
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                Require(!window.IsVisible, "authorization UI remains hidden during isolated checks");
                Require(((TextBox)window.FindName("Details")).Text.Contains("请找一份焊接操作说明"), "request detail shows the actual question");
            }
            Require(((ComboBox)window.FindName("ApprovalScopes")).Items.Count == 0, "no implicit directory authorization");
            var scope = store.SaveAiSearchScope("fixture-a", scopeRoot, false); window.Refresh();
            Require(((ComboBox)window.FindName("ApprovalScopes")).Items.Count == 1, "request offers only explicit device-scoped directory");
            Require(((Button)window.FindName("ApproveButton")).IsEnabled, "pending request has explicit approval action");
            service.Cancel(request.Id, true); window.Refresh();
            Require(!((Button)window.FindName("ApproveButton")).IsEnabled, "rejected request cannot be approved accidentally");
            service.RemoveScope(scope.Id); window.Refresh();
            Require(((ListBox)window.FindName("Scopes")).Items.Count == 0, "revoked scope disappears");
            Console.WriteLine("PASS: 9 isolated AI request UI checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class NeverRun : IAiFileFinder
    {
        public Task<AiFinderResult> FindAsync(string executable, string directory, string question, string? previousAnswer, CancellationToken token) =>
            throw new InvalidOperationException("The UI fixture must not launch a real model.");
    }
}
