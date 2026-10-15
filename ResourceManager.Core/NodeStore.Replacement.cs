namespace ResourceManager.Core;

public sealed record PublicationReplacement(LocalResource Resource, bool ManagedCopyDeleted, string? CleanupWarning);

public sealed partial class NodeStore
{
    public PublicationReplacement ReplaceResourceReference(string id, string sourcePath)
    {
        lock (resourceCopyGate)
        lock (gate)
        {
            var previous = GetResource(id) ?? throw new InvalidOperationException("资源已撤销。");
            if (!Path.IsPathFullyQualified(sourcePath)) throw new ArgumentException("请选择绝对路径。");
            sourcePath = Path.GetFullPath(sourcePath);
            for (var current = sourcePath; current is not null; current = Path.GetDirectoryName(current))
            {
                if (Path.GetFileName(current).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Git 元数据不能作为普通资源发布。");
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("更换引用不接受链接路径。");
            }
            foreach (var managed in new[] { LibraryDirectory, PrivateResourceDirectory })
                if (StorageLocation.IsWithin(sourcePath, managed) || sourcePath.Equals(managed, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请选择软件副本目录以外的原始文件或文件夹。");
            var kind = File.Exists(sourcePath) ? ResourceKind.File : Directory.Exists(sourcePath)
                ? ResourceKind.Folder : throw new FileNotFoundException("资源路径不存在。", sourcePath);
            if (kind != previous.Kind) throw new ArgumentException("新引用必须与原发布的文件或文件夹类型一致。");
            var updated = previous with { SourcePath = sourcePath, Mode = PublishMode.Reference };
            using (var db = Open())
            using (var command = Cmd(db, "UPDATE resources SET source_path=$path,mode='Reference' WHERE id=$id", "$path", sourcePath, "$id", id))
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("资源已撤销。");

            if (previous.Mode != PublishMode.Copy) return new(updated, false, null);
            // Delete only the unique managed root after the durable reference switch.
            // Existing references and resumable uploads can still need that copy.
            var root = Path.GetFullPath(Path.Combine(LibraryDirectory, id));
            try
            {
                if (!Guid.TryParseExact(id, "N", out _) || !StorageLocation.IsWithin(root, LibraryDirectory) ||
                    !StorageLocation.IsWithin(previous.SourcePath, root))
                    throw new IOException("旧副本路径不符合清理范围。");
                bool UsesRoot(string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    StorageLocation.IsWithin(path, root) || StorageLocation.IsWithin(root, path);
                bool PrivateUsesRoot()
                {
                    using var db = Open();
                    using var command = Cmd(db, "SELECT source_path FROM private_resources");
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) if (UsesRoot(reader.GetString(0))) return true;
                    return false;
                }
                if (GetResources().Any(r => UsesRoot(r.SourcePath)) ||
                    PrivateUsesRoot() ||
                    GetUploads().Any(job => UsesRoot(job.SourcePath)))
                    throw new IOException("旧副本仍被其他发布、私发或上传任务引用，已保留。");
                void CheckTree(string path)
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("旧副本包含链接，已保留供检查。");
                    if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
                }
                if (Directory.Exists(root))
                {
                    for (var parent = Path.GetDirectoryName(root); parent is not null; parent = Path.GetDirectoryName(parent))
                        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("旧副本所在路径包含链接，已保留供检查。");
                    CheckTree(root);
                    Directory.Delete(root, true);
                }
                return new(updated, true, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(updated, false, "引用已更换，旧副本未完全清理：" + ex.Message);
            }
        }
    }
}
