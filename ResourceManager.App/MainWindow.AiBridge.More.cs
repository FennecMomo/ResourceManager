using System.IO;
using System.Text.Json;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly Dictionary<string, AiPrivateResourceOperation> aiPrivateResourceOperations = [];

    private sealed class AiPrivateResourceOperation(string id)
    {
        public string Id { get; } = id;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "Running";
        public string? MessageId { get; set; }
        public string? Error { get; set; }
    }

    private async Task<AiBridgeResponse> HandleAdditionalAiBridgeAsync(AiBridgeRequest request, CancellationToken token)
    {
        var args = request.Arguments;
        switch (request.Command)
        {
            case "list_servers":
                return new(true, Data: new { servers = Servers.Select(row => new
                {
                    id = row.Binding.Id, serverIdentity = row.Binding.ServerId,
                    row.Name, row.Address, row.Status, row.ServerVersion,
                    row.SupportsResources, row.SupportsStorage, row.SyncError,
                    members = row.Members.Select(member => new { member.DeviceId, member.Nickname,
                        member.State, member.Version, member.LastSeen, member.ResourceCount }).ToArray(),
                    catalogOwners = row.Catalogs.Select(catalog => new { deviceId = catalog.Owner,
                        catalog.Online, resourceCount = catalog.Catalog.Resources.Count }).ToArray()
                }).ToArray() });

            case "list_server_resources":
            {
                var row = RequiredServer(args);
                var ownerId = Required(args, "deviceId", 100);
                var catalog = row.Catalogs.FirstOrDefault(item => item.Owner == ownerId);
                if (catalog is null) return new(false, "server_catalog_not_loaded");
                var query = Optional(args, "query", 100);
                var kind = ResourceKindFilter(args);
                var matches = catalog.Catalog.Resources.Where(item => Matches(item, query, kind, null, null)).ToArray();
                return new(true, Data: new { serverId = row.Binding.Id, deviceId = ownerId,
                    online = row.Status == "在线", publisherOnline = catalog.Online,
                    groups = catalog.Catalog.Groups.Select(group => new { group.Id, group.Name, group.ParentId }).ToArray(),
                    total = matches.Length, resources = matches.Take(Limit(args, 100)).Select(ResourceInfo).ToArray() });
            }

            case "list_downloads":
                return new(true, Data: new { downloads = store.GetDownloads().Take(Limit(args, 100))
                    .Select(job => new { job.Id, job.PeerId, job.ResourceId, job.ResourceName,
                        kind = job.Kind.ToString(), job.TargetPath, job.Status, job.DownloadedBytes,
                        job.TotalBytes, job.Error, running = activeDownloads.ContainsKey(job.Id),
                        server = store.GetServerDownload(job.Id)?.Server,
                        serverStored = store.GetServerDownload(job.Id)?.Stored }).ToArray() });

            case "download_resource":
            {
                var source = Required(args, "source", 20);
                var peerId = Required(args, "deviceId", 100);
                var resourceId = Required(args, "resourceId", 100);
                var destination = Required(args, "targetDirectory", 2048);
                if (!Path.IsPathFullyQualified(destination) || !Directory.Exists(destination))
                    return new(false, "existing_absolute_target_directory_required");
                DownloadJob job;
                if (source.Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    var peer = Peers.FirstOrDefault(item => item.Peer.DeviceId == peerId);
                    if (peer is null || peer.Status != "在线" || !peerCatalogs.TryGetValue(peerId, out var resources))
                        return new(false, "device_or_catalog_unavailable");
                    var resource = resources.FirstOrDefault(item => item.Id == resourceId && item.Available);
                    if (resource is null) return new(false, "resource_unavailable");
                    job = downloader.CreateJob(peer.Peer, resource, destination);
                }
                else if (source.Equals("server", StringComparison.OrdinalIgnoreCase))
                {
                    var row = RequiredServer(args);
                    if (row.Status != "在线" || row.Session is null) return new(false, "server_unavailable");
                    var catalog = row.Catalogs.FirstOrDefault(item => item.Owner == peerId);
                    var resource = catalog?.Catalog.Resources.FirstOrDefault(item => item.Id == resourceId && item.Available);
                    if (resource is null || catalog is null || !catalog.Online && !resource.ServerStored)
                        return new(false, "resource_unavailable");
                    var owner = row.Members.FirstOrDefault(item => item.DeviceId == peerId);
                    var ownerName = owner?.Nickname ?? peerId;
                    job = downloader.CreateJob(new(peerId, "", 0, ownerName, null, null), resource, destination);
                    store.SaveServerDownload(job.Id, row.Binding.Id, ownerName, resource.ServerStored);
                }
                else return new(false, "invalid_source");
                RefreshDownloadsView();
                QueueDownload(job.Id);
                return new(true, Data: DownloadInfo(job.Id));
            }

            case "pause_download":
            {
                var id = Required(args, "jobId", 100);
                var job = store.GetDownload(id);
                if (job is null) return new(false, "download_not_found");
                if (job.Status == "已完成") return new(false, "download_completed");
                if (activeDownloads.TryGetValue(id, out var active))
                {
                    active.Cancellation.Cancel();
                    await active.Task;
                }
                return new(true, Data: DownloadInfo(id));
            }

            case "resume_download":
            {
                var id = Required(args, "jobId", 100);
                var job = store.GetDownload(id);
                if (job is null) return new(false, "download_not_found");
                if (job.Status == "已完成") return new(false, "download_completed");
                QueueDownload(id);
                return new(true, Data: DownloadInfo(id));
            }

            case "list_favorites":
            {
                var favorites = store.GetFavorites().Concat(store.GetServerFavorites()).ToArray();
                return new(true, Data: new { total = favorites.Length,
                    favorites = favorites.Take(Limit(args, 100)).Select(favorite => new
                    {
                        favorite.PeerId, favorite.ResourceId, favorite.Name,
                        kind = favorite.Kind.ToString(), favorite.ServerId, favorite.ServerStored,
                        serverBindingId = Servers.FirstOrDefault(row => row.Binding.ServerId == favorite.ServerId)?.Binding.Id,
                        status = Favorites.FirstOrDefault(row => row.Favorite == favorite)?.Status ?? "未检查"
                    }).ToArray() });
            }

            case "add_favorite":
            {
                var source = Required(args, "source", 20);
                var peerId = Required(args, "deviceId", 100);
                var resourceId = Required(args, "resourceId", 100);
                if (source.Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    if (Peers.All(item => item.Peer.DeviceId != peerId || item.Status != "在线") ||
                        !peerCatalogs.TryGetValue(peerId, out var resources)) return new(false, "device_or_catalog_unavailable");
                    var resource = resources.FirstOrDefault(item => item.Id == resourceId);
                    if (resource is null) return new(false, "resource_not_found");
                    store.SaveFavorite(new(peerId, resource.Id, resource.Name, resource.Kind));
                }
                else if (source.Equals("server", StringComparison.OrdinalIgnoreCase))
                {
                    var row = RequiredServer(args);
                    if (row.Status != "在线") return new(false, "server_unavailable");
                    var stored = OptionalBool(args, "serverStored") ?? false;
                    var resource = row.Catalogs.FirstOrDefault(item => item.Owner == peerId)?.Catalog.Resources
                        .FirstOrDefault(item => item.Id == resourceId && item.ServerStored == stored);
                    if (resource is null) return new(false, "resource_not_found");
                    store.SaveServerFavorite(new(peerId, resource.Id, resource.Name, resource.Kind,
                        row.Binding.ServerId, resource.ServerStored));
                }
                else return new(false, "invalid_source");
                RefreshFavoritesView();
                return new(true, Data: new { added = true, deviceId = peerId, resourceId });
            }

            case "remove_favorite":
            {
                var source = Required(args, "source", 20);
                var peerId = Required(args, "deviceId", 100);
                var resourceId = Required(args, "resourceId", 100);
                if (source.Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    if (!store.GetFavorites().Any(item => item.PeerId == peerId && item.ResourceId == resourceId))
                        return new(false, "favorite_not_found");
                    store.RemoveFavorite(peerId, resourceId);
                }
                else if (source.Equals("server", StringComparison.OrdinalIgnoreCase))
                {
                    var serverId = Required(args, "serverId", 100);
                    var identity = Servers.FirstOrDefault(row => row.Binding.Id == serverId)?.Binding.ServerId ?? serverId;
                    var stored = OptionalBool(args, "serverStored") ?? false;
                    var favorite = store.GetServerFavorites().FirstOrDefault(item => item.ServerId == identity &&
                        item.PeerId == peerId && item.ResourceId == resourceId && item.ServerStored == stored);
                    if (favorite is null) return new(false, "favorite_not_found");
                    store.RemoveServerFavorite(favorite);
                }
                else return new(false, "invalid_source");
                RefreshFavoritesView();
                return new(true, Data: new { removed = true, deviceId = peerId, resourceId });
            }

            case "set_publication_note":
            {
                var resourceId = Required(args, "resourceId", 100);
                if (store.GetResource(resourceId) is null) return new(false, "publication_not_found");
                store.SetResourceNote(resourceId, Optional(args, "note", 200) ?? "");
                RefreshLocalView();
                return new(true, Data: new { resourceId, note = store.GetResource(resourceId)?.Note });
            }

            case "move_publication":
            {
                var resourceId = Required(args, "resourceId", 100);
                var groupId = Required(args, "groupId", 100);
                store.MoveResourceToGroup(resourceId, groupId);
                RefreshLocalView();
                return new(true, Data: new { resourceId, groupId });
            }

            case "set_group_permission":
            {
                var groupId = Required(args, "groupId", 100);
                var access = ParseAccess(Required(args, "access", 20), allowInherit: true);
                var ids = StringArray(args, "allowedDeviceIds", 100);
                store.SetGroupPermission(groupId, access, ids);
                RefreshLocalView();
                var effective = store.GetEffectiveGroupPermission(groupId);
                return new(true, Data: new { groupId, access = access.ToString(),
                    effectiveAccess = effective.Access.ToString(), allowedDeviceIds = effective.DeviceIds });
            }

            case "set_server_publication":
            {
                var row = RequiredServer(args);
                var kind = Required(args, "kind", 20).ToLowerInvariant();
                var id = Required(args, "id", 100);
                var enabled = OptionalBool(args, "enabled") ?? throw new ArgumentException("缺少 enabled。");
                if (kind == "resource" && store.GetResource(id) is null ||
                    kind == "group" && !store.GetResourceGroups().Any(group => group.Id == id) ||
                    kind is not ("resource" or "group")) return new(false, "publication_not_found");
                store.SetServerPublication(row.Binding.Id, kind, id, enabled);
                return new(true, Data: new { serverId = row.Binding.Id, kind, id,
                    enabled = store.IsServerPublished(row.Binding.Id, kind, id) });
            }

            case "revoke_publication":
            {
                var resourceId = Required(args, "resourceId", 100);
                var resource = store.GetResource(resourceId);
                if (resource is null) return new(false, "publication_not_found");
                if (publishing) return new(false, "publishing_resources");
                await Task.Run(() => store.RemoveResource(resourceId), token);
                RefreshLocalView();
                return new(true, Data: new { resourceId, revoked = true,
                    managedCopyDeleted = resource.Mode == PublishMode.Copy,
                    originalSourcePreserved = resource.Mode == PublishMode.Reference });
            }

            case "list_uploads":
            {
                var uploads = store.GetUploads();
                return new(true, Data: new { total = uploads.Count,
                    uploads = uploads.Take(Limit(args, 100)).Select(job => new { job.Id,
                        serverId = job.Server, job.SourcePath, job.Status, job.Sent, job.Total, job.Error,
                        job.Spec.Name, kind = job.Spec.Kind.ToString(), access = job.Spec.Access.ToString(),
                        job.Spec.Allowed, running = activeUploads.ContainsKey(job.Id) }).ToArray() });
            }

            case "upload_to_server":
            {
                var row = RequiredServer(args);
                if (row.Status != "在线" || row.Session is null || !row.SupportsStorage)
                    return new(false, "server_storage_unavailable");
                var path = Required(args, "path", 2048);
                if (!Path.IsPathFullyQualified(path)) return new(false, "absolute_path_required");
                var access = ParseAccess(Required(args, "access", 20), allowInherit: false);
                var ids = StringArray(args, "allowedDeviceIds", 100);
                if (access == GroupAccess.AllowList && ids.Any(id => row.Members.All(member => member.DeviceId != id)))
                    return new(false, "unknown_allowed_device");
                var job = new WorkspaceUploadManager(store, workspaceClient!).Create(row.Binding, path, access,
                    access == GroupAccess.AllowList ? ids : []);
                StartUpload(job.Id);
                RefreshUploads();
                return new(true, Data: UploadInfo(job.Id));
            }

            case "pause_upload":
            {
                var id = Required(args, "jobId", 100);
                if (store.GetUploads().All(job => job.Id != id)) return new(false, "upload_not_found");
                if (activeUploads.TryGetValue(id, out var active))
                {
                    active.Cancellation.Cancel();
                    await active.Task;
                }
                return new(true, Data: UploadInfo(id));
            }

            case "resume_upload":
            {
                var id = Required(args, "jobId", 100);
                var job = store.GetUploads().FirstOrDefault(item => item.Id == id);
                if (job is null) return new(false, "upload_not_found");
                if (job.Status == "已完成") return new(false, "upload_completed");
                StartUpload(id);
                return new(true, Data: UploadInfo(id));
            }

            case "set_stored_permission":
            {
                var row = RequiredOnlineServer(args);
                var resourceId = Required(args, "resourceId", 100);
                if (!IsOwnStoredResource(row, resourceId)) return new(false, "stored_resource_not_owned_or_unavailable");
                var access = ParseAccess(Required(args, "access", 20), allowInherit: false);
                var ids = StringArray(args, "allowedDeviceIds", 100);
                if (access == GroupAccess.AllowList && ids.Any(id => row.Members.All(member => member.DeviceId != id)))
                    return new(false, "unknown_allowed_device");
                var permission = new StoredPermission(access, access == GroupAccess.AllowList ? ids : []);
                if (!await workspaceClient!.StoredPermissionAsync(row.Binding, row.Session!, resourceId, permission, token))
                    return new(false, "server_rejected_permission_change");
                var job = store.GetUploads().FirstOrDefault(item => item.Id == resourceId);
                if (job is not null) store.SaveUpload(job with { Spec = job.Spec with { Access = access, Allowed = permission.Allowed } });
                row.SetCatalogs(await workspaceClient.CatalogsAsync(row.Binding, row.Session!, token));
                RefreshFavoritesView();
                return new(true, Data: new { resourceId, access = access.ToString(), allowedDeviceIds = permission.Allowed });
            }

            case "delete_stored_resource":
            {
                var row = RequiredOnlineServer(args);
                var resourceId = Required(args, "resourceId", 100);
                if (!IsOwnStoredResource(row, resourceId)) return new(false, "stored_resource_not_owned_or_unavailable");
                if (!await workspaceClient!.DeleteStoredAsync(row.Binding, row.Session!, resourceId, token))
                    return new(false, "server_rejected_delete");
                store.RemoveUpload(resourceId);
                RefreshUploads();
                row.SetCatalogs(await workspaceClient.CatalogsAsync(row.Binding, row.Session!, token));
                RefreshFavoritesView();
                return new(true, Data: new { resourceId, deleted = true });
            }

            case "mute_conversation":
            {
                var peerId = Required(args, "deviceId", 100);
                var peer = store.GetPeer(peerId);
                if (peer is null) return new(false, "device_not_found");
                var minutes = RequiredInt(args, "minutes", 0, 10080);
                store.EnsureChatConversation(peerId, peer.Nickname);
                var until = minutes == 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddMinutes(minutes);
                store.SetChatMutedUntil(peerId, until);
                RefreshChatConversations();
                RefreshChatHeader();
                return new(true, Data: new { deviceId = peerId, mutedUntilUtc = until });
            }

            case "retry_message":
            {
                var peerId = Required(args, "deviceId", 100);
                var messageId = Required(args, "messageId", 100);
                chat.Retry(peerId, messageId);
                RefreshChatTimeline();
                _ = PumpChatSafeAsync();
                return new(true, Data: new { deviceId = peerId, messageId, state = "Queued" });
            }

            case "send_private_resource":
            {
                var peerId = Required(args, "deviceId", 100);
                var path = Required(args, "path", 2048);
                if (!Path.IsPathFullyQualified(path)) return new(false, "absolute_path_required");
                if (!File.Exists(path) && !Directory.Exists(path)) return new(false, "path_not_found");
                if (store.GetPeer(peerId) is null) return new(false, "device_not_found");
                if (!store.GetPeerCapabilities(peerId).Contains(NodeDefaults.PrivateResourceCapability, StringComparer.Ordinal))
                    return new(false, "private_resource_not_supported");
                if (preparingChatResource) return new(false, "private_resource_preparation_running");
                foreach (var old in aiPrivateResourceOperations.Values.Where(item =>
                             item.State != "Running" && DateTimeOffset.UtcNow - item.StartedUtc > TimeSpan.FromDays(1))
                             .Select(item => item.Id).ToArray()) aiPrivateResourceOperations.Remove(old);
                var operation = new AiPrivateResourceOperation(Guid.NewGuid().ToString("N"));
                aiPrivateResourceOperations[operation.Id] = operation;
                preparingChatResource = true;
                RefreshChatHeader();
                _ = RunAiPrivateResourceAsync(operation, peerId, path);
                return new(true, Data: PrivateResourceOperationInfo(operation));
            }

            case "get_private_resource_operation":
            {
                var id = Required(args, "operationId", 100);
                return aiPrivateResourceOperations.TryGetValue(id, out var operation)
                    ? new(true, Data: PrivateResourceOperationInfo(operation))
                    : new(false, "operation_not_found");
            }

            case "check_updates":
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                var githubTask = updateClient.GetLatestAsync(deadline.Token);
                var localTask = FindLocalUpdatesAsync(deadline.Token);
                var serverTask = FindServerUpdatesAsync(deadline.Token);
                AppUpdate? github = null;
                string? githubError = null;
                try { github = await githubTask; }
                catch (Exception ex) when (ex is not OperationCanceledException) { githubError = ex.Message; }
                var local = (await localTask).Concat(await serverTask)
                    .OrderByDescending(item => item.Version).ToArray();
                return new(true, Data: new { currentVersion = AppVersion,
                    github = github is null ? null : new { version = github.Version.ToString(), github.Size, github.Sha256 },
                    githubError, serverErrors = updateServerFailures,
                    sources = local.Select(item => new { version = item.Version.ToString(),
                        item.Peer.DeviceId, item.Peer.Nickname, item.Package.ResourceId,
                        item.Package.Size, item.Package.Sha256, item.ServerBindingId,
                        item.ServerName, item.ServerStored }).ToArray() });
            }

            case "get_update_status":
                return new(true, Data: new { currentVersion = AppVersion, checkingUpdate,
                    status = UpdateStatusText.Text, staged = stagedUpdate is null ? null : new
                    { stagedUpdate.PackagePath, stagedUpdate.Sha256, stagedUpdate.Version } });

            default:
                return new(false, "unsupported_command");
        }
    }

    private async Task RunAiPrivateResourceAsync(AiPrivateResourceOperation operation, string peerId, string path)
    {
        try
        {
            chatResourcePreparation = Task.Run(() => chat.QueuePrivateResource(peerId, path));
            var message = await chatResourcePreparation;
            operation.MessageId = message.MessageId;
            operation.State = "Completed";
            if (!exiting)
            {
                RefreshChatConversations();
                if (SelectedChatPeerId == peerId) RefreshChatTimeline();
                _ = PumpChatSafeAsync();
            }
        }
        catch (Exception ex)
        {
            operation.Error = ex.Message;
            operation.State = "Failed";
            AppLog.Write("MCP 私发资源失败", ex);
        }
        finally
        {
            preparingChatResource = false;
            chatResourcePreparation = null;
            if (!exiting) RefreshChatHeader();
        }
    }

    private object DownloadInfo(string id)
    {
        var job = store.GetDownload(id)!;
        return new { jobId = id, job.Status, job.TargetPath, job.DownloadedBytes, job.TotalBytes,
            job.Error, running = activeDownloads.ContainsKey(id) };
    }

    private object UploadInfo(string id)
    {
        var job = store.GetUploads().First(item => item.Id == id);
        return new { jobId = id, job.Status, job.Sent, job.Total, job.Error,
            running = activeUploads.ContainsKey(id) };
    }

    private ServerTabRow RequiredServer(JsonElement args)
    {
        var id = Required(args, "serverId", 100);
        return Servers.FirstOrDefault(row => row.Binding.Id == id)
            ?? throw new ArgumentException("server_not_found");
    }

    private ServerTabRow RequiredOnlineServer(JsonElement args)
    {
        var row = RequiredServer(args);
        if (row.Status != "在线" || row.Session is null || !row.SupportsStorage)
            throw new InvalidOperationException("server_storage_unavailable");
        return row;
    }

    private bool IsOwnStoredResource(ServerTabRow row, string resourceId) =>
        row.Catalogs.Any(catalog => catalog.Owner == store.GetSettings().Profile.DeviceId &&
            catalog.Catalog.Resources.Any(resource => resource.Id == resourceId && resource.ServerStored));

    private static object PrivateResourceOperationInfo(AiPrivateResourceOperation operation) => new
    {
        operationId = operation.Id, operation.State, operation.MessageId,
        operation.Error, operation.StartedUtc
    };

    private static GroupAccess ParseAccess(string value, bool allowInherit)
    {
        if (!Enum.TryParse<GroupAccess>(value, true, out var access) || !Enum.IsDefined(access) ||
            !allowInherit && access == GroupAccess.Inherit)
            throw new ArgumentException("access 必须是 Public、Private、AllowList" +
                (allowInherit ? " 或 Inherit。" : "。"));
        return access;
    }

    private static bool? OptionalBool(JsonElement args, string name)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            !args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"{name} 必须是布尔值。");
        return value.GetBoolean();
    }

    private static int RequiredInt(JsonElement args, string name, int min, int max)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"{name} 必须是 {min} 到 {max} 之间的整数。");
        return number;
    }

    private static string[] StringArray(JsonElement args, string name, int maxLength)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            !args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100)
            throw new ArgumentException($"{name} 必须是最多 100 项的数组。");
        var ids = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id ||
                id.Length is < 1 or > 100) throw new ArgumentException($"{name} 包含无效设备 ID。");
            ids.Add(id);
        }
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }
}
