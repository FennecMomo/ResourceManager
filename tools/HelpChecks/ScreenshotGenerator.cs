using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResourceManager.App;
using ResourceManager.Core;

internal static class ScreenshotGenerator
{
    private const int Width = 1260;
    private const int Height = 820;
    private static readonly string Output = Path.GetFullPath(Path.Combine("ResourceManager.App", "Assets", "Help"));

    private sealed record Shot(string Chapter, string Step, int Page, string Target, string Label,
        string? Scroll = null, double? FocusHeight = null);

    private static readonly Shot[] Shots =
    [
        new("01", "01", 0, "RefreshPeersButton", "刷新设备"),
        new("01", "02", 0, "PeersGrid", "选择设备", FocusHeight: 75),
        new("01", "03", 0, "RemoteGrid", "浏览资源", FocusHeight: 88),
        new("02", "01", 1, "PublishButton", "发布文件或文件夹"),
        new("02", "02", 1, "LocalGrid", "管理资源树", FocusHeight: 112),
        new("02", "03", 1, "GroupPermissionButton", "设置分组权限"),
        new("03", "01", 2, "GitChooseRepositoryButton", "选择 Git 仓库"),
        new("03", "02", 2, "GitProjectList", "选择协作项目", "GitScrollViewer", 78),
        new("03", "03", 2, "GitPublishButton", "推送提交"),
        new("04", "01", 3, "ChatConversationList", "选择会话", FocusHeight: 78),
        new("04", "02", 3, "ChatInput", "输入消息"),
        new("04", "03", 3, "ChatResourceButton", "发送资源卡片"),
        new("05", "01", 0, "RemoteFavoriteButton", "加入收藏"),
        new("05", "02", 4, "FavoriteCheckButton", "检查状态"),
        new("05", "03", 4, "FavoriteDownloadButton", "下载收藏"),
        new("06", "01", 0, "RemoteDownloadButton", "开始下载"),
        new("06", "02", 5, "ResumeDownloadButton", "继续任务"),
        new("06", "03", 5, "OpenDownloadFolderButton", "打开保存位置"),
        new("07", "01", 6, "FeedbackTargetBox", "选择反馈目标", "FeedbackScrollViewer"),
        new("07", "02", 6, "FeedbackBodyBox", "填写反馈正文", "FeedbackScrollViewer"),
        new("07", "03", 6, "FeedbackHistoryList", "查看提交历史", "FeedbackScrollViewer", 96),
        new("08", "01", 7, "NicknameBox", "修改个人资料", "SettingsScrollViewer"),
        new("08", "02", 7, "StoragePathText", "查看存储位置", "SettingsScrollViewer"),
        new("08", "03", 7, "CheckUpdateButton", "检查更新", "SettingsScrollViewer"),
        new("09", "01", 8, "ServerAddButton", "添加服务器"),
        new("09", "02", 8, "ServerContentHost", "查看服务器内容"),
        new("09", "03", 8, "ServerUploadButton", "上传资源"),
        new("10", "01", 0, "StatusText", "客户端保持运行")
    ];

    internal static void Generate(MainWindow window, FrameworkElement root)
    {
        Directory.CreateDirectory(Output);
        ((TextBlock)window.FindName("IdentityText")).Text = "示例用户";
        ((TextBlock)window.FindName("AddressText")).Text = "192.0.2.10 : 37642";
        ((TextBlock)window.FindName("StoragePathText")).Text = @"D:\ResourceManager";
        ((TextBlock)window.FindName("FeedbackEnvironmentText")).Text = "提交时自动附送：示例用户 · ResourceManager 0.7.2 · Windows";
        ((TextBlock)window.FindName("GitEnvironmentText")).Text = "已找到 Git（演示资料）";
        ((TextBlock)window.FindName("StatusText")).Text = "演示资料已就绪，客户端正在运行";
        SeedDemonstrationRows(window);
        var tabs = (TabControl)window.FindName("Tabs");
        foreach (var shot in Shots)
        {
            tabs.SelectedIndex = shot.Page;
            Layout(root);
            foreach (var scrollName in new[] { "GitScrollViewer", "FeedbackScrollViewer", "SettingsScrollViewer" })
                ((ScrollViewer)window.FindName(scrollName)).ScrollToTop();
            Layout(root);
            var target = (FrameworkElement?)window.FindName(shot.Target)
                ?? throw new InvalidOperationException($"Missing screenshot target {shot.Target}");
            if (shot.Scroll is not null)
            {
                var viewer = (ScrollViewer)window.FindName(shot.Scroll);
                var y = target.TranslatePoint(new Point(0, 0), viewer).Y;
                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + y - 45));
                Layout(root);
            }
            var bounds = target.TransformToAncestor(root).TransformBounds(new Rect(target.RenderSize));
            if (shot.FocusHeight is { } height) bounds.Height = Math.Min(bounds.Height, height);
            if (bounds.Width < 4 || bounds.Height < 4 || bounds.Bottom < 90 || bounds.Top > Height - 8)
                throw new InvalidOperationException($"Screenshot target outside visible page: {shot.Target} {bounds}");
            var screenshot = Capture(root);
            Save(Annotate(screenshot, bounds, shot.Label), $"{shot.Chapter}-{shot.Step}.png");
            Console.WriteLine($"{shot.Chapter}-{shot.Step}: {shot.Target} {bounds}");
        }

        Save(TerminalScreenshot("注册 MCP 服务", 2), "10-02.png");
        Save(TerminalScreenshot("核对注册结果", 5), "10-03.png");
        Console.WriteLine($"Generated {Shots.Length + 2} annotated help screenshots in {Output}");
    }

    private static void SeedDemonstrationRows(MainWindow window)
    {
        var now = new DateTimeOffset(2026, 9, 29, 4, 0, 0, TimeSpan.Zero);
        var peer = new PeerInfo("guide-peer", "192.0.2.20", 37642, "示例同事电脑", null, now);
        window.Peers.Add(new PeerRow(peer, "在线", null, "直连", ""));
        ((ListBox)window.FindName("PeersGrid")).SelectedItem = window.Peers[0];
        var remote = new RemoteResource("guide-resource", "项目资料", ResourceKind.Folder, PublishMode.Reference,
            2048, now, true, "演示资源");
        window.RemoteResources.Add(new ResourceRow(remote, remote.Name, "文件夹", "引用", "2 KB", "可下载", remote.Note));
        window.RemoteResourceTree.Add(new ResourceTreeNode
        {
            Key = remote.Id, Name = remote.Name, GroupId = NodeStore.DefaultResourceGroupId, IsFolder = true,
            RemoteRow = window.RemoteResources[0]
        });
        ((TextBlock)window.FindName("PeerHeading")).Text = "示例同事电脑的资源 · 在线";

        if (window.LocalResourceTree.FirstOrDefault() is { } group)
        {
            group.Children.Add(new ResourceTreeNode
            {
                Key = "guide-local", Name = "演示文档.pdf", GroupId = group.Key,
                Path = @"D:\ResourceManager\演示文档.pdf"
            });
            group.IsExpanded = true;
        }
        window.GitProjectRows.Add(new GitProjectRow(new GitProjectSummary("guide-git", "演示项目", "guide-peer", 2, now, false)));
        window.ChatConversations.Add(new ChatConversationRow("guide-peer", "示例同事电脑", "在线", 0));
        window.Favorites.Add(new FavoriteRow(new Favorite(peer.DeviceId, remote.Id, remote.Name, ResourceKind.Folder),
            remote.Name, peer.Nickname, "文件夹", "在线", "演示资源"));
        var job = new DownloadJob("guide-download", peer.DeviceId, remote.Id, remote.Name, ResourceKind.Folder,
            @"D:\Downloads\项目资料", "已完成", 2048, 2048, null);
        window.Downloads.Add(new DownloadRow(job, remote.Name, peer.Nickname, "已完成", "2 KB / 2 KB",
            job.TargetPath, "", ""));
        window.FeedbackHistory.Add(new FeedbackHistoryRow(new FeedbackHistoryItem("guide-feedback", FeedbackTargetKind.LanServer,
            "演示反馈服务", FeedbackCategory.Suggestion, "改进资源搜索", "演示反馈内容", "", "demo-1", null,
            "Completed", now, now, null)));

        var binding = new ServerBinding("guide-server", "示例服务器", "https://example.com", "guide-server",
            "", "在线", null, null);
        var server = new MainWindow.ServerTabRow(binding, "guide-self");
        server.Update("在线", null);
        server.ServerVersion = "0.3.0";
        server.SupportsResources = true;
        server.SupportsStorage = true;
        var member = new MainWindow.ServerMemberRow("guide-peer", peer.Nickname, "0.7.2", "在线", now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), null, 1);
        server.Members.Add(member);
        server.SelectedMember = member;
        server.ResourceTree.Add(new ResourceTreeNode
        {
            Key = "guide-stored", Name = "项目资料", GroupId = NodeStore.DefaultResourceGroupId,
            IsFolder = true
        });
        window.Servers.Add(server);
        ((TabControl)window.FindName("ServerTabs")).SelectedItem = server;
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
    }

    private static RenderTargetBitmap Capture(FrameworkElement root)
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
        bitmap.Render(background);
        bitmap.Render(root);
        return bitmap;
    }

    private static RenderTargetBitmap Annotate(BitmapSource screenshot, Rect bounds, string label)
    {
        var highlight = new Rect(Math.Max(8, bounds.Left - 8), Math.Max(8, bounds.Top - 8),
            Math.Min(Width - Math.Max(8, bounds.Left - 8) - 8, bounds.Width + 16),
            Math.Min(Height - Math.Max(8, bounds.Top - 8) - 8, bounds.Height + 16));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(screenshot, new Rect(0, 0, Width, Height));
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 9), highlight, 8, 8);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(230, 55, 57)), 5), highlight, 8, 8);
            var text = Text(label, 18, Brushes.White, bold: true);
            var top = highlight.Top >= 46 ? highlight.Top - 39 : Math.Min(Height - 40, highlight.Bottom + 9);
            var left = Math.Clamp(highlight.Left, 10, Width - text.Width - 28);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(230, 55, 57)), null,
                new Rect(left, top, text.Width + 23, 31), 6, 6);
            dc.DrawText(text, new Point(left + 11, top + 3));
        }
        var output = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        return output;
    }

    private static RenderTargetBitmap TerminalScreenshot(string label, int focusLine)
    {
        var lines = new[]
        {
            "Windows PowerShell · ResourceManager MCP",
            "",
            "PS> codex mcp add resource-manager -- \"C:\\Path\\To\\ResourceManager.exe\" --mcp",
            "Added global MCP server 'resource-manager'.",
            "",
            "PS> codex mcp get resource-manager",
            "resource-manager  |  enabled: true  |  transport: stdio",
            "command: C:\\Path\\To\\ResourceManager.exe",
            "args: --mcp"
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(0, 0, Width, Height));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(21, 30, 47)), null,
                new Rect(58, 85, 1144, 650), 14, 14);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(35, 47, 68)), null,
                new Rect(58, 85, 1144, 55), 14, 14);
            dc.DrawText(Text("MCP 命令示意 · 请替换为你的 EXE 实际路径", 18, Brushes.White, bold: true), new Point(85, 99));
            for (var i = 0; i < lines.Length; i++)
            {
                var y = 165 + i * 57;
                if (i == focusLine)
                {
                    var rect = new Rect(76, y - 8, 1108, 48);
                    dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 8), rect, 6, 6);
                    dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(230, 55, 57)), 5), rect, 6, 6);
                }
                dc.DrawText(Text(lines[i], 18, i is 3 or 6 or 7 or 8 ? new SolidColorBrush(Color.FromRgb(142, 230, 177)) : Brushes.White),
                    new Point(91, y));
            }
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(230, 55, 57)), null,
                new Rect(83, 687, 180, 33), 6, 6);
            dc.DrawText(Text(label, 16, Brushes.White, bold: true), new Point(94, 691));
        }
        var output = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        return output;
    }

    private static FormattedText Text(string content, double size, Brush color, bool bold = false) =>
        new(content, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal), size, color, 1);

    private static void Save(BitmapSource image, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(Output, name));
        encoder.Save(stream);
    }
}
