using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    // Exercise the real refresh path against a loopback-only peer; no discovery or visible windows.
    private static async Task CheckDeviceRefreshAsync(MainWindow window, NodeStore store)
    {
        var remote = new NodeStore(Path.Combine(Output, "remote-" + Guid.NewGuid().ToString("N")));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        remote.SaveSettings("刷新检查设备", null, port, false);
        var file = Path.Combine(remote.DataDirectory, "sample.txt");
        File.WriteAllText(file, "isolated resource");
        var resource = remote.AddResource(file, PublishMode.Reference);
        using var remoteClient = new PeerClient(remote);
        using var upnp = new UpnpGatewayClient();
        await using var discovery = new LanDiscoveryService(remote);
        await using var node = new PeerNode(remote, discovery, new UpnpPortMappingManager(remote, upnp),
            chat: new ChatService(remote, remoteClient));
        await node.StartAsync(port, "127.0.0.1");
        using var client = new PeerClient(store);
        var peer = await client.ConnectAsync("127.0.0.1", port);
        store.SavePeerNote(peer.DeviceId, "保留备注");
        store.SaveFavorite(new Favorite(peer.DeviceId, resource.Id, resource.Name, resource.Kind));
        var group = store.SaveResourceGroup("保留设备权限");
        store.SetGroupPermission(group.Id, GroupAccess.AllowList, [peer.DeviceId]);
        var message = new ChatService(store, client).QueueText(peer.DeviceId, "离线仍保留");
        Task Refresh() => (Task)typeof(MainWindow).GetMethod("RefreshAllAsync", Private)!
            .Invoke(window, [null, false])!;
        void RefreshView() => typeof(MainWindow).GetMethod("RefreshPeersView", Private)!.Invoke(window, null);
        var grid = (ListBox)window.FindName("PeersGrid");
        RefreshView();
        Require(window.Peers.Single(row => row.Peer.DeviceId == peer.DeviceId).Status == "未检查",
            "saved peer is visible before startup refresh checks connectivity");

        using var stalled = new TcpListener(IPAddress.Loopback, 0);
        stalled.Start();
        var slowPort = ((IPEndPoint)stalled.LocalEndpoint).Port;
        var slowId = Guid.NewGuid().ToString();
        store.UpsertPeer(new PeerInfo(slowId, "127.0.0.1", slowPort, "AAA 慢入口", null, null));
        var stalledRequest = Task.Run(async () =>
        {
            using var socket = await stalled.AcceptTcpClientAsync();
            await Task.Delay(TimeSpan.FromSeconds(4));
        });
        var progressiveRefresh = Refresh();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var appearedEarly = !progressiveRefresh.IsCompleted && window.Peers.Any(row => row.Peer.DeviceId == peer.DeviceId);
        await progressiveRefresh;
        Require(appearedEarly, "healthy peer appears before a stalled endpoint times out");
        await stalledRequest;
        store.RemovePeer(slowId);
        grid.SelectedItem = window.Peers.Single(row => row.Peer.DeviceId == peer.DeviceId);
        Require(window.RemoteResources.Any(row => row.Resource.Id == resource.Id), "online peer and resources appear after refresh");
        // The LAN lane must not sit behind twenty stalled saved endpoints.
        var stalls = new List<TcpListener>();
        var ids = new List<string>();
        for (var index = 0; index < 20; index++) {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); stalls.Add(listener);
            var id = "slow-lane-" + index; ids.Add(id);
            store.UpsertPeer(new PeerInfo(id, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "历史慢设备" + index, null, null));
        }
        var seam = typeof(MainWindow).GetField("isolatedDeviceDiscovery", Private)!;
        seam.SetValue(window, (Func<CancellationToken, Action<DiscoveredPeer>, Task>)(async (token, callback) => {
            await Task.Delay(30, token);
            callback(new DiscoveredPeer(peer.DeviceId, "127.0.0.1", port, "快速局域网设备"));
            callback(new DiscoveredPeer(peer.DeviceId, "127.0.0.1", port, "快速局域网设备"));
        }));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try {
            var refresh = (Task)typeof(MainWindow).GetMethod("RefreshDevicesAsync", Private)!.Invoke(window, [false])!;
            await Task.Delay(1000);
            Require(window.Peers.Any(row => row.Peer.DeviceId == peer.DeviceId && row.Status == "在线"),
                "LAN healthy peer confirms within one second with twenty stalled historical endpoints");
            Require(!refresh.IsCompleted, "history probes continue independently of the confirmed LAN result");
            await refresh;
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(9), "foreground discovery completes within its eight-second budget");
            Console.WriteLine($"Discovery performance: twenty stalls, foreground {elapsed.ElapsedMilliseconds} ms; healthy peer <= 1000 ms");
        } finally {
            seam.SetValue(window, null);
            stalls.ForEach(listener => listener.Stop()); ids.ForEach(id => store.RemovePeer(id));
        }
        await (Task)typeof(MainWindow).GetField("deviceDetailsRefresh", Private)!.GetValue(window)!;
        await node.StopAsync();
        await Refresh();
        Require(window.Peers.Single(row => row.Peer.DeviceId == peer.DeviceId).Status == "离线",
            "refresh keeps disconnected device visible as offline");
        Require((grid.SelectedItem as PeerRow)?.Peer.DeviceId == peer.DeviceId &&
            window.RemoteResources.Count == 0 && window.RemoteResourceTree.Count == 0,
            "offline selection remains while unavailable resources clear");
        Require(store.GetPeer(peer.DeviceId) is not null && store.GetPeerNote(peer.DeviceId) == "保留备注" &&
            store.GetFavorites().Any(f => f.PeerId == peer.DeviceId) && store.CanAccessGroup(group.Id, peer.DeviceId),
            "offline device retains endpoints, notes, favorites and permissions");
        Require(store.GetChatMessages(peer.DeviceId).Single().MessageId == message.MessageId &&
            store.GetChatMessages(peer.DeviceId).Single().State == "Queued" &&
            window.ChatConversations.Any(row => row.PeerId == peer.DeviceId),
            "offline device retains conversation and queued message");

        await node.StartAsync(port, "127.0.0.1");
        await Refresh();
        Require(window.Peers.Single(row => row.Peer.DeviceId == peer.DeviceId).Note == "保留备注",
            "returning peer reappears with its saved note");
        await node.StopAsync();
        using var alternateReservation = new TcpListener(IPAddress.Loopback, 0);
        alternateReservation.Start();
        var alternatePort = ((IPEndPoint)alternateReservation.LocalEndpoint).Port;
        alternateReservation.Stop();
        store.UpsertPeerEndpoint(new PeerEndpoint(peer.DeviceId, "127.0.0.1", alternatePort,
            PeerEndpointKind.Direct, null, "Manual", null));
        var replacement = new NodeStore(Path.Combine(Output, "replacement-" + Guid.NewGuid().ToString("N")));
        replacement.SaveSettings("另一个设备", null, port, false);
        await using var otherNode = new PeerNode(replacement);
        await otherNode.StartAsync(port, "127.0.0.1");
        await Refresh();
        var remaining = window.Peers.Single(row => row.Peer.DeviceId == peer.DeviceId);
        Require(remaining.Status == "离线" && remaining.Peer.Port == alternatePort,
            "changed address retains offline peer while another valid endpoint remains");
        store.MarkEndpointDeviceChanged(store.GetPeerEndpoints(peer.DeviceId)
            .Single(item => item.Port == alternatePort));
        RefreshView();
        Require(window.Peers.All(row => row.Peer.DeviceId != peer.DeviceId), "changed endpoint does not keep stale device row");
        Require(store.GetPeer(peer.DeviceId) is not null, "changed endpoint preserves original device history");
    }
}
