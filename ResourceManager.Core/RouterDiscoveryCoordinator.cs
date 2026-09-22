using System.Collections.Concurrent;

namespace ResourceManager.Core;

public sealed class RouterDiscoveryCoordinator : IDisposable
{
    private readonly NodeStore store;
    private readonly LanDiscoveryService discovery;
    private readonly UpnpPortMappingManager mappingManager;
    private readonly PeerClient client;
    private readonly ConcurrentDictionary<string, SnapshotState> snapshots = new(StringComparer.Ordinal);

    public RouterDiscoveryCoordinator(NodeStore store, LanDiscoveryService discovery, UpnpPortMappingManager mappingManager)
    {
        this.store = store;
        this.discovery = discovery;
        this.mappingManager = mappingManager;
        client = new PeerClient(store);
    }

    public async Task<RouterSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken = default)
    {
        RemoveExpiredSnapshots();
        RouterNetworkInfo? router = null;
        try { router = await mappingManager.GetCurrentRouterAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        var settings = store.GetSettings();
        var devices = new Dictionary<string, RouterDeviceInfo>(StringComparer.Ordinal)
        {
            [settings.Profile.DeviceId] = new RouterDeviceInfo(settings.Profile.DeviceId, settings.Profile.Nickname, true,
                MappingFor(router?.RouterUdn))
        };
        var peers = new Dictionary<string, PeerInfo?>(StringComparer.Ordinal)
        {
            [settings.Profile.DeviceId] = null
        };
        var found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        var attempts = found.Select(async item =>
        {
            try
            {
                var peer = await client.ConnectAsync(item.Ip, item.Port, item.DeviceId, cancellationToken).ConfigureAwait(false);
                var health = await client.ProbeAsync(peer, cancellationToken).ConfigureAwait(false);
                return (Peer: peer, Health: health);
            }
            catch { return (Peer: (PeerInfo?)null, Health: (PeerHello?)null); }
        });
        foreach (var result in await Task.WhenAll(attempts).ConfigureAwait(false))
        {
            if (result.Peer is null || result.Health is null) continue;
            var supports = result.Health.Capabilities?.Contains(NodeDefaults.RouterDiscoveryCapability, StringComparer.Ordinal) == true;
            devices[result.Peer.DeviceId] = new RouterDeviceInfo(result.Peer.DeviceId, result.Peer.Nickname, supports);
            peers[result.Peer.DeviceId] = result.Peer;
        }
        var token = Guid.NewGuid().ToString("N");
        snapshots[token] = new SnapshotState(DateTimeOffset.UtcNow.AddMinutes(2), peers);
        return new RouterSnapshot(token, router,
            devices.Values.OrderBy(item => item.Nickname, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public async Task<IReadOnlyList<RouterMappingResult>> ExposeAsync(RouterExposeRequest request,
        CancellationToken cancellationToken = default)
    {
        RemoveExpiredSnapshots();
        if (!snapshots.TryGetValue(request.SnapshotToken, out var snapshot) || snapshot.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("设备发现快照已经过期，请重新刷新。");
        var selfId = store.GetSettings().Profile.DeviceId;
        var ids = request.DeviceIds.Distinct(StringComparer.Ordinal).Where(snapshot.Peers.ContainsKey).ToArray();
        var results = new List<RouterMappingResult>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == selfId)
            {
                results.Add(await mappingManager.EnsureMappingAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
                continue;
            }
            if (snapshot.Peers[id] is not { } peer) continue;
            try { results.Add(await client.EnsureRemoteMappingAsync(peer, cancellationToken).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new RouterMappingResult(id, false, null, null, ex.Message));
            }
        }
        return results;
    }

    private int? MappingFor(string? routerUdn) => routerUdn is null ? null :
        store.GetLocalPortMappings().FirstOrDefault(item => item.RouterUdn == routerUdn)?.ExternalPort;

    private void RemoveExpiredSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots.Where(item => item.Value.ExpiresUtc <= now).ToArray())
            snapshots.TryRemove(snapshot.Key, out _);
    }

    public void Dispose() => client.Dispose();

    private sealed record SnapshotState(DateTimeOffset ExpiresUtc, Dictionary<string, PeerInfo?> Peers);
}
