using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed class LanDiscoveryService(NodeStore store, int discoveryPort = NodeDefaults.DiscoveryPort) : IAsyncDisposable
{
    private const string Protocol = "ResourceManager.LanDiscovery.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private UdpClient? listener;
    private CancellationTokenSource? stopping;
    private Task? responder;

    public bool IsRunning => listener is not null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (listener is not null) throw new InvalidOperationException("局域网发现服务已经启动。");

        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            udp.EnableBroadcast = true;
            var cancellation = new CancellationTokenSource();
            listener = udp;
            stopping = cancellation;
            responder = RespondAsync(udp, cancellation.Token);
            return Task.CompletedTask;
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<DiscoveredPeer>> DiscoverAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default,
        IEnumerable<IPAddress>? broadcastAddresses = null)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var settings = store.GetSettings();
        var request = JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryPacket(Protocol, "query", settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort), Json);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var targets = (broadcastAddresses ?? GetBroadcastAddresses()).Distinct().ToArray();
        var sent = false;
        foreach (var address in targets)
        {
            try
            {
                await udp.SendAsync(request, new IPEndPoint(address, discoveryPort), cancellationToken).ConfigureAwait(false);
                sent = true;
            }
            catch (SocketException) { }
        }
        if (!sent) return [];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        var found = new Dictionary<string, DiscoveredPeer>(StringComparer.Ordinal);
        while (true)
        {
            UdpReceiveResult response;
            try { response = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }

            var packet = ReadPacket(response.Buffer, "response");
            if (packet is null || packet.DeviceId == settings.Profile.DeviceId) continue;
            var address = response.RemoteEndPoint.Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork) continue;
            found[packet.DeviceId] = new DiscoveredPeer(
                packet.DeviceId, address.ToString(), packet.Port, packet.Nickname.Trim());
        }
        return found.Values.OrderBy(peer => peer.Nickname, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task RespondAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                UdpReceiveResult request;
                try { request = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested) { continue; }
                var packet = ReadPacket(request.Buffer, "query");
                var settings = store.GetSettings();
                if (packet is null || packet.DeviceId == settings.Profile.DeviceId) continue;
                var response = JsonSerializer.SerializeToUtf8Bytes(
                    new DiscoveryPacket(Protocol, "response", settings.Profile.DeviceId,
                        settings.Profile.Nickname, settings.ListenPort), Json);
                try { await udp.SendAsync(response, request.RemoteEndPoint, cancellationToken).ConfigureAwait(false); }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static DiscoveryPacket? ReadPacket(byte[] bytes, string expectedType)
    {
        if (bytes.Length is 0 or > 4096) return null;
        try
        {
            var packet = JsonSerializer.Deserialize<DiscoveryPacket>(bytes, Json);
            return packet is not null && packet.Protocol == Protocol && packet.Type == expectedType &&
                   !string.IsNullOrWhiteSpace(packet.DeviceId) && packet.DeviceId.Length <= 100 &&
                   !string.IsNullOrWhiteSpace(packet.Nickname) && packet.Nickname.Length <= 80 &&
                   packet.Port is >= 1 and <= 65535
                ? packet
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var index = 0; index < broadcast.Length; index++)
                    broadcast[index] = (byte)(ip[index] | ~mask[index]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    public async Task StopAsync()
    {
        if (listener is null) return;
        var udp = listener;
        var cancellation = stopping!;
        var task = responder!;
        listener = null;
        stopping = null;
        responder = null;
        cancellation.Cancel();
        udp.Dispose();
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed record DiscoveryPacket(string Protocol, string Type, string DeviceId, string Nickname, int Port);
}
