using System.Text.Json.Serialization;

namespace ResourceManager.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceKind>))]
public enum ResourceKind { File, Folder }

[JsonConverter(typeof(JsonStringEnumConverter<PublishMode>))]
public enum PublishMode { Reference, Copy }

[JsonConverter(typeof(JsonStringEnumConverter<PeerEndpointKind>))]
public enum PeerEndpointKind { Direct, Gateway }

public sealed record NodeProfile(string DeviceId, string Nickname, byte[]? Avatar);
public sealed record PeerHello(string DeviceId, string Nickname, int Port, byte[]? Avatar, string[]? Capabilities = null);
public sealed record DiscoveredPeer(string DeviceId, string Ip, int Port, string Nickname);
public sealed record PeerInfo(string DeviceId, string Ip, int Port, string Nickname, byte[]? Avatar, DateTimeOffset? LastSeenUtc);
public sealed record PeerEndpoint(string DeviceId, string Ip, int Port, PeerEndpointKind Kind, string? GatewayId,
    string Source, DateTimeOffset? LastSuccessUtc);
public sealed record GatewayInfo(string Id, string Name, string WanIp, string? RouterUdn, int PortStart, int PortEnd,
    DateTimeOffset? LastRefreshUtc, string? LastStatus);
public sealed record LocalPortMapping(string RouterUdn, string RouterName, string LanIp, string WanIp, int ExternalPort,
    int InternalPort, uint LeaseSeconds, DateTimeOffset LastVerifiedUtc);
public sealed record RouterNetworkInfo(string RouterUdn, string RouterName, string AdapterName, string LanIp,
    string RouterLanIp, string WanIp, bool UpnpAvailable);
public sealed record RouterDeviceInfo(string DeviceId, string Nickname, bool SupportsRouterDiscovery, int? ExternalPort = null);
public sealed record RouterSnapshot(string SnapshotToken, RouterNetworkInfo? Router, IReadOnlyList<RouterDeviceInfo> Devices);
public sealed record RouterExposeRequest(string SnapshotToken, IReadOnlyList<string> DeviceIds);
public sealed record RouterMappingResult(string DeviceId, bool Success, int? ExternalPort, string? WanIp, string? Error);
public sealed record GatewayRefreshResult(int Found, int Connected, string Status, RouterNetworkInfo? Router);
public sealed record LocalResource(string Id, string Name, ResourceKind Kind, PublishMode Mode, string SourcePath, DateTimeOffset PublishedUtc);
public sealed record RemoteResource(string Id, string Name, ResourceKind Kind, PublishMode Mode, long Size, DateTimeOffset ModifiedUtc, bool Available);
public sealed record RemoteFile(string RelativePath, long Size, DateTimeOffset ModifiedUtc, bool IsDirectory = false);
public sealed record Favorite(string PeerId, string ResourceId, string Name, ResourceKind Kind);
public sealed record DownloadJob(string Id, string PeerId, string ResourceId, string ResourceName, ResourceKind Kind, string TargetPath, string Status, long DownloadedBytes, long TotalBytes, string? Error);
public sealed record AppSettings(NodeProfile Profile, int ListenPort, bool CloseToTray, bool AutoUpdate);

public static class NodeDefaults
{
    public const int Port = 37642;
    public const int DiscoveryPort = 37643;
    public const int GatewayPortStart = 48001;
    public const int GatewayPortEnd = 48099;
    public const string RouterDiscoveryCapability = "router-discovery-v1";
    public const string UpnpMappingCapability = "upnp-mapping-v1";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceManager");
}
