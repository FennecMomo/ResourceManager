using System.IO;
using System.Text.Json;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private Task? aiDeviceRefresh;
    private readonly Dictionary<string, AiPublicationOperation> aiPublicationOperations = [];

    private sealed class AiPublicationOperation(string id)
    {
        public string Id { get; } = id;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "Running";
        public string? ResourceId { get; set; }
        public string? Error { get; set; }
    }

    internal Task<AiBridgeResponse> HandleAiBridgeAsync(AiBridgeRequest request, CancellationToken token) =>
        Dispatcher.InvokeAsync(() => HandleAiBridgeOnUiAsync(request, token),
            System.Windows.Threading.DispatcherPriority.Background, token).Task.Unwrap();

    private async Task<AiBridgeResponse> HandleAiBridgeOnUiAsync(AiBridgeRequest request, CancellationToken token)
    {
        if (request.Command == "status")
        {
            var status = HandleLocalControl(new LocalControlRequest(1, "status", Environment.ProcessId));
            return new(status.Success, status.Error, status.Data);
        }
        if (exiting) return new(false, "client_exiting");
        try
        {
            var args = request.Arguments;
            switch (request.Command)
            {
                case "list_devices":
                    return new(true, Data: new
                    {
                        devices = Peers.Select(row => new
                        {
                            id = row.Peer.DeviceId, name = row.Peer.Nickname, row.Note, row.Status,
                            row.Source, address = row.Peer.Ip, port = row.Peer.Port,
                            catalogLoaded = peerCatalogs.ContainsKey(row.Peer.DeviceId)
                        }).ToArray(),
                        refreshRunning = aiDeviceRefresh is { IsCompleted: false }
                    });
                case "refresh_devices":
                    if (aiDeviceRefresh is { IsCompleted: false })
                        return new(true, Data: new { started = false, alreadyRunning = true });
                    aiDeviceRefresh = RefreshDevicesAsync(includeGatewayScan: true);
                    _ = aiDeviceRefresh.ContinueWith(task => AppLog.Write("MCP 设备刷新失败", task.Exception!),
                        TaskContinuationOptions.OnlyOnFaulted);
                    return new(true, Data: new { started = true, alreadyRunning = false });
                case "list_device_resources":
                {
                    var peerId = Required(args, "deviceId", 100);
                    var peer = Peers.FirstOrDefault(row => row.Peer.DeviceId == peerId);
                    if (peer is null) return new(false, "device_not_found");
                    if (peer.Status != "在线" || !peerCatalogs.TryGetValue(peerId, out var resources))
                        return new(true, Data: new { deviceId = peerId, peer.Status, catalogLoaded = false, resources = Array.Empty<object>() });
                    var query = Optional(args, "query", 100);
                    var kind = ResourceKindFilter(args);
                    var limit = Limit(args, 100);
                    return new(true, Data: new
                    {
                        deviceId = peerId, peer.Status, catalogLoaded = true,
                        total = resources.Count(resource => Matches(resource, query, kind, null, null)),
                        resources = resources.Where(resource => Matches(resource, query, kind, null, null))
                            .Take(limit).Select(ResourceInfo).ToArray()
                    });
                }
                case "search_resources":
                {
                    var query = Optional(args, "query", 100);
                    var kind = ResourceKindFilter(args);
                    var source = Optional(args, "source", 20);
                    var serverId = Optional(args, "serverId", 100);
                    var deviceId = Optional(args, "deviceId", 100);
                    if (source is not null && !source.Equals("device", StringComparison.OrdinalIgnoreCase) &&
                        !source.Equals("server", StringComparison.OrdinalIgnoreCase))
                        return new(false, "invalid_source");
                    if (serverId is not null && (source?.Equals("device", StringComparison.OrdinalIgnoreCase) == true ||
                        Servers.All(row => row.Binding.Id != serverId)))
                        return new(false, "invalid_server_filter");
                    var min = OptionalLong(args, "minSizeBytes");
                    var max = OptionalLong(args, "maxSizeBytes");
                    if (min < 0 || max < 0 || min is not null && max is not null && min > max)
                        return new(false, "invalid_size_range");
                    var limit = Limit(args, 100);
                    var found = new List<object>();
                    foreach (var peer in Peers.Where(row => row.Status == "在线" && serverId is null &&
                        source?.Equals("server", StringComparison.OrdinalIgnoreCase) != true &&
                        (deviceId is null || row.Peer.DeviceId == deviceId)))
                        if (peerCatalogs.TryGetValue(peer.Peer.DeviceId, out var items))
                            found.AddRange(items.Where(item => Matches(item, query, kind, min, max))
                                .Select(item => (object)new { source = "device", deviceId = peer.Peer.DeviceId,
                                    deviceName = peer.Peer.Nickname, resource = ResourceInfo(item) }));
                    foreach (var server in Servers.Where(row => row.Status == "在线" &&
                        source?.Equals("device", StringComparison.OrdinalIgnoreCase) != true &&
                        (serverId is null || row.Binding.Id == serverId)))
                        foreach (var catalog in server.Catalogs.Where(item => deviceId is null || item.Owner == deviceId))
                        {
                            var owner = server.Members.FirstOrDefault(member => member.DeviceId == catalog.Owner);
                            found.AddRange(catalog.Catalog.Resources.Where(item => Matches(item, query, kind, min, max))
                                .Select(item => (object)new { source = "server", serverId = server.Binding.Id,
                                    serverName = server.Name, deviceId = catalog.Owner,
                                    deviceName = owner?.Nickname ?? catalog.Owner, resource = ResourceInfo(item) }));
                        }
                    return new(true, Data: new { total = found.Count, results = found.Take(limit).ToArray(),
                        note = "只检索已加载且当前在线的设备与服务器目录；结果是当次快照。" });
                }
                case "list_publications":
                    return new(true, Data: new
                    {
                        groups = store.GetResourceGroups().Select(group => new
                            { group.Id, group.Name, group.ParentId }).ToArray(),
                        publications = store.GetResources().Select(resource => new
                        {
                            resource.Id, resource.Name, kind = resource.Kind.ToString(),
                            mode = resource.Mode.ToString(), resource.SourcePath, resource.GroupId,
                            resource.Note, resource.PublishedUtc,
                            available = resource.Kind == ResourceKind.File ? File.Exists(resource.SourcePath) : Directory.Exists(resource.SourcePath)
                        }).ToArray()
                    });
                case "publish":
                {
                    var path = Required(args, "path", 2048);
                    if (!Path.IsPathFullyQualified(path)) return new(false, "absolute_path_required");
                    var modeText = Required(args, "mode", 20);
                    if (!Enum.TryParse<PublishMode>(modeText, true, out var mode) || !Enum.IsDefined(mode))
                        return new(false, "invalid_publish_mode");
                    var group = Optional(args, "groupId", 100) ?? NodeStore.DefaultResourceGroupId;
                    if (!store.GetResourceGroups().Any(item => item.Id == group)) return new(false, "group_not_found");
                    if (!File.Exists(path) && !Directory.Exists(path)) return new(false, "path_not_found");
                    if (publishing) return new(false, "publishing_resources");
                    foreach (var stale in aiPublicationOperations.Values.Where(item =>
                                 item.State != "Running" && DateTimeOffset.UtcNow - item.StartedUtc > TimeSpan.FromDays(1))
                                 .Select(item => item.Id).ToArray()) aiPublicationOperations.Remove(stale);
                    var operation = new AiPublicationOperation(Guid.NewGuid().ToString("N"));
                    aiPublicationOperations[operation.Id] = operation;
                    _ = RunAiPublicationAsync(operation, path, mode, group);
                    return new(true, Data: PublicationOperationInfo(operation));
                }
                case "get_publication_operation":
                {
                    var id = Required(args, "operationId", 100);
                    return aiPublicationOperations.TryGetValue(id, out var operation)
                        ? new(true, Data: PublicationOperationInfo(operation))
                        : new(false, "operation_not_found");
                }
                case "list_conversations":
                    return new(true, Data: new { conversations = store.GetChatConversations().Select(conversation => new
                    {
                        conversation.PeerId, conversation.Nickname, conversation.Unread,
                        conversation.LastMessageUtc, conversation.Removed,
                        status = peerStatus.GetValueOrDefault(conversation.PeerId, "未检查")
                    }).ToArray() });
                case "list_messages":
                {
                    var peerId = Required(args, "deviceId", 100);
                    if (store.GetPeer(peerId) is null && !store.GetChatConversations().Any(item => item.PeerId == peerId))
                        return new(false, "conversation_not_found");
                    var limit = Limit(args, 50);
                    var messages = store.GetChatMessages(peerId);
                    return new(true, Data: new { deviceId = peerId, total = messages.Count,
                        messages = messages.TakeLast(limit).Select(message => new
                        {
                            message.MessageId, message.Outgoing, message.Kind, message.Text,
                            message.ResourceId, message.ResourceName, message.SentUtc,
                            message.ReceivedUtc, message.State, message.Error,
                            progress = message.Outgoing ? store.GetCachedChatProgress(peerId, message.MessageId) : store.GetLocalChatProgress(peerId, message.MessageId)
                        }).ToArray() });
                }
                case "send_message":
                case "send_resource_card":
                {
                    var peerId = Required(args, "deviceId", 100);
                    if (store.GetPeer(peerId) is null) return new(false, "device_not_found");
                    if (!store.GetPeerCapabilities(peerId).Contains(NodeDefaults.ChatCapability, StringComparer.Ordinal))
                        return new(false, "chat_not_supported");
                    var message = request.Command == "send_message"
                        ? chat.QueueText(peerId, Required(args, "text", ChatService.MaxTextLength))
                        : chat.QueueResource(peerId, Required(args, "resourceId", 100));
                    RefreshChatConversations();
                    if (SelectedChatPeerId == peerId) RefreshChatTimeline();
                    _ = PumpChatSafeAsync();
                    return new(true, Data: new { messageId = message.MessageId, state = "Queued",
                        note = "消息已加入本机队列；离线设备恢复连接后自动重试。" });
                }
                default:
                    return await HandleAdditionalAiBridgeAsync(request, token);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Write("MCP 操作失败：" + request.Command, ex);
            return new(false, "operation_failed");
        }
    }

    private async Task RunAiPublicationAsync(AiPublicationOperation operation, string path,
        PublishMode mode, string group)
    {
        try
        {
            var before = store.GetResources().Select(item => item.Id).ToHashSet();
            var failures = await PublishPathsAsync([path], mode, group);
            if (failures.Count > 0) throw new IOException(failures[0]);
            operation.ResourceId = store.GetResources().FirstOrDefault(item => !before.Contains(item.Id))?.Id
                ?? throw new IOException("发布完成后未找到资源记录。");
            operation.State = "Completed";
        }
        catch (Exception ex)
        {
            operation.Error = ex.Message;
            operation.State = "Failed";
            AppLog.Write("MCP 发布失败", ex);
        }
    }

    private static object PublicationOperationInfo(AiPublicationOperation operation) => new
    {
        operationId = operation.Id, operation.State, operation.ResourceId,
        operation.Error, operation.StartedUtc
    };

    private static string Required(JsonElement args, string name, int maxLength) =>
        Optional(args, name, maxLength) is { Length: > 0 } value ? value : throw new ArgumentException($"缺少 {name}。");

    private static string? Optional(JsonElement args, string name, int maxLength)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须是 JSON 对象。");
        if (!args.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new ArgumentException($"{name} 必须是文字。");
        var value = property.GetString()?.Trim();
        if (value?.Length > maxLength) throw new ArgumentException($"{name} 太长。");
        return value;
    }

    private static int Limit(JsonElement args, int fallback)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || !args.TryGetProperty("limit", out var value))
            return fallback;
        if (!value.TryGetInt32(out var parsed) || parsed is < 1 or > 100) throw new ArgumentException("limit 必须在 1 到 100 之间。");
        return parsed;
    }

    private static long? OptionalLong(JsonElement args, string name)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt64(out var parsed)) throw new ArgumentException($"{name} 必须是整数。");
        return parsed;
    }

    private static ResourceKind? ResourceKindFilter(JsonElement args)
    {
        var text = Optional(args, "kind", 20);
        if (text is null) return null;
        if (!Enum.TryParse<ResourceKind>(text, true, out var kind) || !Enum.IsDefined(kind))
            throw new ArgumentException("kind 只能是 File 或 Folder。");
        return kind;
    }

    private static bool Matches(RemoteResource item, string? query, ResourceKind? kind, long? min, long? max) =>
        (query is null || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Note.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
        (kind is null || item.Kind == kind) && (min is null || item.Size >= min) && (max is null || item.Size <= max);

    private static object ResourceInfo(RemoteResource resource) => new
    {
        resource.Id, resource.Name, kind = resource.Kind.ToString(), mode = resource.Mode.ToString(),
        resource.Size, resource.Available, resource.Note, resource.GroupId, resource.ServerStored
    };
}
