using System.Net;
using System.Text.Json;
using ResourceManager.Core;

namespace ResourceManager.Server;

public sealed partial class WorkspaceHub
{
    private sealed class Pending(WorkspaceRelayTicket ticket)
    {
        public WorkspaceRelayTicket Ticket { get; } = ticket;
        public bool Delivered;
        public TaskCompletionSource<WorkspaceResourceReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, Pending> transfers = [];
    private readonly Dictionary<string, DateTimeOffset> relayReaders = [];
    private readonly Dictionary<string, DateTimeOffset> transferNonces = [];
    private TaskCompletionSource relayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void WakeRelay() { var old = relayChanged; relayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously); old.TrySetResult(); }
    private WorkspacePublishedCatalog ReadCatalog(string owner)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT json FROM catalogs WHERE owner=$owner", "$owner", owner);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<WorkspacePublishedCatalog>(json, WorkspaceProtocol.Json)! : new([], []);
    }
    public bool Publish(string token, string device, WorkspacePublishedCatalog catalog)
    {
        if (catalog.Groups is null || catalog.Resources is null || catalog.Groups.Count > 2000 || catalog.Resources.Count > 2000 || catalog.GroupPermissions?.Count > 2000 ||
            catalog.GroupPermissions?.Any(p => p is null || p.GroupId is null || p.DeviceIds is null || p.DeviceIds.Count > 1000 || p.Access is not (GroupAccess.Public or GroupAccess.Private or GroupAccess.AllowList)) == true ||
            catalog.Groups.Any(g => g is null || string.IsNullOrEmpty(g.Id) || g.Id.Length > 100 || g.Name is null || g.Name.Length > 200) ||
            catalog.Resources.Any(r => r?.Resource is null || string.IsNullOrEmpty(r.Resource.Id) || r.Resource.Id.Length > 100 || string.IsNullOrWhiteSpace(r.Resource.Name) || r.Resource.Name.Length > 255 || r.Resource.Note is null || r.Resource.Note.Length > 200 || string.IsNullOrEmpty(r.Resource.GroupId) || r.Resource.Size < 0 || !Enum.IsDefined(r.Resource.Kind) || !Enum.IsDefined(r.Resource.Mode) || r.Allowed is null || r.Allowed.Count > 1000 || r.Allowed.Any(id => !Guid.TryParse(id, out _)) || r.Access is not (GroupAccess.Public or GroupAccess.Private or GroupAccess.AllowList)) ||
            catalog.Resources.Select(r => r.Resource.Id).Distinct().Count() != catalog.Resources.Count || catalog.Groups.Select(g => g.Id).Distinct().Count() != catalog.Groups.Count)
            throw new WorkspaceException("发布目录无效或过大。", HttpStatusCode.BadRequest);
        lock (gate)
        {
            Authenticate(token, device, false);
            var json = JsonSerializer.Serialize(catalog, WorkspaceProtocol.Json);
            using var db = Open(); using var cmd = Command(db, "INSERT INTO catalogs(owner,json) VALUES($owner,$json) ON CONFLICT(owner) DO UPDATE SET json=excluded.json", "$owner", device, "$json", json); cmd.ExecuteNonQuery();
            Signal(); return true;
        }
    }
    public WorkspaceOwnerCatalog[] Catalogs(string token, string device)
    {
        lock (gate)
        {
            Authenticate(token, device, false);
            return Snapshot().Members.Select(member =>
            {
                var source = ReadCatalog(member.Profile.DeviceId);
                var allowed = source.Resources.Where(r => WorkspaceResourceRules.Allows(r, device, member.Profile.DeviceId)).ToArray();
                var visibleGroups = source.Groups.Where(g => device == member.Profile.DeviceId || source.GroupPermissions?.Any(p => p.GroupId == g.Id && (p.Access == GroupAccess.Public || p.Access == GroupAccess.AllowList && p.DeviceIds.Contains(device))) == true).ToDictionary(g => g.Id);
                var ids = allowed.Select(r => r.Resource.GroupId).ToHashSet();
                foreach (var id in ids.ToArray())
                {
                    var parent = visibleGroups.GetValueOrDefault(id)?.ParentId; var visited = new HashSet<string>();
                    while (parent is not null && visited.Add(parent) && visibleGroups.TryGetValue(parent, out var ancestor)) { ids.Add(parent); parent = ancestor.ParentId; }
                }
                var groupIds = visibleGroups.Keys.Where(ids.Contains).ToHashSet();
                var groups = visibleGroups.Values.Where(g => groupIds.Contains(g.Id)).Select(g => g.ParentId is not null && !groupIds.Contains(g.ParentId) ? g with { ParentId = null } : g).ToArray();
                return new WorkspaceOwnerCatalog(member.Profile.DeviceId, member.Online, new(groups, allowed.Select(r => r.Resource).ToArray()),
                    member.Online ? allowed.Where(r => r.Resource.Available && r.Update is not null && r.Update.ResourceId == r.Resource.Id).Select(r => r.Update!).ToArray() : []);
            }).ToArray();
        }
    }
    private WorkspaceMember AuthorizeTransfer(string viewer, string owner, string resource)
    {
        var member = Snapshot().Members.FirstOrDefault(m => m.Profile.DeviceId == owner);
        if (member is null || !member.Online) throw new WorkspaceException("发布者离线。", HttpStatusCode.ServiceUnavailable);
        var item = ReadCatalog(owner).Resources.FirstOrDefault(r => r.Resource.Id == resource);
        if (item is null || !WorkspaceResourceRules.Allows(item, viewer, owner)) throw new WorkspaceException("资源不可访问。", HttpStatusCode.Forbidden);
        return member;
    }
    public async Task<WorkspaceResourceReply> RelayAsync(string token, string device, WorkspaceResourceRequest request, CancellationToken cancellation)
    {
        Pending pending;
        lock (gate)
        {
            Authenticate(token, device, false);
            var viewer = Snapshot().Members.Single(m => m.Profile.DeviceId == device);
            if (request is null || request.ServerId != store.ServerId || request.Requester != device || request.ResourceId is null || request.Path is null || request.Nonce is null || request.Signature is null || !WorkspaceResourceRules.Valid(request, viewer.PublicKey!)) throw new WorkspaceException("传输签名无效。", HttpStatusCode.BadRequest);
            AuthorizeTransfer(device, request.Owner, request.ResourceId);
            if (!relayReaders.TryGetValue(request.Owner, out var seen) || seen < DateTimeOffset.UtcNow.AddSeconds(-45)) throw new WorkspaceException("发布者未连接中继。", HttpStatusCode.ServiceUnavailable);
            foreach (var old in transferNonces.Where(x => x.Value < DateTimeOffset.UtcNow.AddMinutes(-2)).Select(x => x.Key).ToArray()) transferNonces.Remove(old);
            if (transfers.Count >= 128 || transfers.Values.Count(p => p.Ticket.Request.Owner == request.Owner) >= 8 || transferNonces.Count >= 50000) throw new WorkspaceException("传输繁忙。", HttpStatusCode.TooManyRequests);
            if (!transferNonces.TryAdd(device + ":" + request.Nonce, DateTimeOffset.UtcNow)) throw new WorkspaceException("重复传输请求。", HttpStatusCode.Conflict);
            var id = Guid.NewGuid().ToString("N"); pending = new(new(id, request, viewer.PublicKey!)); transfers.Add(id, pending); WakeRelay();
        }
        try
        {
            var reply = await pending.Completion.Task.WaitAsync(request.Operation == "meta" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(25), cancellation);
            lock (gate) { Authenticate(token, device, false); AuthorizeTransfer(device, request.Owner, request.ResourceId); }
            return reply;
        }
        catch (TimeoutException) { throw new WorkspaceException("发布者响应超时。", HttpStatusCode.ServiceUnavailable); }
        finally { lock (gate) transfers.Remove(pending.Ticket.Id); }
    }
    public async Task<WorkspaceRelayTicket[]> PollRelayAsync(string token, string device, CancellationToken cancellation)
    {
        Task wait;
        lock (gate) { Authenticate(token, device, false); relayReaders[device] = DateTimeOffset.UtcNow; wait = transfers.Values.Any(p => p.Ticket.Request.Owner == device && !p.Delivered) ? Task.CompletedTask : relayChanged.Task; }
        try { await wait.WaitAsync(TimeSpan.FromSeconds(15), cancellation); } catch (TimeoutException) { }
        lock (gate)
        {
            Authenticate(token, device, false); relayReaders[device] = DateTimeOffset.UtcNow;
            var pending = transfers.Values.Where(p => p.Ticket.Request.Owner == device && !p.Delivered).Take(4).ToArray();
            foreach (var item in pending) item.Delivered = true;
            return pending.Select(p => p.Ticket).ToArray();
        }
    }
    public bool ReplyRelay(string token, string device, string id, WorkspaceResourceReply reply)
    {
        lock (gate)
        {
            Authenticate(token, device, false);
            if (reply.Data is null || reply.Data.Length > 6 * 1024 * 1024 || reply.Status is < 200 or > 599) throw new WorkspaceException("传输响应无效。", HttpStatusCode.BadRequest);
            if (!transfers.TryGetValue(id, out var pending)) return false;
            if (pending.Ticket.Request.Owner != device) throw new WorkspaceException("不能响应其他设备的请求。", HttpStatusCode.Forbidden);
            var limit = pending.Ticket.Request.Operation == "tree" ? 6 * 1024 * 1024 : pending.Ticket.Request.Operation == "chunk" ? WorkspaceResourceRules.ChunkSize : 65536;
            if (reply.Data.Length > limit) throw new WorkspaceException("传输响应超过请求上限。", HttpStatusCode.BadRequest);
            return pending.Completion.TrySetResult(reply);
        }
    }
}

public static partial class WorkspaceEndpoints
{
    private static void MapWorkspaceResources(this WebApplication app)
    {
        app.MapPut("/api/v1/workspace/catalog", (WorkspacePublishedCatalog catalog, HttpContext c, WorkspaceHub hub) => Execute(() => Results.Ok(hub.Publish(Token(c), Device(c), catalog))));
        app.MapGet("/api/v1/workspace/catalogs", (HttpContext c, WorkspaceHub hub) => Execute(() => Results.Ok(hub.Catalogs(Token(c), Device(c)))));
        app.MapPost("/api/v1/workspace/relay", (WorkspaceResourceRequest request, HttpContext c, WorkspaceHub hub, CancellationToken token) => ResourceAsync(async () => Results.Ok(await hub.RelayAsync(Token(c), Device(c), request, token))));
        app.MapGet("/api/v1/workspace/relay/poll", (HttpContext c, WorkspaceHub hub, CancellationToken token) => ResourceAsync(async () => Results.Ok(await hub.PollRelayAsync(Token(c), Device(c), token))));
        app.MapPost("/api/v1/workspace/relay/{id}", (string id, WorkspaceResourceReply reply, HttpContext c, WorkspaceHub hub) => Execute(() => Results.Ok(hub.ReplyRelay(Token(c), Device(c), id, reply))));
    }
    private static async Task<IResult> ResourceAsync(Func<Task<IResult>> action)
    { try { return await action(); } catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.Code); } }
}
