using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed class WorkspacePublisher(NodeStore store)
{
    private readonly ResourceCatalog catalog = new(store);
    private readonly Dictionary<string, (long Size, DateTimeOffset Modified, SharedUpdatePackage? Package)> updates = [];
    private static readonly ConcurrentDictionary<string, WorkspaceFileMetadata> hashes = new();

    public async Task<WorkspacePublishedCatalog> BuildAsync(ServerBinding binding, CancellationToken token)
    {
        var resources = new List<WorkspacePublishedResource>();
        foreach (var resource in store.GetResources().Where(r => store.IsServerPublished(binding.Id, "resource", r.Id)))
        {
            token.ThrowIfCancellationRequested();
            var info = catalog.Describe(resource, token); var permission = store.GetEffectiveGroupPermission(resource.GroupId);
            SharedUpdatePackage? update = null;
            if (resource.Kind == ResourceKind.File && resource.Name.StartsWith("ResourceManager", StringComparison.OrdinalIgnoreCase))
            {
                if (!updates.TryGetValue(resource.Id, out var cached) || cached.Size != info.Size || cached.Modified != info.ModifiedUtc)
                {
                    update = await catalog.FindLatestUpdateAsync(token, r => r.Id == resource.Id).ConfigureAwait(false);
                    updates[resource.Id] = (info.Size, info.ModifiedUtc, update);
                }
                else update = cached.Package;
            }
            resources.Add(new(info, permission.Access, permission.DeviceIds, update));
        }
        var ids = resources.Select(r => r.Resource.GroupId).ToHashSet();
        var allGroups = store.GetResourceGroups().ToDictionary(g => g.Id);
        foreach (var id in ids.ToArray())
        {
            var parent = allGroups.GetValueOrDefault(id)?.ParentId; var visited = new HashSet<string>();
            while (parent is not null && visited.Add(parent) && allGroups.TryGetValue(parent, out var ancestor)) { ids.Add(parent); parent = ancestor.ParentId; }
        }
        var groups = allGroups.Values.Where(g => ids.Contains(g.Id)).ToArray();
        return new(groups, resources, groups.Select(g => { var p = store.GetEffectiveGroupPermission(g.Id); return new GroupPermission(g.Id, p.Access, p.DeviceIds); }).ToArray());
    }

    public async Task RunAsync(WorkspaceClient client, ServerBinding binding, WorkspaceSession session, Action<string?> report, CancellationToken token)
    {
        async Task PublishLoop()
        {
            string? previous = null;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var catalog = await Task.Run(() => BuildAsync(binding, token), token).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(catalog, WorkspaceProtocol.Json);
                    if (json != previous) { await client.PublishAsync(binding, session, catalog, token).ConfigureAwait(false); previous = json; }
                    report(null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { report("目录同步失败：" + ex.Message); }
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            }
        }
        async Task RelayLoop()
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var requests = await client.PollRelayAsync(binding, session, token).ConfigureAwait(false);
                    await Task.WhenAll(requests.Select(async ticket =>
                    {
                        var response = await HandleAsync(ticket.Request, ticket.PublicKey, token).ConfigureAwait(false);
                        await client.ReplyRelayAsync(binding, session, ticket.Id, response, token).ConfigureAwait(false);
                    })).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { report("中继连接失败：" + ex.Message); await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            }
        }
        try { await Task.WhenAll(PublishLoop(), RelayLoop()).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task<WorkspaceResourceReply> HandleAsync(WorkspaceResourceRequest request, string requesterKey, CancellationToken token)
    {
        try
        {
            if (request.ResourceId is null || request.Path is null || request.Nonce is null || request.Signature is null || request.Owner != store.GetSettings().Profile.DeviceId || !WorkspaceResourceRules.Valid(request, requesterKey)) return new(403, []);
            var binding = store.GetServerBindings().FirstOrDefault(b => b.ServerId == request.ServerId);
            var member = binding?.Cached?.Members.FirstOrDefault(m => m.Profile.DeviceId == request.Requester);
            if (binding is null || member?.PublicKey != requesterKey || store.GetTrustedDeviceKey(request.Requester) is { } trusted && trusted != requesterKey) return new(403, []);
            var resource = store.GetResource(request.ResourceId);
            if (resource is null || !store.IsServerPublished(binding.Id, "resource", resource.Id)) return new(404, []);
            if (request.Requester != request.Owner && !store.CanAccessGroup(resource.GroupId, request.Requester)) return new(403, []);
            if (request.Operation == "describe") return WorkspaceResourceRules.Json(catalog.Describe(resource, token));
            if (request.Operation == "tree")
            {
                var reply = WorkspaceResourceRules.Json(catalog.ListFiles(resource.Id));
                return reply.Data.Length <= 6 * 1024 * 1024 ? reply : new(413, []);
            }
            var file = catalog.ResolveFile(resource.Id, request.Path);
            if (!file.Exists) return new(404, []);
            var metadata = await MetadataAsync(file, token).ConfigureAwait(false);
            if (request.Operation == "meta") return WorkspaceResourceRules.Json(metadata);
            if (request.Etag != metadata.Etag) return new(409, []);
            if (request.Offset > metadata.Size) return new(416, []);
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            stream.Position = request.Offset;
            var bytes = new byte[(int)Math.Min(WorkspaceResourceRules.ChunkSize, metadata.Size - request.Offset)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            file.Refresh();
            if (file.Length != metadata.Size) return new(409, []);
            return new(200, bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return new(403, []); }
        catch (FileNotFoundException) { return new(404, []); }
        catch (ArgumentException) { return new(400, []); }
        catch (IOException) { return new(409, []); }
    }
    private static async Task<WorkspaceFileMetadata> MetadataAsync(FileInfo file, CancellationToken token)
    {
        var size = file.Length; var modified = file.LastWriteTimeUtc.Ticks;
        var key = file.FullName + "\0" + size + "\0" + modified;
        if (hashes.TryGetValue(key, out var cached)) return cached;
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        file.Refresh(); if (file.Length != size || file.LastWriteTimeUtc.Ticks != modified) throw new IOException("文件已变化。");
        var result = new WorkspaceFileMetadata(size, "\"" + hash + "\"", hash);
        if (hashes.Count > 1024) hashes.Clear(); hashes[key] = result; return result;
    }
}
