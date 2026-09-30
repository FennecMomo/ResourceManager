using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

public sealed class ResourceCatalog(NodeStore store)
{
    private static readonly Regex UpdateFileName = new(
        @"^ResourceManager(?:[-_ ]v?\d+\.\d+\.\d+)?\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProductVersionPrefix = new(
        @"^(?<version>\d+\.\d+\.\d+)(?:[+-]|$)", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim updateInspection = new(1, 1);
    private string? cachedUpdateKey;
    private SharedUpdatePackage? cachedUpdate;

    public IReadOnlyList<RemoteResource> List() => store.GetResources().Select(resource => Describe(resource)).ToList();

    public async Task<SharedUpdatePackage?> FindLatestUpdateAsync(CancellationToken cancellationToken = default, Func<LocalResource, bool>? canAccess = null)
    {
        var candidates = new List<(LocalResource Resource, FileInfo File, Version Version)>();
        foreach (var resource in store.GetResources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canAccess is not null && !canAccess(resource)) continue;
            if (resource.Kind != ResourceKind.File || !UpdateFileName.IsMatch(resource.Name)) continue;
            try
            {
                var file = new FileInfo(resource.SourcePath);
                if (!file.Exists || file.Length is <= 0 or > UpdateClient.MaxPackageBytes) continue;
                var info = FileVersionInfo.GetVersionInfo(file.FullName);
                var productVersion = ProductVersionPrefix.Match(info.ProductVersion ?? "").Groups["version"].Value;
                var version = productVersion.Length > 0 && Version.TryParse(productVersion, out var parsed)
                    ? parsed
                    : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
                if (version == new Version(0, 0, 0)) continue;
                candidates.Add((resource, file, version));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A published file can disappear or change while the catalog is being inspected.
            }
        }

        var latest = candidates.OrderByDescending(item => item.Version).FirstOrDefault();
        if (latest.Resource is null) return null;
        var cacheKey = $"{latest.Resource.Id}\0{latest.File.FullName}\0{latest.File.Length}\0{latest.File.LastWriteTimeUtc.Ticks}";
        await updateInspection.WaitAsync(cancellationToken);
        try
        {
            if (cacheKey == cachedUpdateKey) return cachedUpdate;
            await using var stream = new FileStream(latest.File.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            latest.File.Refresh();
            if (!latest.File.Exists || latest.File.Length != stream.Length)
                throw new IOException("本地更新源在生成校验信息时发生了变化。");
            cachedUpdate = new SharedUpdatePackage(latest.Resource.Id, latest.Version.ToString(3), latest.File.Length,
                hash, latest.File.LastWriteTimeUtc);
            cachedUpdateKey = cacheKey;
            return cachedUpdate;
        }
        finally { updateInspection.Release(); }
    }

    public RemoteResource Describe(LocalResource resource, CancellationToken cancellationToken = default)
    {
        if (IsGitMetadataPath(resource.SourcePath))
            return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode,
                0, resource.PublishedUtc, false, resource.Note, resource.GroupId);
        if (resource.Kind == ResourceKind.File)
        {
            var file = new FileInfo(resource.SourcePath);
            return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode,
                file.Exists ? file.Length : 0, file.Exists ? file.LastWriteTimeUtc : resource.PublishedUtc, file.Exists, resource.Note, resource.GroupId);
        }
        var directory = new DirectoryInfo(resource.SourcePath);
        if (!directory.Exists) return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, 0, resource.PublishedUtc, false, resource.Note, resource.GroupId);
        try
        {
            var files = EnumerateFiles(resource, cancellationToken).ToList();
            return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, files.Where(f => !f.IsDirectory).Sum(f => f.Size),
                files.Count == 0 ? directory.LastWriteTimeUtc : files.Max(f => f.ModifiedUtc), true, resource.Note, resource.GroupId);
        }
        catch (IOException) { return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, 0, resource.PublishedUtc, false, resource.Note, resource.GroupId); }
        catch (UnauthorizedAccessException) { return new RemoteResource(resource.Id, resource.Name, resource.Kind, resource.Mode, 0, resource.PublishedUtc, false, resource.Note, resource.GroupId); }
    }

    public IReadOnlyList<RemoteFile> ListFiles(string resourceId, string? privatePeer = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resource = (privatePeer is null ? store.GetResource(resourceId) : store.GetPrivateResource(resourceId, privatePeer)) ?? throw new FileNotFoundException("资源已撤销。");
        if (!Describe(resource, cancellationToken).Available) throw new FileNotFoundException("资源原文件不可用。");
        return EnumerateFiles(resource, cancellationToken).ToList();
    }

    private static IEnumerable<RemoteFile> EnumerateFiles(LocalResource resource, CancellationToken cancellationToken = default)
    {
        if (resource.Kind == ResourceKind.File)
        {
            var file = new FileInfo(resource.SourcePath);
            if (file.Exists) yield return new RemoteFile("", file.Length, file.LastWriteTimeUtc);
            yield break;
        }
        foreach (var item in Walk(resource.SourcePath, "", cancellationToken)) yield return item;
    }

    private static IEnumerable<RemoteFile> Walk(string root, string relative, CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(Path.Combine(root, relative)))
        {
            if (Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var child = string.IsNullOrEmpty(relative) ? Path.GetFileName(entry) : Path.Combine(relative, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                yield return new RemoteFile(child.Replace('\\', '/'), 0, Directory.GetLastWriteTimeUtc(entry), true);
                foreach (var item in Walk(root, child, cancellationToken)) yield return item;
            }
            else
            {
                var file = new FileInfo(entry);
                yield return new RemoteFile(child.Replace('\\', '/'), file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    public FileInfo ResolveFile(string resourceId, string? relativePath, string? privatePeer = null)
    {
        var resource = (privatePeer is null ? store.GetResource(resourceId) : store.GetPrivateResource(resourceId, privatePeer)) ?? throw new FileNotFoundException("资源已撤销。");
        if (IsGitMetadataPath(resource.SourcePath)) throw new FileNotFoundException("Git 元数据不作为普通资源共享。");
        if (resource.Kind == ResourceKind.File)
        {
            if (!string.IsNullOrEmpty(relativePath)) throw new ArgumentException("文件资源不接受子路径。");
            return new FileInfo(resource.SourcePath);
        }
        if (string.IsNullOrEmpty(relativePath)) throw new ArgumentException("请选择文件夹内的文件。");
        var normalized = relativePath.Replace('\\', '/');
        // Reject Win32 aliases before resolving the path, including trailing dots/
        // spaces which could otherwise turn '.git.' into the hidden '.git' folder.
        if (!StoredPaths.Valid(normalized)) throw new ArgumentException("路径无效。");
        var parts = normalized.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.Contains(':') ||
                           p.Equals(".git", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("路径无效。");
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

    private static bool IsGitMetadataPath(string path) => Path.GetFullPath(path)
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
}
