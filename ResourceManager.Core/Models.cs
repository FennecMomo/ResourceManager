using System.Text.Json.Serialization;

namespace ResourceManager.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceKind>))]
public enum ResourceKind { File, Folder }

[JsonConverter(typeof(JsonStringEnumConverter<PublishMode>))]
public enum PublishMode { Reference, Copy }

public sealed record NodeProfile(string DeviceId, string Nickname, byte[]? Avatar);
public sealed record PeerHello(string DeviceId, string Nickname, int Port, byte[]? Avatar);
public sealed record DiscoveredPeer(string DeviceId, string Ip, int Port, string Nickname);
public sealed record PeerInfo(string DeviceId, string Ip, int Port, string Nickname, byte[]? Avatar, DateTimeOffset? LastSeenUtc);
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
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceManager");
}
