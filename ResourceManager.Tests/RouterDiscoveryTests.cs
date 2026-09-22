using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class RouterDiscoveryTests
{
    [Fact]
    public void SameWanIp_CanStoreSeveralDevicesAndPorts()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.Root);
        var gateway = store.SaveGateway("办公室路由器", "192.168.2.20");
        store.UpsertPeer(new PeerInfo("device-c", gateway.WanIp, 48001, "C", null, DateTimeOffset.UtcNow),
            PeerEndpointKind.Gateway, gateway.Id, "Manual");
        store.UpsertPeer(new PeerInfo("device-d", gateway.WanIp, 48002, "D", null, DateTimeOffset.UtcNow),
            PeerEndpointKind.Gateway, gateway.Id, "Upnp");
        store.UpsertPeer(new PeerInfo("device-e", gateway.WanIp, 48003, "E", null, DateTimeOffset.UtcNow),
            PeerEndpointKind.Gateway, gateway.Id, "Upnp");

        Assert.Equal(3, store.GetPeers().Count);
        Assert.Equal([48001, 48002, 48003], store.GetPeerEndpoints(gatewayId: gateway.Id)
            .Select(item => item.Port).Order().ToArray());

        var reopened = new NodeStore(space.Root);
        Assert.Equal(3, reopened.GetPeers().Count);
        Assert.Equal(3, reopened.GetPeerEndpoints(gatewayId: gateway.Id).Count);
        reopened.RemoveGateway(gateway.Id);
        Assert.Equal(3, reopened.GetPeers().Count);
        Assert.Empty(reopened.GetPeerEndpoints(gatewayId: gateway.Id));
    }

    [Fact]
    public void OpeningLegacyDatabase_PreservesDataAndCreatesDirectEndpoint()
    {
        using var space = new TestSpace();
        Directory.CreateDirectory(space.Root);
        var database = Path.Combine(space.Root, "resources.db");
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE peers (device_id TEXT PRIMARY KEY, ip TEXT NOT NULL, port INTEGER NOT NULL, nickname TEXT NOT NULL, avatar BLOB, last_seen TEXT);
                CREATE TABLE resources (id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, mode TEXT NOT NULL, source_path TEXT NOT NULL, published_utc TEXT NOT NULL);
                CREATE TABLE favorites (peer_id TEXT NOT NULL, resource_id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, PRIMARY KEY(peer_id, resource_id));
                CREATE TABLE downloads (id TEXT PRIMARY KEY, peer_id TEXT NOT NULL, resource_id TEXT NOT NULL, resource_name TEXT NOT NULL, kind TEXT NOT NULL, target_path TEXT NOT NULL, status TEXT NOT NULL, downloaded_bytes INTEGER NOT NULL, total_bytes INTEGER NOT NULL, error TEXT);
                INSERT INTO peers VALUES('legacy-peer','10.0.0.8',37642,'旧设备',NULL,'2026-09-20T00:00:00+00:00');
                INSERT INTO resources VALUES('resource','资料','File','Reference','C:\\old.txt','2026-09-20T00:00:00+00:00');
                INSERT INTO favorites VALUES('legacy-peer','resource','资料','File');
                INSERT INTO downloads VALUES('download','legacy-peer','resource','资料','File','D:\\资料.txt','已暂停',10,20,NULL);
                """;
            command.ExecuteNonQuery();
        }

        var store = new NodeStore(space.Root);

        Assert.Equal("旧设备", Assert.Single(store.GetPeers()).Nickname);
        var endpoint = Assert.Single(store.GetPeerEndpoints("legacy-peer"));
        Assert.Equal(PeerEndpointKind.Direct, endpoint.Kind);
        Assert.Single(store.GetResources());
        Assert.Single(store.GetFavorites());
        Assert.Single(store.GetDownloads());
    }

    [Fact]
    public async Task GatewayConnection_KeepsDialedExternalPort()
    {
        using var space = new TestSpace();
        var receiver = new NodeStore(space.PathFor("receiver"));
        var publisher = new NodeStore(space.PathFor("publisher"));
        var dialedPort = FreePort();
        publisher.SaveSettings("内网设备", null, NodeDefaults.Port, true);
        await using var isolatedDiscovery = new LanDiscoveryService(publisher, FreeUdpPort());
        using var noUpnp = new NoUpnpGatewayClient();
        await using var node = new PeerNode(publisher, isolatedDiscovery,
            new UpnpPortMappingManager(publisher, noUpnp));
        await node.StartAsync(dialedPort, "127.0.0.1");
        var gateway = receiver.SaveGateway("测试入口", "127.0.0.1");
        using var client = new PeerClient(receiver);

        var peer = await client.ConnectAsync("127.0.0.1", dialedPort, gatewayId: gateway.Id,
            source: "GatewayScan");
        var health = await client.ProbeAsync(peer);

        Assert.Equal(NodeDefaults.Port, health.Port);
        Assert.Equal(dialedPort, peer.Port);
        Assert.Equal(dialedPort, receiver.GetPeer(peer.DeviceId)!.Port);
        var endpoint = Assert.Single(receiver.GetPeerEndpoints(peer.DeviceId, gateway.Id));
        Assert.Equal(dialedPort, endpoint.Port);
        Assert.Equal(PeerEndpointKind.Gateway, endpoint.Kind);
    }

    [Fact]
    public async Task GatewayRefresh_UsesCachedCapableEndpoint()
    {
        using var space = new TestSpace();
        var scanner = new NodeStore(space.PathFor("scanner"));
        var publisher = new NodeStore(space.PathFor("publisher"));
        var mappedPort = FreeGatewayPort();
        publisher.SaveSettings("引导设备", null, NodeDefaults.Port, true);
        await using var isolatedDiscovery = new LanDiscoveryService(publisher, FreeUdpPort());
        using var noUpnp = new NoUpnpGatewayClient();
        await using var node = new PeerNode(publisher, isolatedDiscovery,
            new UpnpPortMappingManager(publisher, noUpnp));
        await node.StartAsync(mappedPort, "127.0.0.1");
        var unlisted = new NodeStore(space.PathFor("unlisted"));
        var unlistedPort = FreeGatewayPort();
        unlisted.SaveSettings("未列出的设备", null, NodeDefaults.Port, true);
        await using var unlistedNode = new PeerNode(unlisted);
        await unlistedNode.StartAsync(unlistedPort, "127.0.0.1");
        var gateway = scanner.SaveGateway("测试入口", "127.0.0.1");
        scanner.UpsertPeer(new PeerInfo(publisher.GetSettings().Profile.DeviceId, gateway.WanIp, mappedPort,
                "引导设备", null, DateTimeOffset.UtcNow), PeerEndpointKind.Gateway, gateway.Id, "Manual");
        using var client = new PeerClient(scanner);
        var service = new GatewayDiscoveryService(scanner, client);

        var result = await service.RefreshAsync(gateway);

        Assert.Equal(1, result.Connected);
        Assert.Contains("已连接 1 台", result.Status);
        Assert.Equal(mappedPort, scanner.GetPeer(publisher.GetSettings().Profile.DeviceId)!.Port);
        Assert.Null(scanner.GetPeer(unlisted.GetSettings().Profile.DeviceId));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeGatewayPort()
    {
        for (var port = NodeDefaults.GatewayPortStart; port <= NodeDefaults.GatewayPortEnd; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("测试端口范围已被占用。");
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private sealed class NoUpnpGatewayClient : UpnpGatewayClient
    {
        public override Task<UpnpGatewayDevice?> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UpnpGatewayDevice?>(null);
    }

    private sealed class TestSpace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerRouterTests",
            Guid.NewGuid().ToString("N"));
        public string PathFor(params string[] names) => Path.Combine([Root, .. names]);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
