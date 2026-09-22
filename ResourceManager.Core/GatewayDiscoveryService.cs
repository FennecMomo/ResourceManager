using System.Collections.Concurrent;

namespace ResourceManager.Core;

public sealed class GatewayDiscoveryService(NodeStore store, PeerClient client)
{
    private const int ProbeConcurrency = 16;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    public async Task<GatewayRefreshResult> RefreshAsync(GatewayInfo gateway, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var found = new ConcurrentDictionary<string, Candidate>(StringComparer.Ordinal);
        var checkedPorts = new ConcurrentDictionary<int, byte>();
        progress?.Report("正在检查已保存端口");

        var cached = store.GetPeerEndpoints(gatewayId: gateway.Id)
            .Where(item => item.Source != "DeviceChanged")
            .GroupBy(item => item.Port)
            .Select(group => group.OrderByDescending(item => item.LastSuccessUtc).First())
            .ToArray();
        await ProbeEndpointsAsync(gateway, cached.Select(item => (item.Port, (string?)item.DeviceId)), found,
            checkedPorts, cancellationToken).ConfigureAwait(false);

        var bootstrap = found.Values.FirstOrDefault(item => SupportsGateway(item.Health));
        if (bootstrap is null)
        {
            progress?.Report("正在扫描路由器入口");
            var ports = Enumerable.Range(gateway.PortStart, gateway.PortEnd - gateway.PortStart + 1)
                .Where(port => !checkedPorts.ContainsKey(port))
                .Select(port => (port, (string?)null));
            await ProbeEndpointsAsync(gateway, ports, found, checkedPorts, cancellationToken).ConfigureAwait(false);
            bootstrap = found.Values.FirstOrDefault(item => SupportsGateway(item.Health));
        }

        if (bootstrap is null)
        {
            var emptyStatus = found.Count == 0
                ? "该路由器下没有找到可用设备"
                : $"找到 {found.Count} 台旧版设备，但没有可用于扩展发现的设备";
            store.SaveGatewayRefresh(gateway.Id, null, emptyStatus);
            return new GatewayRefreshResult(found.Count, found.Count, emptyStatus, null);
        }

        progress?.Report($"正在通过 {bootstrap.Peer.Nickname} 查找内部设备");
        RouterSnapshot snapshot;
        try
        {
            snapshot = await client.CreateRouterSnapshotAsync(bootstrap.Peer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failureStatus = $"扩展发现失败：{FriendlyMessage(ex)}";
            store.SaveGatewayRefresh(gateway.Id, null, failureStatus);
            return new GatewayRefreshResult(found.Count, found.Count, failureStatus, null);
        }

        var missing = snapshot.Devices
            .Where(device => !found.ContainsKey(device.DeviceId))
            .Select(device => device.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            progress?.Report("正在检查已有映射");
            var uncheckedPorts = Enumerable.Range(gateway.PortStart, gateway.PortEnd - gateway.PortStart + 1)
                .Where(port => !checkedPorts.ContainsKey(port))
                .Select(port => (port, (string?)null));
            await ProbeEndpointsAsync(gateway, uncheckedPorts, found, checkedPorts, cancellationToken).ConfigureAwait(false);
            missing = snapshot.Devices
                .Where(device => !found.ContainsKey(device.DeviceId))
                .Select(device => device.DeviceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        var mappingFailures = new List<string>();
        if (missing.Length > 0)
        {
            progress?.Report($"正在为 {missing.Length} 台设备申请 UPnP");
            IReadOnlyList<RouterMappingResult> results;
            try
            {
                results = await client.ExposeRouterDevicesAsync(bootstrap.Peer,
                    new RouterExposeRequest(snapshot.SnapshotToken, missing), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results = [];
                mappingFailures.Add(FriendlyMessage(ex));
            }

            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Success || result.ExternalPort is null)
                {
                    mappingFailures.Add(result.Error ?? $"{result.DeviceId} 映射失败");
                    continue;
                }
                var verified = await ProbeOneAsync(gateway, result.ExternalPort.Value, result.DeviceId,
                    cancellationToken).ConfigureAwait(false);
                if (verified is null)
                    mappingFailures.Add($"{result.DeviceId} 映射成功但外部验证失败");
                else
                    found[result.DeviceId] = verified;
            }
        }

        var mismatch = snapshot.Router is { WanIp.Length: > 0 } &&
                       !string.Equals(snapshot.Router.WanIp, gateway.WanIp, StringComparison.Ordinal);
        var status = mismatch
            ? $"已连接 {found.Count} 台；引导设备报告的外部入口为 {snapshot.Router!.WanIp}，当前记录仍保留 {gateway.WanIp}"
            : mappingFailures.Count > 0
                ? $"已连接 {found.Count} 台；{mappingFailures[0]}"
                : $"已连接 {found.Count} 台设备";
        store.SaveGatewayRefresh(gateway.Id, snapshot.Router?.RouterUdn, status);
        return new GatewayRefreshResult(snapshot.Devices.Count, found.Count, status, snapshot.Router);
    }

    private async Task ProbeEndpointsAsync(GatewayInfo gateway, IEnumerable<(int Port, string? ExpectedDeviceId)> endpoints,
        ConcurrentDictionary<string, Candidate> found, ConcurrentDictionary<int, byte> checkedPorts,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(ProbeConcurrency, ProbeConcurrency);
        var tasks = endpoints.Select(async endpoint =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                checkedPorts.TryAdd(endpoint.Port, 0);
                var candidate = await ProbeOneAsync(gateway, endpoint.Port, endpoint.ExpectedDeviceId,
                    cancellationToken).ConfigureAwait(false);
                if (candidate is not null) found[candidate.Peer.DeviceId] = candidate;
            }
            finally { concurrency.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<Candidate?> ProbeOneAsync(GatewayInfo gateway, int port, string? expectedDeviceId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        PeerHello health;
        try { health = await client.ProbeAddressAsync(gateway.WanIp, port, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (expectedDeviceId is not null && !string.Equals(expectedDeviceId, health.DeviceId, StringComparison.Ordinal))
        {
            var old = store.GetPeerEndpoints(expectedDeviceId, gateway.Id)
                .FirstOrDefault(item => item.Ip == gateway.WanIp && item.Port == port);
            if (old is not null) store.MarkEndpointDeviceChanged(old);
        }

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var peer = await client.ConnectAsync(gateway.WanIp, port, health.DeviceId, connectTimeout.Token,
                gateway.Id, "GatewayScan").ConfigureAwait(false);
            return new Candidate(peer, health);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool SupportsGateway(PeerHello hello) =>
        hello.Capabilities?.Contains(NodeDefaults.RouterDiscoveryCapability, StringComparer.Ordinal) == true;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "引导设备拒绝了请求，请先重新连接",
        HttpRequestException => "无法连接引导设备",
        _ => exception.Message
    };

    private sealed record Candidate(PeerInfo Peer, PeerHello Health);
}
