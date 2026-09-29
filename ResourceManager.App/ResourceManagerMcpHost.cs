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
     Description("在已加载且当前在线的设备和服务器目录中搜索资源。支持名称/备注、类型、大小及来源过滤，返回资源 ID；搜索结果只代表当次快照。")]
    public static Task<string> SearchResources(
        [Description("可选：资源名或备注包含的文字。") ] string? query = null,
        [Description("可选：File 或 Folder。") ] string? kind = null,
        [Description("可选：最小字节数。") ] long? minSizeBytes = null,
        [Description("可选：最大字节数。") ] long? maxSizeBytes = null,
        [Description("最多返回 1–100 条，默认 100。") ] int limit = 100,
        [Description("可选：device 或 server。") ] string? source = null,
        [Description("可选：服务器绑定 ID，先由 list_servers 获取。") ] string? serverId = null,
        [Description("可选：设备 ID。") ] string? deviceId = null) =>
        Call("search_resources", new { query, kind, minSizeBytes, maxSizeBytes, limit, source, serverId, deviceId });

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

    [McpServerTool(Name = "resource_manager_list_servers", ReadOnly = true, OpenWorld = false),
     Description("列出已绑定服务器、连接状态、成员和已加载目录所有者。")]
    public static Task<string> ListServers() => Call("list_servers");

    [McpServerTool(Name = "resource_manager_list_server_resources", ReadOnly = true, OpenWorld = true),
     Description("浏览指定服务器中某台设备当前可见的资源目录。先用 list_servers 取得服务器 ID 和目录所有者 ID。")]
    public static Task<string> ListServerResources(string serverId, string deviceId,
        string? query = null, string? kind = null, int limit = 100) =>
        Call("list_server_resources", new { serverId, deviceId, query, kind, limit });

    [McpServerTool(Name = "resource_manager_list_downloads", ReadOnly = true, OpenWorld = false),
     Description("列出下载任务、保存路径、进度和错误；可用 jobId 继续或暂停。")]
    public static Task<string> ListDownloads(int limit = 100) => Call("list_downloads", new { limit });

    [McpServerTool(Name = "resource_manager_download_resource", Destructive = false, OpenWorld = true),
     Description("把可见资源下载到已存在的本机绝对目录，返回下载任务 ID；source 为 device 或 server，server 来源须提供 serverId。可能在该目录写入文件。")]
    public static Task<string> DownloadResource(string source, string deviceId, string resourceId,
        string targetDirectory, string? serverId = null) =>
        Call("download_resource", new { source, deviceId, resourceId, targetDirectory, serverId });

    [McpServerTool(Name = "resource_manager_pause_download", Destructive = false, OpenWorld = false),
     Description("暂停指定下载任务并返回当前进度。")]
    public static Task<string> PauseDownload(string jobId) => Call("pause_download", new { jobId });

    [McpServerTool(Name = "resource_manager_resume_download", Destructive = false, OpenWorld = true),
     Description("继续下载任务，返回当前状态；使用原目标路径。")]
    public static Task<string> ResumeDownload(string jobId) => Call("resume_download", new { jobId });

    [McpServerTool(Name = "resource_manager_list_favorites", ReadOnly = true, OpenWorld = false),
     Description("列出收藏资源、来源和当前可用状态。")]
    public static Task<string> ListFavorites(int limit = 100) => Call("list_favorites", new { limit });

    [McpServerTool(Name = "resource_manager_add_favorite", Destructive = false, OpenWorld = false),
     Description("收藏已加载目录中的资源；source 为 device 或 server，后者需 serverId。")]
    public static Task<string> AddFavorite(string source, string deviceId, string resourceId,
        string? serverId = null, bool? serverStored = null) =>
        Call("add_favorite", new { source, deviceId, resourceId, serverId, serverStored });

    [McpServerTool(Name = "resource_manager_remove_favorite", Destructive = false, OpenWorld = false),
     Description("移除指定收藏记录；不会删除资源。服务器收藏可使用收藏记录的 serverId，即使服务器绑定已移除。")]
    public static Task<string> RemoveFavorite(string source, string deviceId, string resourceId,
        string? serverId = null, bool? serverStored = null) =>
        Call("remove_favorite", new { source, deviceId, resourceId, serverId, serverStored });

    [McpServerTool(Name = "resource_manager_set_publication_note", Destructive = false, OpenWorld = false),
     Description("修改本机已发布资源的备注，最多 200 字；空字符串清除备注。")]
    public static Task<string> SetPublicationNote(string resourceId, string note) =>
        Call("set_publication_note", new { resourceId, note });

    [McpServerTool(Name = "resource_manager_move_publication", Destructive = false, OpenWorld = false),
     Description("把本机发布项移入现有资源分组。")]
    public static Task<string> MovePublication(string resourceId, string groupId) =>
        Call("move_publication", new { resourceId, groupId });

    [McpServerTool(Name = "resource_manager_set_group_permission", Destructive = false, OpenWorld = true),
     Description("设置本机分组访问权限。access 为 Inherit、Public、Private 或 AllowList；AllowList 应明确给出设备 ID。")]
    public static Task<string> SetGroupPermission(string groupId, string access,
        string[]? allowedDeviceIds = null) =>
        Call("set_group_permission", new { groupId, access, allowedDeviceIds });

    [McpServerTool(Name = "resource_manager_set_server_publication", Destructive = false, OpenWorld = true),
     Description("启用或停用本机资源/分组在指定已绑定服务器上的发布。kind 为 resource 或 group。")]
    public static Task<string> SetServerPublication(string serverId, string kind, string id, bool enabled) =>
        Call("set_server_publication", new { serverId, kind, id, enabled });

    [McpServerTool(Name = "resource_manager_revoke_publication", Destructive = true, OpenWorld = true),
     Description("撤销本机发布项。引用发布保留原文件；副本发布会删除 ResourceManager 管理的副本。")]
    public static Task<string> RevokePublication(string resourceId) =>
        Call("revoke_publication", new { resourceId });

    [McpServerTool(Name = "resource_manager_list_uploads", ReadOnly = true, OpenWorld = false),
     Description("列出服务器副本上传任务、路径、权限和进度。")]
    public static Task<string> ListUploads(int limit = 100) => Call("list_uploads", new { limit });

    [McpServerTool(Name = "resource_manager_upload_to_server", Destructive = false, OpenWorld = true),
     Description("将本机绝对路径的文件或文件夹上传为服务器副本，返回任务 ID；access 为 Public、Private 或 AllowList。")]
    public static Task<string> UploadToServer(string serverId, string path, string access,
        string[]? allowedDeviceIds = null) =>
        Call("upload_to_server", new { serverId, path, access, allowedDeviceIds });

    [McpServerTool(Name = "resource_manager_pause_upload", Destructive = false, OpenWorld = true),
     Description("暂停指定服务器上传任务。")]
    public static Task<string> PauseUpload(string jobId) => Call("pause_upload", new { jobId });

    [McpServerTool(Name = "resource_manager_resume_upload", Destructive = false, OpenWorld = true),
     Description("继续指定服务器上传任务。")]
    public static Task<string> ResumeUpload(string jobId) => Call("resume_upload", new { jobId });

    [McpServerTool(Name = "resource_manager_set_stored_permission", Destructive = false, OpenWorld = true),
     Description("修改本人上传的服务器副本权限；access 为 Public、Private 或 AllowList。")]
    public static Task<string> SetStoredPermission(string serverId, string resourceId, string access,
        string[]? allowedDeviceIds = null) =>
        Call("set_stored_permission", new { serverId, resourceId, access, allowedDeviceIds });

    [McpServerTool(Name = "resource_manager_delete_stored_resource", Destructive = true, OpenWorld = true),
     Description("永久删除本人上传到服务器的资源副本。")]
    public static Task<string> DeleteStoredResource(string serverId, string resourceId) =>
        Call("delete_stored_resource", new { serverId, resourceId });

    [McpServerTool(Name = "resource_manager_mute_conversation", Destructive = false, OpenWorld = false),
     Description("将指定设备会话静音若干分钟；0 表示取消静音，上限 10080 分钟。")]
    public static Task<string> MuteConversation(string deviceId, int minutes) =>
        Call("mute_conversation", new { deviceId, minutes });

    [McpServerTool(Name = "resource_manager_retry_message", Destructive = false, OpenWorld = true),
     Description("将指定会话内发送失败的消息重新排队。")]
    public static Task<string> RetryMessage(string deviceId, string messageId) =>
        Call("retry_message", new { deviceId, messageId });

    [McpServerTool(Name = "resource_manager_send_private_resource", Destructive = false, OpenWorld = true),
     Description("私发本机绝对路径的文件或文件夹，后台复制到私发队列并返回 operationId；完成后再查询消息投递状态。")]
    public static Task<string> SendPrivateResource(string deviceId, string path) =>
        Call("send_private_resource", new { deviceId, path });

    [McpServerTool(Name = "resource_manager_get_private_resource_operation", ReadOnly = true, OpenWorld = false),
     Description("查询私发资源准备任务是否完成及生成的消息 ID；完成不代表对方已收到。")]
    public static Task<string> GetPrivateResourceOperation(string operationId) =>
        Call("get_private_resource_operation", new { operationId });

    [McpServerTool(Name = "resource_manager_check_updates", ReadOnly = true, OpenWorld = true),
     Description("检查 GitHub、本地设备与已绑定服务器中的客户端更新候选；不下载或安装。")]
    public static Task<string> CheckUpdates() => Call("check_updates");

    [McpServerTool(Name = "resource_manager_get_update_status", ReadOnly = true, OpenWorld = false),
     Description("读取当前版本和更新准备状态；不执行安装。")]
    public static Task<string> GetUpdateStatus() => Call("get_update_status");
}
