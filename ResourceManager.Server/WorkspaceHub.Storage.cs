using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Core;

namespace ResourceManager.Server;

public sealed partial class WorkspaceHub
{
    // A fixed lock pool serializes each resource's writes, verification, reads and deletion without unbounded lock allocation.
    private readonly SemaphoreSlim[] storageLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private string UploadRoot => Path.GetFullPath(options.UploadDirectory ?? Path.Combine(store.DataDirectory, "uploads"));
    private string StoragePath(string id, bool complete) => StoredPaths.Resolve(UploadRoot, (complete ? "ready/" : "pending/") + id);
    private static void ValidId(string id) { if (id is null || id.Length != 34 || !id.StartsWith("s-") || !Guid.TryParseExact(id[2..], "N", out _)) throw new WorkspaceException("资源标识无效。", HttpStatusCode.BadRequest); }
    private async Task<T> StorageAction<T>(string id, Func<Task<T>> action, CancellationToken token)
    {
        ValidId(id); var mutex = storageLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(id) % (uint)storageLocks.Length)]; await mutex.WaitAsync(token);
        try { return await action(); } finally { mutex.Release(); }
    }
    private static void ValidatePermission(StoredPermission permission)
    {
        if (permission.Access is not (GroupAccess.Public or GroupAccess.Private or GroupAccess.AllowList) || permission.Allowed is null || permission.Allowed.Length > 1000 || permission.Allowed.Any(id => !Guid.TryParse(id, out _))) throw new WorkspaceException("权限设置无效。", HttpStatusCode.BadRequest);
    }
    private (string Owner, StoredResourceSpec Spec, bool Complete) ReadStored(string id)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT owner,json,complete FROM stored_resources WHERE id=$id", "$id", id); using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new WorkspaceException("上传资源已删除或不存在。", HttpStatusCode.NotFound);
        if (r.GetInt32(2) == -1) throw new WorkspaceException("资源正在删除，请重试删除操作。", HttpStatusCode.Gone);
        return (r.GetString(0), JsonSerializer.Deserialize<StoredResourceSpec>(r.GetString(1), WorkspaceProtocol.Json)!, r.GetInt32(2) == 1);
    }
    private (StoredEntry Entry, bool Complete)? ReadEntry(string id, string path)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT json,complete FROM stored_entries WHERE resource=$id AND path=$path COLLATE NOCASE", "$id", id, "$path", path); using var r = cmd.ExecuteReader();
        return r.Read() ? (JsonSerializer.Deserialize<StoredEntry>(r.GetString(0), WorkspaceProtocol.Json)!, r.GetInt32(1) == 1) : null;
    }
    private StoredEntry[] Entries(string id)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT json FROM stored_entries WHERE resource=$id ORDER BY path", "$id", id); using var r = cmd.ExecuteReader(); var result = new List<StoredEntry>(); while (r.Read()) result.Add(JsonSerializer.Deserialize<StoredEntry>(r.GetString(0), WorkspaceProtocol.Json)!); return result.ToArray();
    }
    private void Own(string token, string device, string id, bool writing = true)
    {
        lock (gate) Authenticate(token, device, false);
        var item = ReadStored(id);
        if (item.Owner != device) throw new WorkspaceException("只能修改自己上传的资源。", HttpStatusCode.Forbidden);
        if (writing && item.Complete) throw new WorkspaceException("资源已发布，请新建上传任务。", HttpStatusCode.Conflict);
    }
    public void Audit(string action, string device, string resource, string outcome)
    {
        device ??= ""; resource ??= "";
        try
        {
            using var db = Open(); using var cmd = Command(db, "INSERT INTO audit(time,action,device,resource,outcome) VALUES($time,$action,$device,$resource,$outcome)", "$time", DateTimeOffset.UtcNow.ToString("O"), "$action", action, "$device", device.Length > 100 ? device[..100] : device, "$resource", resource.Length > 100 ? resource[..100] : resource, "$outcome", outcome); cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        { Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, code = "AUDIT_WRITE_FAILED", action, error = ex.GetType().Name })); }
    }
    public object[] AdminStoredResources()
    {
        using var db = Open(); using var cmd = Command(db, "SELECT id,owner,json,complete FROM stored_resources ORDER BY owner,id"); using var r = cmd.ExecuteReader(); var items = new List<object>();
        while (r.Read()) items.Add(new { id = r.GetString(0), owner = r.GetString(1), resource = JsonSerializer.Deserialize<StoredResourceSpec>(r.GetString(2), WorkspaceProtocol.Json), state = r.GetInt32(3) == 1 ? "published" : r.GetInt32(3) == -1 ? "deleting" : "uploading" });
        return items.ToArray();
    }
    public object[] ReadAudit(long before = long.MaxValue)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT id,time,action,device,resource,outcome FROM audit WHERE id<$before ORDER BY id DESC LIMIT 200", "$before", before); using var r = cmd.ExecuteReader(); var result = new List<object>(); while (r.Read()) result.Add(new { id = r.GetInt64(0), time = r.GetString(1), action = r.GetString(2), device = r.GetString(3), resource = r.GetString(4), outcome = r.GetString(5) }); return result.ToArray();
    }
    public Task<StoredUploadState> GetStoredState(string token, string device, string id, CancellationToken t) => StorageAction(id, () => { Own(token, device, id, false); var item = ReadStored(id); return Task.FromResult(new StoredUploadState(item.Spec, item.Complete)); }, t);
    public Task<StoredUploadState> BeginStored(string token, string device, StoredResourceSpec spec, CancellationToken cancellation) => StorageAction(spec.Id, () =>
    {
        lock (gate) Authenticate(token, device, false);
        ValidatePermission(new(spec.Access, spec.Allowed));
        if (!StoredPaths.Valid(spec.Name) || spec.Name.Contains('/') || spec.Note is null || spec.Note.Length > 200 || !Enum.IsDefined(spec.Kind)) throw new WorkspaceException("资源资料无效。", HttpStatusCode.BadRequest);
        if (spec.Update is { } update)
        {
            if (spec.Kind != ResourceKind.File || !spec.Name.Equals("ResourceManager.exe", StringComparison.OrdinalIgnoreCase) || update.ResourceId != spec.Id || update.Version is null || update.Version.Length > 50 || !Version.TryParse(update.Version, out _) || update.Size <= 0 || update.Sha256?.Length != 64 || !update.Sha256.All(Uri.IsHexDigit)) throw new WorkspaceException("更新包元数据无效。", HttpStatusCode.BadRequest);
        }
        using var db = Open(); using var find = Command(db, "SELECT owner,json,complete FROM stored_resources WHERE id=$id", "$id", spec.Id);
        using (var r = find.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != device) throw new WorkspaceException("资源标识已被占用。", HttpStatusCode.Conflict);
                var existing = JsonSerializer.Deserialize<StoredResourceSpec>(r.GetString(1), WorkspaceProtocol.Json)!;
                if (JsonSerializer.Serialize(existing, WorkspaceProtocol.Json) != JsonSerializer.Serialize(spec, WorkspaceProtocol.Json)) throw new WorkspaceException("源资源或权限已变化，请删除任务后重新上传。", HttpStatusCode.Conflict);
                var complete = r.GetInt32(2);
                if (complete == -1) throw new WorkspaceException("资源正在删除，请重试删除操作。", HttpStatusCode.Gone);
                r.Close();
                if (complete == 0 && Directory.Exists(StoragePath(spec.Id, true)) && !Directory.Exists(StoragePath(spec.Id, false)))
                {
                    using var recover = Command(db, "UPDATE stored_resources SET complete=1 WHERE id=$id AND NOT EXISTS(SELECT 1 FROM stored_entries WHERE resource=$id AND complete=0)", "$id", spec.Id);
                    if (recover.ExecuteNonQuery() != 1) throw new IOException("上传提交恢复失败。");
                    complete = 1; Audit("upload.recover", device, spec.Id, "OK"); lock (gate) Signal();
                }
                return Task.FromResult(new StoredUploadState(existing, complete == 1));
            }
        }
        Directory.CreateDirectory(StoragePath(spec.Id, false));
        using var save = Command(db, "INSERT INTO stored_resources(id,owner,json,complete) VALUES($id,$owner,$json,0)", "$id", spec.Id, "$owner", device, "$json", JsonSerializer.Serialize(spec, WorkspaceProtocol.Json)); save.ExecuteNonQuery();
        Audit("upload.begin", device, spec.Id, "OK"); return Task.FromResult(new StoredUploadState(spec, false));
    }, cancellation);
    public Task<StoredEntryProgress> DeclareStored(string token, string device, string id, StoredEntry entry, CancellationToken cancellation) => StorageAction(id, () =>
    {
        Own(token, device, id);
        if (!StoredPaths.Valid(entry.Path) || entry.Size < 0 || entry.Directory && entry.Size != 0 || !entry.Directory && (entry.Sha256?.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))) throw new WorkspaceException("文件清单无效。", HttpStatusCode.BadRequest);
        var item = ReadStored(id);
        if (item.Spec.Kind == ResourceKind.File && (entry.Directory || entry.Path != item.Spec.Name)) throw new WorkspaceException("单文件资源清单无效。", HttpStatusCode.BadRequest);
        var path = StoredPaths.Resolve(StoragePath(id, false), entry.Path);
        var previous = ReadEntry(id, entry.Path);
        if (previous is { } old)
        {
            if (old.Entry != entry) throw new WorkspaceException("文件已变化或路径大小写冲突，请重新上传。", HttpStatusCode.Conflict);
            return Task.FromResult(new StoredEntryProgress(entry.Directory ? 0 : new FileInfo(path).Length, old.Complete));
        }
        // Check manifest ancestors on all platforms; case-insensitive names must remain portable to Windows clients.
        var pieces = entry.Path.Split('/');
        for (var i = 1; i < pieces.Length; i++)
        {
            var parent = string.Join('/', pieces.Take(i)); var declared = ReadEntry(id, parent);
            if (declared is null || !declared.Value.Entry.Directory || declared.Value.Entry.Path != parent) throw new WorkspaceException("父目录尚未声明或路径冲突。", HttpStatusCode.Conflict);
        }
        if (entry.Directory) Directory.CreateDirectory(path);
        else { using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); }
        using var db = Open(); using var save = Command(db, "INSERT INTO stored_entries(resource,path,json,complete) VALUES($id,$path,$json,$complete)", "$id", id, "$path", entry.Path, "$json", JsonSerializer.Serialize(entry, WorkspaceProtocol.Json), "$complete", entry.Directory ? 1 : 0); save.ExecuteNonQuery();
        return Task.FromResult(new StoredEntryProgress(0, entry.Directory));
    }, cancellation);
    public Task<StoredEntryProgress> WriteStored(string token, string device, string id, string path, long offset, byte[] bytes, CancellationToken cancellation) => StorageAction(id, async () =>
    {
        Own(token, device, id); var entry = ReadEntry(id, path) ?? throw new WorkspaceException("文件未声明。", HttpStatusCode.NotFound);
        if (entry.Entry.Directory || entry.Complete || offset < 0 || bytes.Length > WorkspaceResourceRules.ChunkSize || offset > entry.Entry.Size || bytes.Length > entry.Entry.Size - offset) throw new WorkspaceException("分片范围无效。", HttpStatusCode.BadRequest);
        var target = StoredPaths.Resolve(StoragePath(id, false), entry.Entry.Path);
        await using var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None, 65536, true);
        if (stream.Length != offset) throw new WorkspaceException("续传位置已变化，请继续任务以重新读取进度。", HttpStatusCode.Conflict);
        stream.Position = offset; await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation); return new StoredEntryProgress(stream.Position, false);
    }, cancellation);
    public Task<bool> VerifyStored(string token, string device, string id, string path, CancellationToken cancellation) => StorageAction(id, async () =>
    {
        Own(token, device, id); var item = ReadEntry(id, path) ?? throw new WorkspaceException("文件不存在。", HttpStatusCode.NotFound);
        if (item.Complete) return true;
        var target = StoredPaths.Resolve(StoragePath(id, false), item.Entry.Path);
        bool valid;
        await using (var file = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true)) valid = file.Length == item.Entry.Size && Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).Equals(item.Entry.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            using (var reset = new FileStream(target, FileMode.Truncate)) { }
            Audit("upload.verify", device, id, "HASH_MISMATCH"); throw new WorkspaceException("文件校验失败，损坏文件已重置，可继续重传。", HttpStatusCode.Conflict);
        }
        using var db = Open(); using var save = Command(db, "UPDATE stored_entries SET complete=1 WHERE resource=$id AND path=$path COLLATE NOCASE", "$id", id, "$path", path); save.ExecuteNonQuery(); return true;
    }, cancellation);
    public Task<bool> CommitStored(string token, string device, string id, int count, CancellationToken cancellation) => StorageAction(id, () =>
    {
        Own(token, device, id, false); var item = ReadStored(id); if (item.Complete) return Task.FromResult(true);
        using var db = Open(); using var check = Command(db, "SELECT COUNT(*),COALESCE(SUM(CASE WHEN complete=0 THEN 1 ELSE 0 END),0) FROM stored_entries WHERE resource=$id", "$id", id);
        using (var r = check.ExecuteReader()) { r.Read(); if (count < 0 || r.GetInt64(0) != count || r.GetInt64(1) != 0 || item.Spec.Kind == ResourceKind.File && count != 1) throw new WorkspaceException("上传尚未完整，不能发布。", HttpStatusCode.Conflict); }
        var pending = StoragePath(id, false); var ready = StoragePath(id, true); Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
        // Recover the narrow crash window between rename and database publication by retrying commit.
        if (Directory.Exists(pending)) Directory.Move(pending, ready);
        if (!Directory.Exists(ready)) throw new IOException("上传目录丢失。");
        using var save = Command(db, "UPDATE stored_resources SET complete=1 WHERE id=$id", "$id", id); save.ExecuteNonQuery();
        Audit("upload.commit", device, id, "OK"); lock (gate) Signal(); return Task.FromResult(true);
    }, cancellation);
    public Task<bool> DeleteStored(string token, string device, string id, bool admin, CancellationToken cancellation) => StorageAction(id, () =>
    {
        if (!admin) { lock (gate) Authenticate(token, device, false); }
        using var db = Open(); using var exists = Command(db, "SELECT owner FROM stored_resources WHERE id=$id", "$id", id); var owner = exists.ExecuteScalar() as string;
        if (owner is null) return Task.FromResult(true);
        if (!admin && owner != device) throw new WorkspaceException("只能删除自己的资源。", HttpStatusCode.Forbidden);
        // Hide immediately, then remove bytes. A failed deletion remains hidden and can be retried.
        using (var hide = Command(db, "UPDATE stored_resources SET complete=-1 WHERE id=$id", "$id", id)) hide.ExecuteNonQuery();
        lock (gate) Signal();
        foreach (var complete in new[] { false, true }) { var path = StoragePath(id, complete); if (Directory.Exists(path)) Directory.Delete(path, true); }
        using var tx = db.BeginTransaction();
        foreach (var table in new[] { "stored_entries", "stored_resources" }) { using var remove = Command(db, "DELETE FROM " + table + " WHERE " + (table == "stored_entries" ? "resource" : "id") + "=$id", "$id", id); remove.Transaction = tx; remove.ExecuteNonQuery(); } tx.Commit();
        Audit("resource.delete", admin ? "admin" : device, id, "OK"); return Task.FromResult(true);
    }, cancellation);
    public Task<bool> ChangeStoredPermission(string token, string device, string id, StoredPermission permission, CancellationToken cancellation) => StorageAction(id, () =>
    {
        Own(token, device, id, false); ValidatePermission(permission); var item = ReadStored(id);
        using var db = Open(); using var cmd = Command(db, "UPDATE stored_resources SET json=$json WHERE id=$id", "$id", id, "$json", JsonSerializer.Serialize(item.Spec with { Access = permission.Access, Allowed = permission.Allowed }, WorkspaceProtocol.Json)); cmd.ExecuteNonQuery();
        Audit("resource.permissions", device, id, "OK"); lock (gate) Signal(); return Task.FromResult(true);
    }, cancellation);
    private WorkspacePublishedResource[] StoredCatalog(string owner)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT id,json FROM stored_resources WHERE owner=$owner AND complete=1", "$owner", owner); using var r = cmd.ExecuteReader(); var result = new List<WorkspacePublishedResource>();
        while (r.Read())
        {
            var spec = JsonSerializer.Deserialize<StoredResourceSpec>(r.GetString(1), WorkspaceProtocol.Json)!; var entries = Entries(spec.Id); var total = entries.Sum(e => e.Size); var modified = entries.Select(e => e.Modified).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max();
            var update = spec.Update;
            if (spec.Kind != ResourceKind.File || !spec.Name.Equals("ResourceManager.exe", StringComparison.OrdinalIgnoreCase) || entries.Length != 1 || update?.ResourceId != spec.Id || update.Size != total || !update.Sha256.Equals(entries[0].Sha256, StringComparison.OrdinalIgnoreCase)) update = null;
            result.Add(new(new(spec.Id, spec.Name, spec.Kind, PublishMode.Copy, total, modified, true, spec.Note, "server-storage", true), spec.Access, spec.Allowed, update));
        }
        return result.ToArray();
    }
    public Task<WorkspaceResourceReply> ReadStoredResource(string token, string device, WorkspaceResourceRequest request, CancellationToken cancellation) => StorageAction(request.ResourceId, async () =>
    {
        lock (gate)
        {
            Authenticate(token, device, false); var member = Snapshot().Members.First(m => m.Profile.DeviceId == device);
            if (request.Requester != device || request.ServerId != store.ServerId || request.Path is null || request.Nonce is null || request.Signature is null || !WorkspaceResourceRules.Valid(request, member.PublicKey!)) throw new WorkspaceException("读取签名无效。", HttpStatusCode.Forbidden);
        }
        var item = ReadStored(request.ResourceId);
        if (!item.Complete || !Snapshot().Members.Any(m => m.Profile.DeviceId == item.Owner)) return new(404, []);
        if (item.Owner != request.Owner || item.Owner != device && item.Spec.Access != GroupAccess.Public && !(item.Spec.Access == GroupAccess.AllowList && item.Spec.Allowed.Contains(device))) return new(403, []);
        if (request.Operation == "describe") return WorkspaceResourceRules.Json(StoredCatalog(item.Owner).Single(r => r.Resource.Id == request.ResourceId).Resource);
        if (request.Operation == "tree") return WorkspaceResourceRules.Json(Entries(request.ResourceId).Select(e => new RemoteFile(item.Spec.Kind == ResourceKind.File ? "" : e.Path, e.Size, e.Modified, e.Directory)).ToArray());
        var relative = item.Spec.Kind == ResourceKind.File && request.Path == "" ? item.Spec.Name : request.Path;
        var entry = ReadEntry(request.ResourceId, relative)?.Entry; if (entry is null || entry.Directory || entry.Path != relative) return new(404, []);
        var metadata = new WorkspaceFileMetadata(entry.Size, "\"" + entry.Sha256 + "\"", entry.Sha256);
        if (request.Operation == "meta") return WorkspaceResourceRules.Json(metadata);
        if (request.Etag != metadata.Etag) return new(409, []);
        if (request.Offset > entry.Size) return new(416, []);
        var path = StoredPaths.Resolve(StoragePath(request.ResourceId, true), entry.Path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); file.Position = request.Offset;
        var bytes = new byte[(int)Math.Min(WorkspaceResourceRules.ChunkSize, entry.Size - request.Offset)]; await file.ReadExactlyAsync(bytes, cancellation); return new(200, bytes);
    }, cancellation);
}

public static partial class WorkspaceEndpoints
{
    private static void MapWorkspaceStorage(this WebApplication app)
    {
        app.MapGet("/api/v1/workspace/storage/uploads/{id}", (string id, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => Results.Ok(await h.GetStoredState(Token(c), Device(c), id, t))));
        app.MapPut("/api/v1/workspace/storage/uploads/{id}", (string id, StoredResourceSpec spec, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => id == spec.Id ? Results.Ok(await h.BeginStored(Token(c), Device(c), spec, t)) : Results.BadRequest()));
        app.MapPut("/api/v1/workspace/storage/uploads/{id}/entry", (string id, StoredEntry entry, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => Results.Ok(await h.DeclareStored(Token(c), Device(c), id, entry, t))));
        app.MapPut("/api/v1/workspace/storage/uploads/{id}/chunk", (string id, string path, long offset, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () =>
        {
            var bytes = new byte[WorkspaceResourceRules.ChunkSize + 1]; var count = 0;
            while (count < bytes.Length) { var read = await c.Request.Body.ReadAsync(bytes.AsMemory(count), t); if (read == 0) break; count += read; }
            if (count > WorkspaceResourceRules.ChunkSize) return Results.StatusCode(413);
            return Results.Ok(await h.WriteStored(Token(c), Device(c), id, path, offset, bytes[..count], t));
        }));
        app.MapPost("/api/v1/workspace/storage/uploads/{id}/verify", (string id, string path, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => Results.Ok(await h.VerifyStored(Token(c), Device(c), id, path, t))));
        app.MapPost("/api/v1/workspace/storage/uploads/{id}/commit", (string id, int count, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => Results.Ok(await h.CommitStored(Token(c), Device(c), id, count, t))));
        app.MapDelete("/api/v1/workspace/storage/uploads/{id}", (string id, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => Results.Ok(await h.DeleteStored(Token(c), Device(c), id, false, t))));
        app.MapPatch("/api/v1/workspace/storage/uploads/{id}/permissions", (string id, StoredPermission permission, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, id, async () => Results.Ok(await h.ChangeStoredPermission(Token(c), Device(c), id, permission, t))));
        app.MapPost("/api/v1/workspace/storage/read", (WorkspaceResourceRequest request, HttpContext c, WorkspaceHub h, CancellationToken t) => StorageResult(h, c, request.ResourceId, async () =>
        {
            var reply = await h.ReadStoredResource(Token(c), Device(c), request, t);
            if (reply.Status >= 400) h.Audit("download.denied", Device(c), request.ResourceId, reply.Status.ToString());
            return Results.Ok(reply);
        }));
        // Defence in depth: these endpoints also require the actual admin listener, even in custom test hosts.
        app.MapGet("/admin/api/workspace/resources", (HttpContext c, WorkspaceHub h, ServerRuntimeOptions o) => Admin(c, o) ? Results.Ok(h.AdminStoredResources()) : Results.StatusCode(403));
        app.MapGet("/admin/api/workspace/audit", (long? before, HttpContext c, WorkspaceHub h, ServerRuntimeOptions o) => Admin(c, o) ? Results.Ok(h.ReadAudit(before ?? long.MaxValue)) : Results.StatusCode(403));
        app.MapDelete("/admin/api/workspace/resources/{id}", (string id, HttpContext c, WorkspaceHub h, ServerRuntimeOptions o, CancellationToken t) => StorageResult(h, c, id, async () => Admin(c, o) ? Results.Ok(await h.DeleteStored("", "admin", id, true, t)) : Results.StatusCode(403)));
    }
    private static bool Admin(HttpContext c, ServerRuntimeOptions o) => c.Connection.LocalPort == o.AdminPort && c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    private static async Task<IResult> StorageResult(WorkspaceHub hub, HttpContext context, string id, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (WorkspaceException ex) { hub.Audit("storage.denied", Device(context), id, ((int)ex.Code).ToString()); return Results.Json(new { error = ex.Message }, statusCode: (int)ex.Code); }
        catch (ArgumentException) { return Results.Json(new { error = "路径或参数无效。", code = "INVALID_PATH" }, statusCode: 400); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { hub.Audit("storage.error", Device(context), id, "STORAGE_IO_ERROR"); return Results.Json(new { error = "服务器磁盘不足或文件读写失败；任务已保留，请检查存储后重试。", code = "STORAGE_IO_ERROR" }, statusCode: 507); }
    }
}
