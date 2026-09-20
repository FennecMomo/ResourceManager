namespace ResourceManager.Core;

public sealed class ResourceCatalog(NodeStore store)
{
    public IReadOnlyList<RemoteResource> List() => store.GetResources().Select(Describe).ToList();

    public RemoteResource Describe(LocalResource resource)
    {
        if (resource.Kind == ResourceKind.File)
        {
            var file = new FileInfo(resource.SourcePath);
            return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode,
                file.Exists ? file.Length : 0, file.Exists ? file.LastWriteTimeUtc : resource.PublishedUtc, file.Exists);
        }
        var directory = new DirectoryInfo(resource.SourcePath);
        if (!directory.Exists) return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, 0, resource.PublishedUtc, false);
        try
        {
            var files = EnumerateFiles(resource).ToList();
            return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, files.Where(f => !f.IsDirectory).Sum(f => f.Size),
                files.Count == 0 ? directory.LastWriteTimeUtc : files.Max(f => f.ModifiedUtc), true);
        }
        catch (IOException) { return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, 0, resource.PublishedUtc, false); }
        catch (UnauthorizedAccessException) { return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, 0, resource.PublishedUtc, false); }
    }

    public IReadOnlyList<RemoteFile> ListFiles(string resourceId)
    {
        var resource = store.GetResource(resourceId) ?? throw new FileNotFoundException("资源已撤销。");
        if (!Describe(resource).Available) throw new FileNotFoundException("资源原文件不可用。");
        return EnumerateFiles(resource).ToList();
    }

    private static IEnumerable<RemoteFile> EnumerateFiles(LocalResource resource)
    {
        if (resource.Kind == ResourceKind.File)
        {
            var file = new FileInfo(resource.SourcePath);
            if (file.Exists) yield return new RemoteFile("", file.Length, file.LastWriteTimeUtc);
            yield break;
        }
        foreach (var item in Walk(resource.SourcePath, "")) yield return item;
    }

    private static IEnumerable<RemoteFile> Walk(string root, string relative)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(Path.Combine(root, relative)))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var child = string.IsNullOrEmpty(relative) ? Path.GetFileName(entry) : Path.Combine(relative, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                yield return new RemoteFile(child.Replace('\\', '/'), 0, Directory.GetLastWriteTimeUtc(entry), true);
                foreach (var item in Walk(root, child)) yield return item;
            }
            else
            {
                var file = new FileInfo(entry);
                yield return new RemoteFile(child.Replace('\\', '/'), file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    public FileInfo ResolveFile(string resourceId, string? relativePath)
    {
        var resource = store.GetResource(resourceId) ?? throw new FileNotFoundException("资源已撤销。");
        if (resource.Kind == ResourceKind.File)
        {
            if (!string.IsNullOrEmpty(relativePath)) throw new ArgumentException("文件资源不接受子路径。");
            return new FileInfo(resource.SourcePath);
        }
        if (string.IsNullOrEmpty(relativePath)) throw new ArgumentException("请选择文件夹内的文件。");
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.Contains(':'))) throw new ArgumentException("路径无效。");
        var root = Path.GetFullPath(resource.SourcePath);
        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("不允许访问链接路径。");
            }
        }
        var full = Path.GetFullPath(current);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径超出已发布目录。");
        return new FileInfo(full);
    }
}
