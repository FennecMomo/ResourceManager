using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace ResourceManager.Core;

public sealed record UpnpPortMapping(int ExternalPort, string InternalClient, int InternalPort, string Protocol,
    string Description, uint LeaseSeconds, bool Enabled);

public sealed class UpnpException(string message, int? errorCode = null) : Exception(message)
{
    public int? ErrorCode { get; } = errorCode;
}

public class UpnpGatewayClient : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public virtual async Task<UpnpGatewayDevice?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var locations = await DiscoverLocationsAsync(cancellationToken).ConfigureAwait(false);
        var preferredGateways = GetDefaultGatewayAddresses();
        foreach (var location in locations.OrderByDescending(location => preferredGateways.Contains(location.Host)))
        {
            try
            {
                using var response = await http.GetAsync(location, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var services = document.Descendants().Where(node => node.Name.LocalName == "service");
                var service = services.Select(node => new
                    {
                        Type = Value(node, "serviceType"),
                        Control = Value(node, "controlURL")
                    })
                    .FirstOrDefault(item => item.Type.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) ||
                                            item.Type.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase));
                if (service is null || string.IsNullOrWhiteSpace(service.Control)) continue;
                var udn = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "UDN")?.Value.Trim();
                var name = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "friendlyName")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(udn)) udn = $"upnp:{location.Host}";
                if (string.IsNullOrWhiteSpace(name)) name = "UPnP 路由器";
                return new UpnpGatewayDevice(http, new Uri(location, service.Control), service.Type, udn, name, location.Host);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { }
        }
        return null;
    }

    private static HashSet<string> GetDefaultGatewayAddresses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
            var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                item.OperationalStatus == OperationalStatus.Up &&
                item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(local)));
            if (network is null) return result;
            foreach (var gateway in network.GetIPProperties().GatewayAddresses)
            {
                var address = gateway.Address.IsIPv4MappedToIPv6
                    ? gateway.Address.MapToIPv4()
                    : gateway.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork) result.Add(address.ToString());
            }
        }
        catch (SocketException) { }
        return result;
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverLocationsAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var searchTarget in new[]
                 {
                     "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                     "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                     "ssdp:all"
                 })
        {
            var request = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                $"ST: {searchTarget}\r\n\r\n");
            await udp.SendAsync(request, target, cancellationToken).ConfigureAwait(false);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var result = new HashSet<Uri>();
        while (true)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            var headers = Encoding.UTF8.GetString(packet.Buffer).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            var location = headers.FirstOrDefault(line => line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
            if (location is not null && Uri.TryCreate(location[(location.IndexOf(':') + 1)..].Trim(), UriKind.Absolute, out var uri))
                result.Add(uri);
        }
        return result.ToArray();
    }

    private static string Value(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(node => node.Name.LocalName == localName)?.Value.Trim() ?? "";

    public void Dispose() => http.Dispose();
}

public sealed class UpnpGatewayDevice
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly HttpClient http;
    private readonly Uri controlUri;
    private readonly string serviceType;

    internal UpnpGatewayDevice(HttpClient http, Uri controlUri, string serviceType, string udn, string name, string routerLanIp)
    {
        this.http = http;
        this.controlUri = controlUri;
        this.serviceType = serviceType;
        Udn = udn;
        Name = name;
        RouterLanIp = routerLanIp;
        (LocalIp, AdapterName) = ResolveLocalInterface(controlUri);
    }

    public string Udn { get; }
    public string Name { get; }
    public string RouterLanIp { get; }
    public string LocalIp { get; }
    public string AdapterName { get; }

    public async Task<RouterNetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken = default)
    {
        var external = "";
        try
        {
            var response = await InvokeAsync("GetExternalIPAddress", [], cancellationToken).ConfigureAwait(false);
            var candidate = response.Descendants()
                .FirstOrDefault(node => node.Name.LocalName == "NewExternalIPAddress")?.Value.Trim();
            if (IPAddress.TryParse(candidate, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                external = candidate!;
        }
        catch (UpnpException) { }
        return new RouterNetworkInfo(Udn, Name, AdapterName, LocalIp, RouterLanIp, external, true);
    }

    public async Task<UpnpPortMapping?> GetMappingAsync(int externalPort, string protocol = "TCP",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await InvokeAsync("GetSpecificPortMappingEntry",
            [
                ("NewRemoteHost", ""),
                ("NewExternalPort", externalPort.ToString()),
                ("NewProtocol", protocol)
            ], cancellationToken).ConfigureAwait(false);
            string Read(string name) => response.Descendants().FirstOrDefault(node => node.Name.LocalName == name)?.Value.Trim() ?? "";
            return new UpnpPortMapping(externalPort, Read("NewInternalClient"), int.TryParse(Read("NewInternalPort"), out var port) ? port : 0,
                protocol, Read("NewPortMappingDescription"), uint.TryParse(Read("NewLeaseDuration"), out var lease) ? lease : 0,
                Read("NewEnabled") is "1" or "true");
        }
        catch (UpnpException ex) when (ex.ErrorCode is 714 or 713) { return null; }
    }

    public async Task<uint> AddMappingAsync(int externalPort, string internalClient, int internalPort, string description,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await AddMappingCoreAsync(externalPort, internalClient, internalPort, description, 0, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (UpnpException ex) when (ex.ErrorCode is not 718)
        {
            const uint fallbackLease = 86400;
            await AddMappingCoreAsync(externalPort, internalClient, internalPort, description, fallbackLease, cancellationToken).ConfigureAwait(false);
            return fallbackLease;
        }
    }

    private async Task AddMappingCoreAsync(int externalPort, string internalClient, int internalPort, string description,
        uint leaseSeconds, CancellationToken cancellationToken)
    {
        await InvokeAsync("AddPortMapping",
        [
            ("NewRemoteHost", ""),
            ("NewExternalPort", externalPort.ToString()),
            ("NewProtocol", "TCP"),
            ("NewInternalPort", internalPort.ToString()),
            ("NewInternalClient", internalClient),
            ("NewEnabled", "1"),
            ("NewPortMappingDescription", description),
            ("NewLeaseDuration", leaseSeconds.ToString())
        ], cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteMappingAsync(int externalPort, CancellationToken cancellationToken = default) =>
        InvokeAsync("DeletePortMapping",
        [
            ("NewRemoteHost", ""),
            ("NewExternalPort", externalPort.ToString()),
            ("NewProtocol", "TCP")
        ], cancellationToken);

    private async Task<XDocument> InvokeAsync(string action, IReadOnlyList<(string Name, string Value)> arguments,
        CancellationToken cancellationToken)
    {
        XNamespace service = serviceType;
        var body = new XElement(Soap + "Body",
            new XElement(service + action, arguments.Select(item => new XElement(item.Name, item.Value))));
        var envelope = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap),
                new XAttribute(Soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), body));
        using var request = new HttpRequestMessage(HttpMethod.Post, controlUri)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        XDocument document;
        try { document = XDocument.Parse(content); }
        catch (System.Xml.XmlException ex) { throw new UpnpException($"路由器返回了无法识别的 UPnP 响应：{ex.Message}"); }
        if (!response.IsSuccessStatusCode)
        {
            var codeText = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "errorCode")?.Value;
            var description = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "errorDescription")?.Value;
            var code = int.TryParse(codeText, out var parsed) ? parsed : (int?)null;
            throw new UpnpException(string.IsNullOrWhiteSpace(description)
                ? $"UPnP 操作失败（HTTP {(int)response.StatusCode}）。"
                : $"UPnP 操作失败：{description}。", code);
        }
        return document;
    }

    private static (string Ip, string Adapter) ResolveLocalInterface(Uri destination)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(destination.Host, destination.Port > 0 ? destination.Port : 80);
            var ip = ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.GetIPProperties().UnicastAddresses.Any(item => item.Address.ToString() == ip))
                    return (ip, network.Name);
            }
            return (ip, "当前网络");
        }
        catch { return ("", "当前网络"); }
    }
}
