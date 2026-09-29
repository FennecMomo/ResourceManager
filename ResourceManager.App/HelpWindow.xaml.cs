using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResourceManager.App;

public sealed record HelpStep(string Number, string Title, string Description)
{
    public string Screenshot { get; set; } = "";
}

public sealed record HelpTool(string Name, string Description);
public sealed record HelpChapter(string Number, string Title, string Subtitle, string Intro,
    IReadOnlyList<HelpStep> Steps, string Tip, IReadOnlyList<HelpTool>? Tools = null, string? CommandExample = null)
{
    public Visibility ToolVisibility => Tools is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommandVisibility => string.IsNullOrWhiteSpace(CommandExample) ? Visibility.Collapsed : Visibility.Visible;
}

public partial class HelpWindow : Window
{
    public IReadOnlyList<HelpChapter> Chapters { get; } = CreateChapters();

    public HelpWindow()
    {
        InitializeComponent();
        DataContext = this;
        ChapterList.SelectedIndex = 0;
    }

    private void Chapter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ChapterScroll?.ScrollToTop();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        var image = new BitmapImage(new Uri($"pack://application:,,,{path}", UriKind.Absolute));
        var preview = new Window
        {
            Owner = this,
            Title = "操作截图 · 点击关闭按钮返回帮助",
            Width = Math.Min(1280, SystemParameters.WorkArea.Width - 32),
            Height = Math.Min(850, SystemParameters.WorkArea.Height - 32),
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = System.Windows.Media.Brushes.White,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new System.Windows.Controls.Image { Source = image, Stretch = Stretch.None,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top }
            }
        };
        preview.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) preview.Close();
        };
        preview.Show();
    }

    private static IReadOnlyList<HelpChapter> CreateChapters()
    {
        var chapters = new List<HelpChapter>
        {
        new("01", "设备", "查找设备，浏览对方公开的资源",
            "设备页左侧列出已经发现或保存的设备，右侧显示选中设备的资源。刷新会逐步更新可用连接；暂时离线的设备也可以保留在列表中。",
            [T("01", "点击刷新", "在“已连接的设备”标题旁点击“刷新”，等待列表逐步更新。"),
             T("02", "选择一台设备", "在左侧选择设备；右侧资源树会显示对方当前公开的文件和文件夹。"),
             T("03", "使用资源", "选中资源后点击“下载资源”或“加入收藏”。暂时无法连接时可稍后刷新。")],
            "列表中的“离线”表示此刻无法连接，不等于设备或资源已经永久失效。"),

        new("02", "我的发布", "把文件或文件夹分享给其他设备",
            "左侧资源树管理你的发布和分组；右侧“文件信息”显示选中项的详情、备注与可用操作。",
            [T("01", "点击发布", "在“文件信息”右上角点击“发布 ▾”，选择“发布文件…”或“发布文件夹…”，再用系统选择窗口选取内容。"),
             T("02", "查看和整理", "在左侧资源树选中发布项；可以新建分组、移动发布，或为资源填写备注。"),
             T("03", "控制访问", "选中分组可设置权限；选中资源可使用“发送给”提醒设备，或撤销发布。也可以选择“发布到服务器…”。")],
            "发布文件夹时请先确认其中没有不想分享的内容；撤销发布后，对方将无法继续从这项发布下载。"),

        new("03", "Git 协作", "交换提交并查看项目协作图",
            "每位成员保留自己的仓库和协作线。协作页显示项目与提交点，可加入项目、推送自己的提交或同步选中的点。",
            [T("01", "准备 Git", "如页面提示缺少 Git，可安装 Git 或选择现有的 git.exe。"),
             T("02", "创建或加入项目", "创建项目时输入名称并选择已有 Git 仓库；也可以选中同伴的项目，点击“加入并下载仓库”。"),
             T("03", "查看协作图", "选中项目后点击提交点查看详情；使用“推送我的提交”或“同步选中的点”交换进度。")],
            "推送会传送所选分支的提交历史和 LFS 大文件，请先检查仓库中是否含有不应共享的内容。"),

        new("04", "聊天", "与已连接设备交流并发送资源卡片",
            "聊天页左侧是会话，右侧是消息时间线和输入框。未能立刻送达的消息会保存在本机队列中等待重试。",
            [T("01", "选择设备", "从会话列表选择要联系的设备，查看历史消息和当前送达状态。"),
             T("02", "发送文字", "在底部输入框填写消息并点击“发送”；多行内容会让输入框自动增高，最多显示五行。"),
             T("03", "分享资源", "点击“发送资源”选择已发布资源；接收方可从聊天中的资源卡片下载。")],
            "会话可以静音一小时；清空本机记录只处理当前电脑上的聊天记录。"),

        new("05", "收藏", "保存常用资源的快捷入口",
            "收藏会记住资源及其来源，并显示在线、离线或已撤销等状态。你可以从设备或服务器的资源列表加入收藏。",
            [T("01", "添加收藏", "在设备资源树选中资源后点击“加入收藏”；服务器资源也有“收藏”操作。"),
             T("02", "检查状态", "打开“收藏”页，点击“检查状态”更新资源及来源是否仍可访问。"),
             T("03", "下载或移除", "选中收藏可点击“下载资源”；不再需要时点击“取消收藏”。")],
            "收藏是快捷入口，不是资源的本地副本；离线资源仍需来源重新可用后才能下载。"),

        new("06", "下载", "查看传输进度和恢复中断任务",
            "下载页集中显示从设备和服务器获取的任务，便于确认状态、继续任务和找到保存位置。",
            [T("01", "发起任务", "在设备、收藏或服务器页面选中资源，点击相应的“下载”按钮。"),
             T("02", "处理暂停或中断", "打开“下载”页选中任务，必要时点击“继续任务”。"),
             T("03", "查找结果", "下载完成后点击“打开保存位置”；不再需要的任务可从列表移除。")],
            "下载任务的状态和文件保存在当前资料目录中；改变存储位置前请确认正在进行的传输。"),

        new("07", "反馈", "提交问题和建议并追踪处理状态",
            "反馈可发送到已配置的反馈服务端，或在登录个人 GitHub 后提交。页面下方可以查看历史记录和状态。",
            [T("01", "选择目标", "在反馈页选择可用的提交目标；若选 GitHub，先使用自己的账号登录。"),
             T("02", "填写并提交", "选择类别，填写标题和内容；服务端反馈可添加附件，然后点击“提交反馈”。"),
             T("03", "查看后续", "在“提交历史”中点击“刷新状态”，查看待处理、已接受或已完成等状态。")],
            "GitHub 目标不支持附件；带附件的反馈请选支持附件的服务端。"),

        new("08", "设置", "调整个人资料、连接方式和存储位置",
            "设置页管理昵称头像、监听端口、开机启动、路由器入口、存储目录和更新。修改后请留意页面顶部的保存提示。",
            [T("01", "修改资料", "编辑昵称、头像或连接偏好，点击页面顶部的“保存设置”。"),
             T("02", "管理存储和网络", "“存储位置”可迁移资料目录；“当前路由器”可查看外部入口并保存路由器地址。"),
             T("03", "检查更新", "在“软件更新”中点击“检查更新”，发现新版后按提示下载并安装。")],
            "更改存储位置会在校验后重启程序；原目录数据会保留，确认迁移无误后再自行清理。"),

        new("09", "服务器", "通过公网服务器发现设备与保存资源",
            "服务器页可以用地址连接服务器、查看服务器资源，并将文件或文件夹上传为持久资源。",
            [T("01", "连接服务器", "点击“添加服务器”填写服务器地址与所需连接信息；已添加的服务器可重新连接或编辑。"),
             T("02", "浏览内容", "选中服务器查看在线设备和存储资源；按资源权限进行收藏或下载。"),
             T("03", "上传资源", "点击“上传…”选择文件或文件夹；在“上传任务”中查看进度、暂停或继续。自己的资源可管理权限或删除。")],
            "服务器上传会在服务器保存副本；下载和权限取决于该服务器的配置与资源所有者设置。"),

        new("10", "MCP 与 AI", "让 AI 使用本机 ResourceManager 工具",
            "MCP 通过当前用户的本机通道连接正在运行的客户端。配置完成后，AI 可按你的指令查询设备、搜索资源、发布内容或发送消息。",
            [T("01", "保持客户端运行", "先正常启动 ResourceManager。AI 使用工具时，本机客户端需要处于运行状态；最小化到托盘也可以。"),
             T("02", "注册 MCP 服务", "在支持本机 stdio MCP 的 AI 客户端中，把当前版本的 ResourceManager.exe 设为命令，并添加 --mcp 参数。Codex 可运行：codex mcp add resource-manager -- <EXE 绝对路径> --mcp。"),
             T("03", "验证并开始使用", "运行 codex mcp get resource-manager 核对路径；在新的 AI 会话中要求它“刷新设备并搜索名称包含报告的资源”。发布或发消息前，请核对目标和内容。")],
            "AI 会话可能不会热加载刚注册的工具。升级程序后，要把 MCP 注册路径更新为新版 EXE。发布工具先返回操作 ID，应查询完成状态后再认定发布成功。",
            [new("resource_manager_status", "查看正在运行的客户端状态"),
             new("resource_manager_list_devices", "查看设备、在线状态和连接入口"),
             new("resource_manager_refresh_devices", "后台刷新设备连接"),
             new("resource_manager_list_device_resources", "浏览指定设备的可见资源"),
             new("resource_manager_search_resources", "按名称、类型和大小检索可见资源"),
             new("resource_manager_list_publications", "查看本机发布和分组"),
             new("resource_manager_publish", "发布指定文件或文件夹"),
             new("resource_manager_get_publication_operation", "查询后台发布是否完成"),
             new("resource_manager_list_conversations", "列出聊天会话与未读数量"),
             new("resource_manager_list_messages", "查看指定会话的消息和状态"),
             new("resource_manager_send_message", "发送文字消息"),
             new("resource_manager_send_resource_card", "发送已发布资源卡片")],
            "codex mcp add resource-manager -- \"C:\\Path\\To\\ResourceManager.exe\" --mcp\n" +
            "codex mcp get resource-manager")
        };

        foreach (var chapter in chapters)
        foreach (var step in chapter.Steps)
            step.Screenshot = $"/ResourceManager;component/Assets/Help/{chapter.Number}-{step.Number}.png";
        return chapters;
    }

    private static HelpStep T(string number, string title, string description) => new(number, title, description);
}
