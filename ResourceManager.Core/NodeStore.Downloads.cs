namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    internal DownloadJob CreateDownload(PeerInfo peer, RemoteResource resource, string destinationDirectory, string? localName = null)
    {
        if (!resource.Available) throw new InvalidOperationException("资源当前不可下载。");
        var name = resource.Name;
        if (!StoredPaths.Valid(name) || name.Contains('/') || resource.Size < 0 || !Enum.IsDefined(resource.Kind))
            throw new InvalidDataException("资源名称、类型或大小无效。");
        var targetName = localName ?? name;
        if (!StoredPaths.Valid(targetName) || targetName.Contains('/') || targetName.Contains('\\'))
            throw new InvalidDataException("保存文件名无效。");
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        lock (gate)
        {
            // Persist the reservation before any network request starts. All download
            // managers sharing this store must include queued/paused jobs and sidecars.
            var reserved = GetDownloads().SelectMany(j => new[] { j.TargetPath, j.TargetPath + ".rm-part", j.TargetPath + ".rm-etag" })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Occupied(string path) => new[] { path, path + ".rm-part", path + ".rm-etag" }
                .Any(p => reserved.Contains(p) || File.Exists(p) || Directory.Exists(p));
            var target = Path.Combine(destinationDirectory, targetName);
            var stem = resource.Kind == ResourceKind.File ? Path.GetFileNameWithoutExtension(targetName) : targetName;
            var extension = resource.Kind == ResourceKind.File ? Path.GetExtension(targetName) : "";
            for (var index = 2; Occupied(target); index++)
                target = Path.Combine(destinationDirectory, $"{stem} ({index}){extension}");
            var job = new DownloadJob(Guid.NewGuid().ToString("N"), peer.DeviceId, resource.Id, name,
                resource.Kind, target, "等待下载", 0, resource.Size, null);
            SaveDownload(job);
            return job;
        }
    }
}
