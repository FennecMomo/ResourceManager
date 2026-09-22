using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceManager.Core;

public sealed class PeerNode(NodeStore store) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly ResourceCatalog catalog = new(store);
    public bool IsRunning => app is not null;
    public int Port { get; private set; }

    private PeerHello Self()
    {
        var settings = store.GetSettings();
        return new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, Port, settings.Profile.Avatar);
    }

    public async Task StartAsync(int port, string listenAddress = "0.0.0.0", CancellationToken cancellationToken = default)
    {
        if (app is not null) throw new InvalidOperationException("节点已经启动。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls($"http://{listenAddress}:{port}");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        var instance = builder.Build();
        instance.Use(async (context, next) =>
        {
            if (context.Request.Path.Value is not ("/api/v1/health" or "/api/v1/hello"))
            {
                var ip = RemoteIp(context);
                if (!store.IsKnownIp(ip)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
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
            if (store.GetPeers().Any(p => p.Ip == ip && p.DeviceId != hello.DeviceId))
                return Results.Conflict("该 IP 已属于另一台设备，请先处理旧设备记录。");
            store.UpsertPeer(new PeerInfo(hello.DeviceId, ip, hello.Port, hello.Nickname.Trim(), hello.Avatar, DateTimeOffset.UtcNow));
            return Results.Ok(Self());
        });
        instance.MapGet("/api/v1/resources", () => Results.Ok(catalog.List()));
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
                var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");
                return Results.File(File.OpenRead(file.FullName), "application/octet-stream", file.Name,
                    file.LastWriteTimeUtc, etag, enableRangeProcessing: true);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        Port = port;
        try { await instance.StartAsync(cancellationToken); app = instance; }
        catch { Port = 0; await instance.DisposeAsync(); throw; }
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

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class PeerClient(NodeStore store) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    private static Uri Base(string ip, int port)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("请输入有效的 IPv4 地址。");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new Uri($"http://{address}:{port}/api/v1/");
    }

    private static Uri Route(PeerInfo peer, string route) => new(Base(peer.Ip, peer.Port), route);

    public async Task<PeerInfo> ConnectAsync(string ip, int port, string? expectedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var settings = store.GetSettings();
        var hello = new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort, settings.Profile.Avatar);
        using var response = await http.PostAsJsonAsync(new Uri(Base(ip, port), "hello"), hello, json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var remote = await response.Content.ReadFromJsonAsync<PeerHello>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("对方未返回设备资料。");
        if (remote.DeviceId == hello.DeviceId) throw new InvalidOperationException("不能连接到自己。");
        if (expectedDeviceId is not null && remote.DeviceId != expectedDeviceId) throw new InvalidOperationException("新地址对应的不是原设备。");
        if (store.GetPeers().Any(p => p.Ip == ip && p.DeviceId != remote.DeviceId))
            throw new InvalidOperationException("该 IP 对应另一台已记录的设备。");
        var peer = new PeerInfo(remote.DeviceId, ip, remote.Port, remote.Nickname, remote.Avatar, DateTimeOffset.UtcNow);
        store.UpsertPeer(peer);
        return peer;
    }

    public async Task<PeerHello> ProbeAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        var remote = await http.GetFromJsonAsync<PeerHello>(Route(peer, "health"), json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("对方未返回设备资料。");
        if (remote.DeviceId != peer.DeviceId) throw new InvalidOperationException("此 IP 现在属于另一台设备。");
        store.UpsertPeer(peer with { Nickname = remote.Nickname, Avatar = remote.Avatar, Port = remote.Port, LastSeenUtc = DateTimeOffset.UtcNow });
        return remote;
    }

    public async Task<IReadOnlyList<RemoteResource>> GetResourcesAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        return await http.GetFromJsonAsync<List<RemoteResource>>(Route(peer, "resources"), json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string resourceId, CancellationToken cancellationToken = default)
    {
        await ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
        return await http.GetFromJsonAsync<List<RemoteFile>>(Route(peer, $"resources/{Uri.EscapeDataString(resourceId)}/tree"), json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string resourceId, string relativePath, long offset, string? etag, CancellationToken cancellationToken = default)
    {
        var uri = Route(peer, $"resources/{Uri.EscapeDataString(resourceId)}/content?path={Uri.EscapeDataString(relativePath)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose() => http.Dispose();
}
