using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed record StoredResourceSpec(string Id, string Name, ResourceKind Kind, GroupAccess Access, string[] Allowed, string Note = "", SharedUpdatePackage? Update = null);
public sealed record StoredEntry(string Path, long Size, string Sha256, bool Directory, DateTimeOffset Modified);
public sealed record StoredEntryProgress(long Offset, bool Complete);
public sealed record StoredUploadState(StoredResourceSpec Spec, bool Complete);
public sealed record StoredPermission(GroupAccess Access, string[] Allowed);
public sealed record UploadJob(string Id, string Server, string SourcePath, StoredResourceSpec Spec, string Status, long Sent = 0, long Total = 0, string? Error = null);

public static class StoredPaths
{
    // Apply a portable Windows/Linux policy before a name ever reaches disk.
    public static bool Valid(string value) => !string.IsNullOrEmpty(value) && value.Length <= 4096 && value.Split('/').All(p =>
        p.Length is > 0 and <= 255 && p is not "." and not ".." && !p.EndsWith('.') && !p.EndsWith(' ') &&
        !p.Any(c => c < 32 || "\\:*?\"<>|".Contains(c)) && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase));
    public static string Resolve(string root, string relative)
    {
        if (!Valid(relative)) throw new ArgumentException("文件路径无效。");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("文件路径越界。");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持符号链接或重解析点。");
            if (current == root) break;
        }
        return path;
    }
}

public sealed partial class WorkspaceClient
{
    public Task<StoredUploadState> GetStoredStateAsync(ServerBinding b, WorkspaceSession s, string id, CancellationToken t) => SendJsonAsync<StoredUploadState>(b, s, HttpMethod.Get, "storage/uploads/" + id, null, t);
    public Task<StoredUploadState> BeginUploadAsync(ServerBinding b, WorkspaceSession s, StoredResourceSpec spec, CancellationToken t) => SendJsonAsync<StoredUploadState>(b, s, HttpMethod.Put, "storage/uploads/" + spec.Id, spec, t);
    public Task<StoredEntryProgress> DeclareEntryAsync(ServerBinding b, WorkspaceSession s, string id, StoredEntry entry, CancellationToken t) => SendJsonAsync<StoredEntryProgress>(b, s, HttpMethod.Put, "storage/uploads/" + id + "/entry", entry, t);
    public async Task<StoredEntryProgress> UploadChunkAsync(ServerBinding b, WorkspaceSession s, string id, string path, long offset, byte[] bytes, CancellationToken t)
    {
        using var request = Authorized(b, s, HttpMethod.Put, $"storage/uploads/{id}/chunk?path={Uri.EscapeDataString(path)}&offset={offset}");
        request.Content = new ByteArrayContent(bytes);
        using var response = await resourceHttp.SendAsync(request, t).ConfigureAwait(false); await Check(response, t).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<StoredEntryProgress>(WorkspaceProtocol.Json, t).ConfigureAwait(false))!;
    }
    public Task<bool> CompleteEntryAsync(ServerBinding b, WorkspaceSession s, string id, string path, CancellationToken t) => SendJsonAsync<bool>(b, s, HttpMethod.Post, $"storage/uploads/{id}/verify?path={Uri.EscapeDataString(path)}", null, t);
    public Task<bool> CompleteUploadAsync(ServerBinding b, WorkspaceSession s, string id, int count, CancellationToken t) => SendJsonAsync<bool>(b, s, HttpMethod.Post, $"storage/uploads/{id}/commit?count={count}", null, t);
    public Task<bool> DeleteStoredAsync(ServerBinding b, WorkspaceSession s, string id, CancellationToken t) => SendJsonAsync<bool>(b, s, HttpMethod.Delete, "storage/uploads/" + id, null, t);
    public Task<bool> StoredPermissionAsync(ServerBinding b, WorkspaceSession s, string id, StoredPermission permission, CancellationToken t) => SendJsonAsync<bool>(b, s, HttpMethod.Patch, "storage/uploads/" + id + "/permissions", permission, t);
    public Task<WorkspaceResourceReply> StoredResourceAsync(ServerBinding b, WorkspaceSession s, WorkspaceResourceRequest request, CancellationToken t) => SendJsonAsync<WorkspaceResourceReply>(b, s, HttpMethod.Post, "storage/read", request, t);
}

public sealed class WorkspaceUploadManager(NodeStore store, WorkspaceClient client)
{
    public UploadJob Create(ServerBinding server, string path, GroupAccess access, string[] allowed)
    {
        path = Path.GetFullPath(path); var folder = Directory.Exists(path);
        if (!folder && !File.Exists(path)) throw new FileNotFoundException("上传源不存在。");
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (!StoredPaths.Valid(name) || name.Contains('/')) throw new ArgumentException("上传名称无效。");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持上传符号链接。");
        var id = "s-" + Guid.NewGuid().ToString("N");
        var job = new UploadJob(id, server.Id, path, new(id, name, folder ? ResourceKind.Folder : ResourceKind.File, access, allowed), "等待上传");
        store.SaveUpload(job); return job;
    }
    public async Task RunAsync(string id, ServerBinding binding, Func<WorkspaceSession?> session, IProgress<UploadJob>? progress, CancellationToken token)
    {
        var job = store.GetUploads().Single(j => j.Id == id);
        WorkspaceSession Session() => session() ?? throw new IOException("服务器未连接，请重新连接后继续。");
        void Report() { store.SaveUpload(job); progress?.Report(job); }
        try
        {
            job = job with { Status = "准备上传", Error = null }; Report();
            var entries = new List<(StoredEntry Entry, string Local)>();
            var root = job.Spec.Kind == ResourceKind.Folder ? job.SourcePath : Path.GetDirectoryName(job.SourcePath)!;
            async Task Add(string local, bool directory)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, local).Replace('\\', '/'); StoredPaths.Resolve(root, relative);
                if (directory) { entries.Add((new(relative, 0, "", true, Directory.GetLastWriteTimeUtc(local)), local)); return; }
                var before = new FileInfo(local); var size = before.Length; var modified = before.LastWriteTimeUtc;
                await using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                before.Refresh(); if (before.Length != size || before.LastWriteTimeUtc != modified) throw new IOException("源文件在准备期间发生变化。");
                entries.Add((new(relative, size, hash, false, modified), local));
            }
            async Task Walk(string directory)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("上传目录中包含符号链接：" + Path.GetFileName(path));
                    var folder = (attributes & FileAttributes.Directory) != 0; await Add(path, folder); if (folder) await Walk(path);
                }
            }
            if (job.Spec.Kind == ResourceKind.Folder) await Walk(root); else await Add(job.SourcePath, false);
            if (job.Spec.Kind == ResourceKind.File && job.Spec.Name.Equals("ResourceManager.exe", StringComparison.OrdinalIgnoreCase))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(job.SourcePath);
                if (info.FileMajorPart >= 0 && info.FileVersion is not null) job = job with { Spec = job.Spec with { Update = new(job.Id, $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}", entries[0].Entry.Size, entries[0].Entry.Sha256, entries[0].Entry.Modified) } };
            }
            job = job with { Status = "上传中", Total = entries.Sum(e => e.Entry.Size), Sent = 0 }; Report();
            var started = await client.BeginUploadAsync(binding, Session(), job.Spec, token).ConfigureAwait(false);
            if (!started.Complete)
            {
                foreach (var (entry, local) in entries)
                {
                    var state = await client.DeclareEntryAsync(binding, Session(), job.Id, entry, token).ConfigureAwait(false);
                    if (!entry.Directory && !state.Complete)
                    {
                        await using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, WorkspaceResourceRules.ChunkSize, true);
                        if (stream.Length != entry.Size) throw new IOException("源文件已变化，请删除任务后重新上传。");
                        stream.Position = state.Offset; var baseSent = job.Sent; job = job with { Sent = baseSent + state.Offset }; Report();
                        while (stream.Position < stream.Length)
                        {
                            var offset = stream.Position; var bytes = new byte[(int)Math.Min(WorkspaceResourceRules.ChunkSize, stream.Length - offset)]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                            await client.UploadChunkAsync(binding, Session(), job.Id, entry.Path, offset, bytes, token).ConfigureAwait(false);
                            job = job with { Sent = baseSent + stream.Position }; Report();
                        }
                        await client.CompleteEntryAsync(binding, Session(), job.Id, entry.Path, token).ConfigureAwait(false);
                    }
                    else { job = job with { Sent = job.Sent + entry.Size }; Report(); }
                }
                await client.CompleteUploadAsync(binding, Session(), job.Id, entries.Count, token).ConfigureAwait(false);
            }
            job = job with { Status = "已完成", Sent = job.Total }; Report();
        }
        catch (OperationCanceledException) { job = job with { Status = "已暂停" }; Report(); throw; }
        catch (Exception ex) { job = job with { Status = "已中断", Error = ex.Message }; Report(); throw; }
    }
}

public sealed partial class NodeStore
{
    public void SaveUpload(UploadJob job) { using var db = Open(); using var cmd = Cmd(db, "INSERT OR REPLACE INTO server_uploads VALUES($id,$json)", "$id", job.Id, "$json", JsonSerializer.Serialize(job, WorkspaceProtocol.Json)); cmd.ExecuteNonQuery(); }
    public IReadOnlyList<UploadJob> GetUploads() { using var db = Open(); using var cmd = Cmd(db, "SELECT json FROM server_uploads ORDER BY rowid DESC"); using var reader = cmd.ExecuteReader(); var items = new List<UploadJob>(); while (reader.Read()) items.Add(JsonSerializer.Deserialize<UploadJob>(reader.GetString(0), WorkspaceProtocol.Json)!); return items; }
    public void RemoveUpload(string id) { using var db = Open(); using var cmd = Cmd(db, "DELETE FROM server_uploads WHERE id=$id", "$id", id); cmd.ExecuteNonQuery(); }
    public void SaveServerFavorite(Favorite favorite) { using var db = Open(); using var cmd = Cmd(db, "INSERT OR REPLACE INTO server_favorites VALUES($server,$owner,$resource,$mode,$json)", "$server", favorite.ServerId, "$owner", favorite.PeerId, "$resource", favorite.ResourceId, "$mode", favorite.ServerStored ? 1 : 0, "$json", JsonSerializer.Serialize(favorite, WorkspaceProtocol.Json)); cmd.ExecuteNonQuery(); }
    public IReadOnlyList<Favorite> GetServerFavorites() { using var db = Open(); using var cmd = Cmd(db, "SELECT json FROM server_favorites"); using var reader = cmd.ExecuteReader(); var items = new List<Favorite>(); while (reader.Read()) items.Add(JsonSerializer.Deserialize<Favorite>(reader.GetString(0), WorkspaceProtocol.Json)!); return items; }
    public void RemoveServerFavorite(Favorite f) { using var db = Open(); using var cmd = Cmd(db, "DELETE FROM server_favorites WHERE server=$server AND owner=$owner AND resource=$resource AND mode=$mode", "$server", f.ServerId, "$owner", f.PeerId, "$resource", f.ResourceId, "$mode", f.ServerStored ? 1 : 0); cmd.ExecuteNonQuery(); }
}
