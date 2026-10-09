using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceManager.Core;

public sealed class PeerNode : IAsyncDisposable
{
    private readonly NodeStore store;
    private readonly ResourceCatalog catalog;
    private readonly RouterDiscoveryCoordinator routerDiscovery;
    private readonly UpnpPortMappingManager mappingManager;
    private readonly ReminderService? reminders;
    private readonly ChatService? chat;
    private readonly GitCollaborationStore gitProjects;
    private WebApplication? app;

    public PeerNode(NodeStore store)
        : this(store, new LanDiscoveryService(store),
            new UpnpPortMappingManager(store, new UpnpGatewayClient()))
    {
    }

    public PeerNode(NodeStore store, LanDiscoveryService discovery, UpnpPortMappingManager mappingManager,
        ReminderService? reminders = null, GitCollaborationStore? gitProjects = null, ChatService? chat = null)
    {
        this.store = store;
        this.mappingManager = mappingManager;
        this.reminders = reminders;
        this.chat = chat;
        this.gitProjects = gitProjects ?? new GitCollaborationStore(store.DataDirectory);
        catalog = new ResourceCatalog(store);
        routerDiscovery = new RouterDiscoveryCoordinator(store, discovery, mappingManager);
    }

    public bool IsRunning => app is not null;
    public int Port { get; private set; }

    private PeerHello Self()
    {
        var settings = store.GetSettings();
        var capabilities = new List<string> { NodeDefaults.RouterDiscoveryCapability, NodeDefaults.UpnpMappingCapability,
            "git-collaboration-v1", "resource-groups-v1", PeerProof.Capability, "resource-access-v1", "workspace-resources-v1" };
        if (reminders is not null) capabilities.Add(NodeDefaults.ReminderCapability);
        if (chat is not null) capabilities.AddRange([NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability, NodeDefaults.ChatProgressCapability]);
        return new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort, settings.Profile.Avatar,
            capabilities.ToArray());
    }

    public async Task StartAsync(int port, string listenAddress = "0.0.0.0", CancellationToken cancellationToken = default)
    {
        if (app is not null) throw new InvalidOperationException("节点已经启动。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls($"http://{listenAddress}:{port}");
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        var instance = builder.Build();
        var authentication = new PeerAuthenticationServer(store);
        instance.Use(async (context, next) =>
        {
            var authHello = context.Request.Path == "/api/v1/auth/hello";
            var signed = context.Request.Headers.ContainsKey("X-RM-Signature");
            if ((signed || authHello) && !await authentication.VerifyAsync(context, authHello))
            {
                context.Response.StatusCode = 401; return;
            }
            if (context.Request.Path.Value is not ("/api/v1/health" or "/api/v1/hello" or "/api/v1/auth/hello") && !signed)
            {
                var ip = RemoteIp(context);
                if (!store.IsKnownIp(ip))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            if (context.Request.Path.Value is "/api/v1/reminders" or "/api/v1/chat/messages" or "/api/v1/chat/progress")
            {
                var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = ChatService.MaxBodyBytes;
                if (context.Request.ContentLength > ChatService.MaxBodyBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
            }
            if (context.Request.Path.Value == "/api/v1/git/sync")
            {
                var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 2 * 1024 * 1024;
            }
            await next();
        });
        instance.MapGet("/api/v1/health", () => Results.Ok(Self()));
        instance.MapPost("/api/v1/hello", (PeerHello hello, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(hello.DeviceId) || hello.DeviceId.Length > 100 ||
                string.IsNullOrWhiteSpace(hello.Nickname) || hello.Nickname.Length > 80 ||
                hello.Port is < 1 or > 65535 || hello.Avatar?.Length > 512 * 1024 ||
                hello.DeviceId == store.GetSettings().Profile.DeviceId) return Results.BadRequest("设备资料无效。");
            var ip = RemoteIp(context);
            if (store.GetTrustedDeviceKey(hello.DeviceId) is not null) return Results.Ok(Self());
            store.UpsertPeer(new PeerInfo(hello.DeviceId, ip, hello.Port, hello.Nickname.Trim(), hello.Avatar,
                DateTimeOffset.UtcNow));
            store.SavePeerCapabilities(hello.DeviceId, hello.Capabilities);
            return Results.Ok(Self());
        });
        string? Visitor(HttpContext context) => context.Items["VerifiedDevice"] as string;
        var workspacePublisher = new WorkspacePublisher(store);
        instance.MapPost("/api/v1/workspace-resource", async (WorkspaceResourceRequest request, HttpContext context, CancellationToken token) =>
        {
            if (Visitor(context) != request.Requester) return Results.StatusCode(403);
            return Results.Ok(await workspacePublisher.HandleAsync(request, context.Request.Headers["X-RM-Key"].ToString(), token));
        });
        instance.MapPost("/api/v1/auth/hello", (PeerHello hello, HttpContext context) =>
        {
            if (hello.DeviceId != Visitor(context) || string.IsNullOrWhiteSpace(hello.Nickname) || hello.Nickname.Length > 80 ||
                hello.Port is < 1 or > 65535 || hello.Avatar?.Length > 512 * 1024) return Results.BadRequest();
            try { store.TrustDeviceKey(hello.DeviceId, context.Request.Headers["X-RM-Key"].ToString()); }
            catch (InvalidOperationException) { return Results.Unauthorized(); }
            store.UpsertPeer(new PeerInfo(hello.DeviceId, RemoteIp(context), hello.Port, hello.Nickname.Trim(), hello.Avatar, DateTimeOffset.UtcNow));
            store.SavePeerCapabilities(hello.DeviceId, hello.Capabilities);
            var self = Self(); var nonce = context.Request.Headers["X-RM-Nonce"].ToString();
            return Results.Ok(new SignedHello(self, nonce, PeerProof.PublicKey(store), PeerProof.Sign(store, PeerProof.HelloPayload(self, nonce, hello.DeviceId))));
        });
        IResult? AccessDenied(string id, HttpContext context)
        {
            var resource = store.GetResource(id);
            if (resource is not null && store.CanAccessGroup(resource.GroupId, Visitor(context))) return null;
            // Unknown and forbidden IDs are indistinguishable unless this device previously saw the resource.
            return Visitor(context) is null || resource is null && store.PreviouslySawResource(id, Visitor(context)) ? Results.NotFound() : Results.StatusCode(403);
        }
        instance.MapGet("/api/v1/resources", (HttpContext context) => Results.Ok(store.VisibleCatalog(Visitor(context), catalog, context.RequestAborted).Resources));
        instance.MapGet("/api/v1/resource-catalog", (HttpContext context) => Results.Ok(store.VisibleCatalog(Visitor(context), catalog, context.RequestAborted)));
        instance.MapGet("/api/v1/resources/{id}", (string id, HttpContext context) => AccessDenied(id, context) ?? Results.Ok(catalog.Describe(store.GetResource(id)!, context.RequestAborted)));
        instance.MapPost("/api/v1/git/sync", (GitSyncRequest request) =>
        {
            if (request.Events is null || request.Known is null ||
                request.Events.Length > 50 || request.Known.Length > 2000)
                return Results.BadRequest("协作记录数量过多。");
            gitProjects.Merge(request.Events);
            return Results.Ok(new GitSyncResponse(gitProjects.GetMissing(request.Known), gitProjects.GetVersions()));
        });
        instance.MapGet("/api/v1/git/bundles/{hash}", (string hash) =>
        {
            if (!gitProjects.GetEvents().Any(item => item.BundleHash == hash)) return Results.NotFound();
            var path = gitProjects.BundlePath(hash);
            return path is null ? Results.NotFound() : Results.File(path, "application/octet-stream",
                enableRangeProcessing: true);
        });
        instance.MapGet("/api/v1/updates/latest", async (HttpContext context, CancellationToken token) =>
        {
            var update = await catalog.FindLatestUpdateAsync(token, resource => store.CanAccessGroup(resource.GroupId, Visitor(context))).ConfigureAwait(false);
            var resource = update is null ? null : store.GetResource(update.ResourceId);
            return resource is null || !store.CanAccessGroup(resource.GroupId, Visitor(context)) ? Results.NotFound() : Results.Ok(update);
        });
        instance.MapGet("/api/v1/resources/{id}/tree", (string id, HttpContext context) =>
        {
            if (AccessDenied(id, context) is { } denied) return denied;
            try { return Results.Ok(catalog.ListFiles(id, cancellationToken: context.RequestAborted)); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        instance.MapGet("/api/v1/resources/{id}/content", (string id, string? path, HttpContext context) =>
        {
            if (AccessDenied(id, context) is { } denied) return denied;
            try
            {
                var file = catalog.ResolveFile(id, path);
                if (!file.Exists) return Results.NotFound();
                var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                    $"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");
                return Results.File(File.OpenRead(file.FullName), "application/octet-stream", file.Name,
                    file.LastWriteTimeUtc, etag, enableRangeProcessing: true);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        instance.MapPost("/api/v1/chat/progress", (ChatProgressQuery query, HttpContext context) =>
        {
            var visitor = Visitor(context);
            if (chat is null || visitor is null || store.GetPeer(visitor) is null) return Results.StatusCode(403);
            if (query.MessageIds is null || query.MessageIds.Length > 100 ||
                query.MessageIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64)) return Results.BadRequest();
            var receipts = query.MessageIds.Distinct().Select(id => store.GetLocalChatProgress(visitor, id))
                .Where(item => item is not null).ToArray();
            return Results.Ok(receipts);
        });
        // Require both the unguessable per-message capability and the intended recipient's registered entry.
        string? PrivateRecipient(string id, HttpContext context)
        {
            var peerId = context.Request.Headers["X-ResourceManager-Recipient"].ToString();
            if (Visitor(context) is { } verified && verified != peerId) return null;
            if (Visitor(context) is null && store.GetTrustedDeviceKey(peerId) is not null) return null;
            return store.GetPrivateResource(id, peerId) is not null &&
                store.GetPeerEndpoints(peerId).Any(endpoint => endpoint.Ip == RemoteIp(context) && endpoint.Source != "DeviceChanged")
                ? peerId : null;
        }
        instance.MapGet("/api/v1/chat/resources/{id}", (string id, HttpContext context) =>
        {
            var peerId = PrivateRecipient(id, context);
            var resource = peerId is null ? null : store.GetPrivateResource(id, peerId);
            return resource is null ? Results.NotFound() : Results.Ok(catalog.Describe(resource, context.RequestAborted));
        });
        instance.MapGet("/api/v1/chat/resources/{id}/tree", (string id, HttpContext context) =>
        {
            var peerId = PrivateRecipient(id, context);
            if (peerId is null) return Results.NotFound();
            try { return Results.Ok(catalog.ListFiles(id, peerId, context.RequestAborted)); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        instance.MapGet("/api/v1/chat/resources/{id}/content", (string id, string? path, HttpContext context) =>
        {
            var peerId = PrivateRecipient(id, context);
            if (peerId is null) return Results.NotFound();
            try
            {
                var file = catalog.ResolveFile(id, path, peerId);
                if (!file.Exists) return Results.NotFound();
                var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");
                return Results.File(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), "application/octet-stream", file.Name,
                    file.LastWriteTimeUtc, etag, enableRangeProcessing: true);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        instance.MapPost("/api/v1/router-discovery/snapshot", async (CancellationToken token) =>
            Results.Ok(await routerDiscovery.CreateSnapshotAsync(token).ConfigureAwait(false)));
        instance.MapPost("/api/v1/router-discovery/expose", async (RouterExposeRequest request, CancellationToken token) =>
        {
            try { return Results.Ok(await routerDiscovery.ExposeAsync(request, token).ConfigureAwait(false)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });
        instance.MapPost("/api/v1/router-mapping/ensure", async (CancellationToken token) =>
            Results.Ok(await mappingManager.EnsureMappingAsync(cancellationToken: token).ConfigureAwait(false)));
        if (reminders is not null)
        {
            instance.MapPost("/api/v1/reminders", async (ResourceReminderRequest request, HttpContext context,
                CancellationToken token) =>
            {
                if (Visitor(context) is { } verified ? verified != request.SenderDeviceId : store.GetTrustedDeviceKey(request.SenderDeviceId) is not null)
                    return Results.Json(new ReminderReceipt(false, "发送方身份与握手登记不匹配。", 403), statusCode: 403);
                var receipt = await reminders.ReceiveAsync(request, RemoteIp(context), token).ConfigureAwait(false);
                return Results.Json(receipt, statusCode: receipt.StatusCode);
            });
        }
        if (chat is not null)
        {
            instance.MapPost("/api/v1/chat/messages", async (ChatMessageRequest request, HttpContext context,
                CancellationToken token) =>
            {
                if (Visitor(context) is { } verified ? verified != request.SenderDeviceId : store.GetTrustedDeviceKey(request.SenderDeviceId) is not null)
                    return Results.Json(new ChatReceipt(false, "发送方身份与握手登记不匹配。", 403), statusCode: 403);
                var receipt = await chat.ReceiveAsync(request, RemoteIp(context), token).ConfigureAwait(false);
                return Results.Json(receipt, statusCode: receipt.StatusCode);
            });
        }

        Port = port;
        try
        {
            await instance.StartAsync(cancellationToken);
            app = instance;
        }
        catch
        {
            Port = 0;
            await instance.DisposeAsync();
            throw;
        }
    }

    private static string RemoteIp(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress ?? IPAddress.None;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    public async Task StopAsync()
    {
        if (app is null) return;
        var old = app;
        app = null;
        Port = 0;
        await old.StopAsync();
        await old.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        routerDiscovery.Dispose();
    }
}

public sealed class PeerClient(NodeStore store, bool supportsReminders = false, bool supportsChat = false) : IDisposable, IResourceClient
{
    private readonly HttpClient http = new(new PeerAuthenticationHandler(store, supportsReminders, supportsChat)) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpClient updateHttp = new(new PeerAuthenticationHandler(store, supportsReminders, supportsChat)) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient gitHttp = new(new PeerAuthenticationHandler(store, supportsReminders, supportsChat)) { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Uri Base(string ip, int port)
    {
        if (!IPAddress.TryParse(ip, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("请输入有效的 IPv4 地址。");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new Uri($"http://{address}:{port}/api/v1/");
    }

    private static Uri Route(PeerInfo peer, string route) => new(Base(peer.Ip, peer.Port), route);

    public async Task<PeerInfo> ConnectAsync(string ip, int port, string? expectedDeviceId = null,
        CancellationToken cancellationToken = default, string? gatewayId = null, string source = "direct")
    {
        var settings = store.GetSettings();
        var capabilities = new List<string> { NodeDefaults.RouterDiscoveryCapability, NodeDefaults.UpnpMappingCapability };
        if (supportsReminders) capabilities.Add(NodeDefaults.ReminderCapability);
        if (supportsChat) capabilities.AddRange([NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability, NodeDefaults.ChatProgressCapability]);
        var hello = new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort,
            settings.Profile.Avatar, capabilities.ToArray());
        using var response = await http.PostAsJsonAsync(new Uri(Base(ip, port), "hello"), hello, Json, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var remote = await response.Content.ReadFromJsonAsync<PeerHello>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("对方未返回设备资料。");
        if (remote.DeviceId == hello.DeviceId) throw new InvalidOperationException("不能连接到自己。");
        if (expectedDeviceId is not null && remote.DeviceId != expectedDeviceId)
            throw new InvalidOperationException("新地址对应的不是原设备。");

        // 外部映射场景中，remote.Port 是对方的内网监听端口；连接地址必须保留本次实际拨号端口。
        var peer = new PeerInfo(remote.DeviceId, ip, port, remote.Nickname, remote.Avatar, DateTimeOffset.UtcNow);
        store.UpsertPeer(peer, gatewayId is null ? PeerEndpointKind.Direct : PeerEndpointKind.Gateway,
            gatewayId, source);
        store.SavePeerCapabilities(peer.DeviceId, remote.Capabilities);
        return peer;
    }

    public async Task<PeerHello> ProbeAddressAsync(string ip, int port, CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<PeerHello>(new Uri(Base(ip, port), "health"), Json, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("对方未返回设备资料。");
    }

    public async Task<PeerHello> ProbeAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        var remote = await ProbeAddressAsync(peer.Ip, peer.Port, cancellationToken).ConfigureAwait(false);
        if (remote.DeviceId != peer.DeviceId) throw new InvalidOperationException("此端点现在属于另一台设备。");
        store.UpdatePeerProfile(peer with
        {
            Nickname = remote.Nickname,
            Avatar = remote.Avatar,
            LastSeenUtc = DateTimeOffset.UtcNow
        });
        store.TouchPeerEndpoint(peer.DeviceId, peer.Ip, peer.Port);
        store.SavePeerCapabilities(peer.DeviceId, remote.Capabilities);
        return remote;
    }

    public async Task<RouterSnapshot> CreateRouterSnapshotAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(Route(peer, "router-discovery/snapshot"), null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RouterSnapshot>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("引导设备未返回发现结果。");
    }

    public async Task<IReadOnlyList<RouterMappingResult>> ExposeRouterDevicesAsync(PeerInfo peer,
        RouterExposeRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(Route(peer, "router-discovery/expose"), request, Json,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RouterMappingResult>>(Json, cancellationToken)
                   .ConfigureAwait(false) ?? [];
    }

    public async Task<RouterMappingResult> EnsureRemoteMappingAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(Route(peer, "router-mapping/ensure"), null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RouterMappingResult>(Json, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("目标设备未返回映射结果。");
    }

    public async Task<(PeerHello Hello, IReadOnlyList<RemoteResource> Resources, IReadOnlyList<ResourceGroup> Groups)> GetCatalogAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        var hello = await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        return await GetCatalogAfterProbeAsync(peer, hello, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(PeerHello Hello, IReadOnlyList<RemoteResource> Resources, IReadOnlyList<ResourceGroup> Groups)> GetCatalogAfterProbeAsync(
        PeerInfo peer, PeerHello hello, CancellationToken cancellationToken = default)
    {
        if ((hello.Capabilities ?? []).Contains("resource-groups-v1", StringComparer.Ordinal))
        {
            var tree = await http.GetFromJsonAsync<ResourceTreeCatalog>(Route(peer, "resource-catalog"), Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("资源目录响应为空。");
            return (hello, tree.Resources, tree.Groups);
        }
        var resources = await http.GetFromJsonAsync<List<RemoteResource>>(Route(peer, "resources"), Json, cancellationToken)
                   .ConfigureAwait(false) ?? [];
        return (hello, resources, [new ResourceGroup(NodeStore.DefaultResourceGroupId, "默认组", null, 0, DateTimeOffset.UnixEpoch)]);
    }

    public async Task<IReadOnlyList<RemoteResource>> GetResourcesAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        return (await GetCatalogAsync(peer, cancellationToken).ConfigureAwait(false)).Resources;
    }

    public async Task<RemoteResource?> GetResourceAsync(PeerInfo peer, string resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!resourceId.StartsWith("private-", StringComparison.Ordinal))
        {
            var hello = await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
            if (!(hello.Capabilities ?? []).Contains("resource-access-v1"))
                return (await GetResourcesAsync(peer, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Id == resourceId);
            using var publicRequest = ResourceRequest(peer, resourceId, "");
            using var publicResponse = await http.SendAsync(publicRequest, cancellationToken).ConfigureAwait(false);
            if (publicResponse.StatusCode == HttpStatusCode.NotFound) return null;
            if (publicResponse.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("无权访问此资源。");
            publicResponse.EnsureSuccessStatusCode();
            return await publicResponse.Content.ReadFromJsonAsync<RemoteResource>(Json, cancellationToken).ConfigureAwait(false);
        }
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        using var request = ResourceRequest(peer, resourceId, "");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RemoteResource>(Json, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage ResourceRequest(PeerInfo peer, string resourceId, string suffix)
    {
        var privateResource = resourceId.StartsWith("private-", StringComparison.Ordinal);
        var request = new HttpRequestMessage(HttpMethod.Get, Route(peer,
            $"{(privateResource ? "chat/" : "")}resources/{Uri.EscapeDataString(resourceId)}{suffix}"));
        if (privateResource) request.Headers.Add("X-ResourceManager-Recipient", store.GetSettings().Profile.DeviceId);
        return request;
    }

    public async Task<ReminderReceipt> SendReminderAsync(PeerInfo peer, ResourceReminderRequest reminder,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(Route(peer, "reminders"), reminder, Json, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("对方版本不支持定向提醒。");
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or
            HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestEntityTooLarge)
        {
            string? reason = null;
            try
            {
                reason = (await response.Content.ReadFromJsonAsync<ReminderReceipt>(Json, cancellationToken)
                    .ConfigureAwait(false))?.Reason;
            }
            catch (Exception) { }
            throw new InvalidOperationException(reason ?? response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "对方未登记本机设备，请先建立连接。",
                HttpStatusCode.TooManyRequests => "发送过于频繁，请稍后再试。",
                HttpStatusCode.RequestEntityTooLarge => "提醒内容过大。",
                _ => "对方拒绝了这条提醒。"
            });
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReminderReceipt>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("对方未返回提醒结果。");
    }

    public async Task<ChatProgressReceipt[]> GetChatProgressAsync(PeerInfo peer, string[] messageIds,
        CancellationToken cancellationToken = default)
    {
        if (!store.GetPeerCapabilities(peer.DeviceId).Contains(NodeDefaults.ChatProgressCapability)) return [];
        using var response = await http.PostAsJsonAsync(Route(peer, "chat/progress"), new ChatProgressQuery(messageIds), Json, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChatProgressReceipt[]>(Json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<ChatReceipt> SendChatAsync(PeerInfo peer, ChatMessageRequest message,
        CancellationToken cancellationToken = default)
    {
        var hello = await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        if (!(hello.Capabilities ?? []).Contains(NodeDefaults.ChatCapability, StringComparer.Ordinal))
            return new ChatReceipt(false, "对方版本不支持聊天。", 404);
        if (message.Kind == "PrivateResource" && !(hello.Capabilities ?? []).Contains(NodeDefaults.PrivateResourceCapability, StringComparer.Ordinal))
            return new ChatReceipt(false, "对方版本不支持私发资源，请先升级到 0.4.4 或更新版本。", 409);
        using var response = await http.PostAsJsonAsync(Route(peer, "chat/messages"), message, Json,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ChatReceipt(false, "对方版本不支持聊天。", 404);
        try
        {
            var receipt = await response.Content.ReadFromJsonAsync<ChatReceipt>(Json, cancellationToken).ConfigureAwait(false);
            return receipt ?? new ChatReceipt(false, "对方未返回送达确认。", (int)response.StatusCode);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            return new ChatReceipt(false, response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "对方未登记本机设备或来源入口不匹配。",
                HttpStatusCode.RequestEntityTooLarge => "消息超过 16 KiB 请求上限。",
                _ => $"对方返回 HTTP {(int)response.StatusCode}。"
            }, (int)response.StatusCode);
        }
    }

    public async Task<SharedUpdatePackage?> GetSharedUpdateAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        using var response = await updateHttp.GetAsync(Route(peer, "updates/latest"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SharedUpdatePackage>(Json, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("对方未返回本地更新源资料。");
    }

    public async Task<GitSyncResponse> SyncGitAsync(PeerInfo peer, IReadOnlyList<GitProjectEvent> events,
        IReadOnlyList<GitLineVersion> known,
        CancellationToken cancellationToken = default)
    {
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        using var response = await gitHttp.PostAsJsonAsync(Route(peer, "git/sync"),
            new GitSyncRequest(events.ToArray(), known.ToArray()), Json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitSyncResponse>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("对方未返回 Git 协作记录。");
    }

    public async Task<string> DownloadGitBundleAsync(PeerInfo peer, GitBundleInfo bundle,
        CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(bundle.Hash, "^[0-9a-f]{64}$") ||
            bundle.Size is <= 0 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("协作包资料无效。");
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(bundle.Path)!);
        var temporary = bundle.Path + ".download";
        var offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
        if (offset > bundle.Size) { File.Delete(temporary); offset = 0; }
        if (offset == bundle.Size && await GitBundleFile.VerifyAsync(temporary, bundle.Hash,
                bundle.Size, cancellationToken).ConfigureAwait(false))
        {
            File.Move(temporary, bundle.Path, true);
            return bundle.Path;
        }
        if (offset == bundle.Size) { File.Delete(temporary); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get,
            Route(peer, $"git/bundles/{Uri.EscapeDataString(bundle.Hash)}"));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await gitHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
        if (offset > 0 && response.Content.Headers.ContentRange?.From != offset)
            throw new InvalidDataException("协作包续传的起点与请求不一致。");
        StorageLocation.EnsureSpace(Path.GetDirectoryName(bundle.Path)!, bundle.Size - offset);
        await using (var output = new FileStream(temporary, offset == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            var total = offset;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > bundle.Size) throw new InvalidDataException("协作包超过预期大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        if (new FileInfo(temporary).Length != bundle.Size) throw new InvalidDataException("协作包下载不完整。");
        if (!await GitBundleFile.VerifyAsync(temporary, bundle.Hash, bundle.Size,
                cancellationToken).ConfigureAwait(false))
        {
            File.Delete(temporary);
            throw new InvalidDataException("协作包 SHA-256 校验失败。");
        }
        File.Move(temporary, bundle.Path, true);
        return bundle.Path;
    }

    public async Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string resourceId,
        CancellationToken cancellationToken = default)
    {
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        using var request = ResourceRequest(peer, resourceId, "/tree");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RemoteFile>>(Json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string resourceId, string relativePath,
        long offset, string? etag, CancellationToken cancellationToken = default)
    {
        var request = ResourceRequest(peer, resourceId, $"/content?path={Uri.EscapeDataString(relativePath)}");
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose()
    {
        http.Dispose();
        updateHttp.Dispose();
        gitHttp.Dispose();
    }
}
