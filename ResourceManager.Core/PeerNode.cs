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
    private WebApplication? app;

    public PeerNode(NodeStore store)
        : this(store, new LanDiscoveryService(store),
            new UpnpPortMappingManager(store, new UpnpGatewayClient()))
    {
    }

    public PeerNode(NodeStore store, LanDiscoveryService discovery, UpnpPortMappingManager mappingManager,
        ReminderService? reminders = null)
    {
        this.store = store;
        this.mappingManager = mappingManager;
        this.reminders = reminders;
        catalog = new ResourceCatalog(store);
        routerDiscovery = new RouterDiscoveryCoordinator(store, discovery, mappingManager);
    }

    public bool IsRunning => app is not null;
    public int Port { get; private set; }

    private PeerHello Self()
    {
        var settings = store.GetSettings();
        return new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort, settings.Profile.Avatar,
            [NodeDefaults.RouterDiscoveryCapability, NodeDefaults.UpnpMappingCapability, NodeDefaults.ReminderCapability]);
    }

    public async Task StartAsync(int port, string listenAddress = "0.0.0.0", CancellationToken cancellationToken = default)
    {
        if (app is not null) throw new InvalidOperationException("节点已经启动。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls($"http://{listenAddress}:{port}");
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        var instance = builder.Build();
        instance.Use(async (context, next) =>
        {
            if (context.Request.Path.Value is not ("/api/v1/health" or "/api/v1/hello"))
            {
                var ip = RemoteIp(context);
                if (!store.IsKnownIp(ip))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            if (context.Request.Path.Value == "/api/v1/reminders")
            {
                var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = ReminderService.MaxBodyBytes;
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
            store.UpsertPeer(new PeerInfo(hello.DeviceId, ip, hello.Port, hello.Nickname.Trim(), hello.Avatar,
                DateTimeOffset.UtcNow));
            return Results.Ok(Self());
        });
        instance.MapGet("/api/v1/resources", () => Results.Ok(catalog.List()));
        instance.MapGet("/api/v1/updates/latest", async (CancellationToken token) =>
        {
            var update = await catalog.FindLatestUpdateAsync(token).ConfigureAwait(false);
            return update is null ? Results.NotFound() : Results.Ok(update);
        });
        instance.MapGet("/api/v1/resources/{id}/tree", (string id) =>
        {
            try { return Results.Ok(catalog.ListFiles(id)); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        instance.MapGet("/api/v1/resources/{id}/content", (string id, string? path) =>
        {
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
                var receipt = await reminders.ReceiveAsync(request, RemoteIp(context), token).ConfigureAwait(false);
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

public sealed class PeerClient(NodeStore store) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpClient updateHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
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
        var hello = new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort,
            settings.Profile.Avatar,
            [NodeDefaults.RouterDiscoveryCapability, NodeDefaults.UpnpMappingCapability, NodeDefaults.ReminderCapability]);
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

    public async Task<(PeerHello Hello, IReadOnlyList<RemoteResource> Resources)> GetCatalogAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        var hello = await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        var resources = await http.GetFromJsonAsync<List<RemoteResource>>(Route(peer, "resources"), Json, cancellationToken)
                   .ConfigureAwait(false) ?? [];
        return (hello, resources);
    }

    public async Task<IReadOnlyList<RemoteResource>> GetResourcesAsync(PeerInfo peer,
        CancellationToken cancellationToken = default)
    {
        return (await GetCatalogAsync(peer, cancellationToken).ConfigureAwait(false)).Resources;
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

    public async Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string resourceId,
        CancellationToken cancellationToken = default)
    {
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        return await http.GetFromJsonAsync<List<RemoteFile>>(
                   Route(peer, $"resources/{Uri.EscapeDataString(resourceId)}/tree"), Json, cancellationToken)
                   .ConfigureAwait(false) ?? [];
    }

    public Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string resourceId, string relativePath,
        long offset, string? etag, CancellationToken cancellationToken = default)
    {
        var uri = Route(peer,
            $"resources/{Uri.EscapeDataString(resourceId)}/content?path={Uri.EscapeDataString(relativePath)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
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
    }
}
