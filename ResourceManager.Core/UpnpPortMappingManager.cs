using System.Security.Cryptography;
using System.Text;

namespace ResourceManager.Core;

public sealed class UpnpPortMappingManager(NodeStore store, UpnpGatewayClient gatewayClient)
{
    private readonly SemaphoreSlim mappingGate = new(1, 1);
    private readonly object inspectionGate = new();
    private readonly Dictionary<string, DateTimeOffset> nextInspection = new(StringComparer.Ordinal);

    public async Task<RouterNetworkInfo?> GetCurrentRouterAsync(CancellationToken cancellationToken = default)
    {
        var gateway = await gatewayClient.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (gateway is null) return null;
        var network = await gateway.GetNetworkInfoAsync(cancellationToken).ConfigureAwait(false);
        if (store.GetLocalPortMappings().All(item => item.RouterUdn != network.RouterUdn) && ShouldInspect(network.RouterUdn))
            await DetectExistingMappingAsync(gateway, network, cancellationToken).ConfigureAwait(false);
        return network;
    }

    private bool ShouldInspect(string routerUdn)
    {
        lock (inspectionGate)
        {
            if (nextInspection.GetValueOrDefault(routerUdn) > DateTimeOffset.UtcNow) return false;
            nextInspection[routerUdn] = DateTimeOffset.UtcNow.AddMinutes(5);
            return true;
        }
    }

    private async Task DetectExistingMappingAsync(UpnpGatewayDevice gateway, RouterNetworkInfo network,
        CancellationToken cancellationToken)
    {
        var settings = store.GetSettings();
        using var concurrency = new SemaphoreSlim(8, 8);
        var tasks = Enumerable.Range(NodeDefaults.GatewayPortStart,
                NodeDefaults.GatewayPortEnd - NodeDefaults.GatewayPortStart + 1)
            .Select(async port =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var mapping = await gateway.GetMappingAsync(port, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return mapping is { Enabled: true } && mapping.InternalClient == network.LanIp &&
                           mapping.InternalPort == settings.ListenPort
                        ? mapping
                        : null;
                }
                catch (UpnpException) { return null; }
                finally { concurrency.Release(); }
            }).ToArray();
        var existing = (await Task.WhenAll(tasks).ConfigureAwait(false)).FirstOrDefault(item => item is not null);
        if (existing is null) return;
        store.SaveLocalPortMapping(new LocalPortMapping(network.RouterUdn, network.RouterName, network.LanIp,
            network.WanIp, existing.ExternalPort, settings.ListenPort, existing.LeaseSeconds, DateTimeOffset.UtcNow));
    }

    public async Task<RouterMappingResult> EnsureMappingAsync(int? preferredPort = null,
        CancellationToken cancellationToken = default)
    {
        await mappingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = store.GetSettings().Profile;
            var gateway = await gatewayClient.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (gateway is null)
                return new RouterMappingResult(profile.DeviceId, false, null, null, "没有发现支持 UPnP IGD 的路由器。");
            var network = await gateway.GetNetworkInfoAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(network.LanIp))
                return new RouterMappingResult(profile.DeviceId, false, null, network.WanIp, "无法确定当前内网 IPv4 地址。");
            var settings = store.GetSettings();
            var saved = store.GetLocalPortMappings().FirstOrDefault(item => item.RouterUdn == network.RouterUdn);
            var description = $"ResourceManager/{profile.DeviceId}";
            if (saved is null)
            {
                var owned = await FindOwnedMappingAsync(gateway, network, settings.ListenPort, description,
                    cancellationToken).ConfigureAwait(false);
                if (owned is not null)
                {
                    var ownedValue = owned.Value;
                    var existing = ownedValue.Mapping;
                    if (existing.InternalClient != network.LanIp || existing.InternalPort != settings.ListenPort ||
                        !existing.Enabled)
                    {
                        await gateway.DeleteMappingAsync(ownedValue.Port, cancellationToken).ConfigureAwait(false);
                        var repairedLease = await gateway.AddMappingAsync(ownedValue.Port, network.LanIp,
                            settings.ListenPort, description, cancellationToken).ConfigureAwait(false);
                        return SaveSuccessfulMapping(profile.DeviceId, network, ownedValue.Port, settings.ListenPort,
                            repairedLease);
                    }
                    var reusedLease = existing.LeaseSeconds;
                    if (reusedLease > 0)
                        reusedLease = await gateway.AddMappingAsync(ownedValue.Port, network.LanIp, settings.ListenPort,
                            description, cancellationToken).ConfigureAwait(false);
                    return SaveSuccessfulMapping(profile.DeviceId, network, ownedValue.Port, settings.ListenPort,
                        reusedLease);
                }
            }

            var first = preferredPort ?? saved?.ExternalPort ?? PreferredPort(profile.DeviceId);
            for (var offset = 0; offset <= NodeDefaults.GatewayPortEnd - NodeDefaults.GatewayPortStart; offset++)
            {
                var port = NodeDefaults.GatewayPortStart +
                           (first - NodeDefaults.GatewayPortStart + offset) %
                           (NodeDefaults.GatewayPortEnd - NodeDefaults.GatewayPortStart + 1);
                var existing = await gateway.GetMappingAsync(port, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    var ours = existing.Description.Equals(description, StringComparison.Ordinal) ||
                               existing.InternalClient == network.LanIp && existing.InternalPort == settings.ListenPort;
                    if (!ours) continue;
                    if (existing.InternalClient == network.LanIp && existing.InternalPort == settings.ListenPort && existing.Enabled)
                    {
                        var lease = existing.LeaseSeconds;
                        if (lease > 0)
                            lease = await gateway.AddMappingAsync(port, network.LanIp, settings.ListenPort, description,
                                cancellationToken).ConfigureAwait(false);
                        return SaveSuccessfulMapping(profile.DeviceId, network, port, settings.ListenPort, lease);
                    }
                    try { await gateway.DeleteMappingAsync(port, cancellationToken).ConfigureAwait(false); }
                    catch (UpnpException) { continue; }
                }

                try
                {
                    var lease = await gateway.AddMappingAsync(port, network.LanIp, settings.ListenPort, description, cancellationToken)
                        .ConfigureAwait(false);
                    return SaveSuccessfulMapping(profile.DeviceId, network, port, settings.ListenPort, lease);
                }
                catch (UpnpException ex) when (ex.ErrorCode == 718) { }
            }
            return new RouterMappingResult(profile.DeviceId, false, null, network.WanIp,
                $"端口 {NodeDefaults.GatewayPortStart}–{NodeDefaults.GatewayPortEnd} 已全部占用。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RouterMappingResult(store.GetSettings().Profile.DeviceId, false, null, null, ex.Message);
        }
        finally { mappingGate.Release(); }
    }

    private async Task<(int Port, UpnpPortMapping Mapping)?> FindOwnedMappingAsync(UpnpGatewayDevice gateway,
        RouterNetworkInfo network, int listenPort, string description, CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(8, 8);
        var tasks = Enumerable.Range(NodeDefaults.GatewayPortStart,
                NodeDefaults.GatewayPortEnd - NodeDefaults.GatewayPortStart + 1)
            .Select(async port =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var mapping = await gateway.GetMappingAsync(port, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return mapping is not null &&
                           (mapping.Description.Equals(description, StringComparison.Ordinal) ||
                            mapping.InternalClient == network.LanIp && mapping.InternalPort == listenPort)
                        ? (Port: port, Mapping: mapping)
                        : ((int Port, UpnpPortMapping Mapping)?)null;
                }
                finally { concurrency.Release(); }
            }).ToArray();
        return (await Task.WhenAll(tasks).ConfigureAwait(false))
            .Where(item => item is not null)
            .OrderBy(item => item!.Value.Port)
            .FirstOrDefault();
    }

    private RouterMappingResult SaveSuccessfulMapping(string deviceId, RouterNetworkInfo network, int externalPort,
        int internalPort, uint lease)
    {
        store.SaveLocalPortMapping(new LocalPortMapping(network.RouterUdn, network.RouterName, network.LanIp,
            network.WanIp, externalPort, internalPort, lease, DateTimeOffset.UtcNow));
        return new RouterMappingResult(deviceId, true, externalPort, network.WanIp, null);
    }

    public async Task MaintainMappingsAsync(CancellationToken cancellationToken = default)
    {
        var gateway = await gatewayClient.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (gateway is null) return;
        var info = await gateway.GetNetworkInfoAsync(cancellationToken).ConfigureAwait(false);
        var saved = store.GetLocalPortMappings().FirstOrDefault(item => item.RouterUdn == info.RouterUdn);
        if (saved is not null) await EnsureMappingAsync(saved.ExternalPort, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveCurrentMappingAsync(CancellationToken cancellationToken = default)
    {
        await mappingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var gateway = await gatewayClient.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (gateway is null) return false;
            var mapping = store.GetLocalPortMappings().FirstOrDefault(item => item.RouterUdn == gateway.Udn);
            if (mapping is null) return false;
            try { await gateway.DeleteMappingAsync(mapping.ExternalPort, cancellationToken).ConfigureAwait(false); }
            catch (UpnpException ex) when (ex.ErrorCode is 714 or 713) { }
            store.RemoveLocalPortMapping(mapping.RouterUdn);
            return true;
        }
        finally { mappingGate.Release(); }
    }

    internal static int PreferredPort(string deviceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        var value = BitConverter.ToUInt32(hash, 0);
        return NodeDefaults.GatewayPortStart + (int)(value %
            (NodeDefaults.GatewayPortEnd - NodeDefaults.GatewayPortStart + 1));
    }
}
