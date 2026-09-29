using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ResourceManager.Core;

namespace ResourceManager.App;

internal static class ResourceManagerMcpHost
{
    internal static async Task RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
        await builder.Build().RunAsync();
    }
}

[McpServerToolType]
public static class ResourceManagerMcpTools
{
    private static LocalAiBridgeClient Client()
    {
        var configured = Environment.GetEnvironmentVariable("RESOURCEMANAGER_CLIENT_PID");
        if (configured is not null && (!int.TryParse(configured, out var processId) || processId <= 0))
            throw new InvalidOperationException("RESOURCEMANAGER_CLIENT_PID 必须是正整数。");
        return new LocalAiBridgeClient(configured is null ? null : int.Parse(configured));
    }

    private static async Task<string> Call(string command, object? arguments = null) =>
        (await Client().CallAsync(command, arguments)).GetRawText();

    [McpServerTool(Name = "resource_manager_status", ReadOnly = true, OpenWorld = false),
     Description("读取正在运行的 ResourceManager 客户端状态、版本和资料目录；不唤起窗口。")]
    public static Task<string> GetStatus() => Call("status");

    [McpServerTool(Name = "resource_manager_list_devices", ReadOnly = true, OpenWorld = false),
     Description("列出已登记设备、在线状态、连接入口和资源目录是否已加载。不主动刷新。")]
    public static Task<string> ListDevices() => Call("list_devices");

    [McpServerTool(Name = "resource_manager_refresh_devices", Destructive = false, OpenWorld = true),
     Description("在后台启动设备刷新，包含直连、局域网、路由器和服务器更新；立即返回是否已启动。随后调用设备列表或资源搜索查看逐步更新结果。")]
    public static Task<string> RefreshDevices() => Call("refresh_devices");

    [McpServerTool(Name = "resource_manager_list_device_resources", ReadOnly = true, OpenWorld = true),
     Description("从已加载的设备资源目录读取当前对该设备可见的资源；不会绕过原有权限。设备离线或目录未加载时明确返回状态。")]
    public static Task<string> ListDeviceResources(
        [Description("设备 ID，先由 list_devices 获取。") ] string deviceId,
        [Description("可选：按资源名或备注包含的文字筛选。") ] string? query = null,
        [Description("可选：File 或 Folder。") ] string? kind = null,
        [Description("最多返回 1–100 条，默认 100。") ] int limit = 100) =>
        Call("list_device_resources", new { deviceId, query, kind, limit });

    [McpServerTool(Name = "resource_manager_search_resources", ReadOnly = true, OpenWorld = true),
     Description("在已加载且当前在线的设备和服务器目录中搜索资源。支持名称/备注、类型和大小条件，返回来源与资源 ID；搜索结果只代表当次快照。")]
    public static Task<string> SearchResources(
        [Description("可选：资源名或备注包含的文字。") ] string? query = null,
        [Description("可选：File 或 Folder。") ] string? kind = null,
        [Description("可选：最小字节数。") ] long? minSizeBytes = null,
        [Description("可选：最大字节数。") ] long? maxSizeBytes = null,
        [Description("最多返回 1–100 条，默认 100。") ] int limit = 100) =>
        Call("search_resources", new { query, kind, minSizeBytes, maxSizeBytes, limit });

    [McpServerTool(Name = "resource_manager_list_publications", ReadOnly = true, OpenWorld = false),
     Description("列出本机已发布资源与分组，包含资源 ID、源路径、发布方式和可用状态。")]
    public static Task<string> ListPublications() => Call("list_publications");

    [McpServerTool(Name = "resource_manager_publish", Destructive = false, OpenWorld = true),
     Description("后台发布本机绝对路径对应的文件或文件夹，立即返回 operationId。mode=Reference 引用原位置，mode=Copy 复制副本。调用 get_publication_operation 确认完成；不显示文件选择弹窗。")]
    public static Task<string> Publish(
        [Description("本机存在的文件或文件夹的完整绝对路径。") ] string path,
        [Description("Reference 或 Copy，必须明确指定。") ] string mode,
        [Description("可选：已存在的分组 ID；省略则使用默认组。") ] string? groupId = null) =>
        Call("publish", new { path, mode, groupId });

    [McpServerTool(Name = "resource_manager_get_publication_operation", ReadOnly = true, OpenWorld = false),
     Description("查询 MCP 发布任务的 Running、Completed 或 Failed 状态及生成的资源 ID。客户端重启后任务记录不保留，可用 list_publications 核对已完成发布。")]
    public static Task<string> GetPublicationOperation(
        [Description("publish 返回的 operationId。") ] string operationId) =>
        Call("get_publication_operation", new { operationId });

    [McpServerTool(Name = "resource_manager_list_conversations", ReadOnly = true, OpenWorld = false),
     Description("列出聊天会话、对方设备 ID、未读数和最近消息时间；不修改已读状态。")]
    public static Task<string> ListConversations() => Call("list_conversations");

    [McpServerTool(Name = "resource_manager_list_messages", ReadOnly = true, OpenWorld = false),
     Description("读取指定设备最近的聊天消息及发送状态；不修改已读状态。")]
    public static Task<string> ListMessages(
        [Description("设备 ID。") ] string deviceId,
        [Description("返回最近 1–100 条，默认 50。") ] int limit = 50) =>
        Call("list_messages", new { deviceId, limit });

    [McpServerTool(Name = "resource_manager_send_message", Destructive = false, OpenWorld = true),
     Description("向已登记且支持聊天的设备发送文字。先写入本机可靠队列，离线时以后重试；返回排队消息 ID，不表示对方已收到。")]
    public static Task<string> SendMessage(
        [Description("收件设备 ID。") ] string deviceId,
        [Description("要发送的实际消息文字，最多 2000 字。") ] string text) =>
        Call("send_message", new { deviceId, text });

    [McpServerTool(Name = "resource_manager_send_resource_card", Destructive = false, OpenWorld = true),
     Description("向已登记且支持聊天的设备发送一张已发布资源卡片；只返回本机排队状态，不表示对方已收到。")]
    public static Task<string> SendResourceCard(
        [Description("收件设备 ID。") ] string deviceId,
        [Description("本机已发布且可用的资源 ID。") ] string resourceId) =>
        Call("send_resource_card", new { deviceId, resourceId });
}
