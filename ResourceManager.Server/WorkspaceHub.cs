using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.Core;

namespace ResourceManager.Server;

public sealed class WorkspaceHub : BackgroundService
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly ServerStore store;
    private readonly ServerRuntimeOptions options;
    private readonly byte[] signingKey;
    private string cursor = Guid.NewGuid().ToString("N");
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, Queue<DateTimeOffset>> registrations = [];
    public WorkspaceHub(ServerStore store, ServerRuntimeOptions options)
    {
        this.store = store; this.options = options;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(store.DataDirectory, "workspace.db"), Pooling = false }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS members(id TEXT PRIMARY KEY,public_key TEXT NOT NULL,profile TEXT NOT NULL,token_hash TEXT NOT NULL,expires INTEGER NOT NULL,seen INTEGER NOT NULL,online INTEGER NOT NULL,blocked INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS member_tokens ON members(token_hash) WHERE token_hash!='';
            CREATE TABLE IF NOT EXISTS nonces(value TEXT PRIMARY KEY,time INTEGER NOT NULL);
            UPDATE members SET online=0;
            """; cmd.ExecuteNonQuery();
        var key = store.GetSetting("workspace_signing_key");
        signingKey = key is null ? WorkspaceProtocol.CreateKey() : Convert.FromBase64String(key);
        if (key is null) store.SetSetting("workspace_signing_key", Convert.ToBase64String(signingKey));
    }
    public WorkspaceCapabilities Capabilities => new(WorkspaceProtocol.Protocol, store.ServerId, store.ServerName, options.Version, WorkspaceProtocol.PublicKey(signingKey));
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static SqliteCommand Command(SqliteConnection db, string sql, params object[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2) cmd.Parameters.AddWithValue((string)args[i], args[i + 1]); return cmd;
    }
    private void Signal() { cursor = Guid.NewGuid().ToString("N"); var old = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); old.TrySetResult(); }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public WorkspaceSession Join(WorkspaceRegistration request, string source)
    {
        var now = DateTimeOffset.UtcNow; var p = request.Profile;
        if (p is null || !Guid.TryParse(p.DeviceId, out _) || string.IsNullOrWhiteSpace(p.Nickname) || p.Nickname.Length > 80 ||
            p.Avatar?.Length > 512 * 1024 || p.Version is null || p.Version.Length > 30 || request.PublicKey is null || request.Nonce is null || request.Signature is null ||
            request.Nonce.Length != 48 || request.Timestamp < now.ToUnixTimeSeconds() - 300 || request.Timestamp > now.ToUnixTimeSeconds() + 300 ||
            !WorkspaceProtocol.Verify(request.PublicKey, request.Signature, WorkspaceProtocol.RegistrationBytes(store.ServerId, request)))
            throw new WorkspaceException("登记资料或签名无效。", HttpStatusCode.BadRequest);
        lock (gate)
        {
            foreach (var old in registrations.Where(x => x.Value.Count == 0 || x.Value.Last() < now.AddMinutes(-1)).Select(x => x.Key).ToArray()) registrations.Remove(old);
            if (!registrations.TryGetValue(source, out var queue)) registrations[source] = queue = new();
            while (queue.TryPeek(out var old) && old < now.AddMinutes(-1)) queue.Dequeue();
            if (queue.Count >= 30 || registrations.Count > 10000) throw new WorkspaceException("登记过于频繁。", HttpStatusCode.TooManyRequests);
            queue.Enqueue(now);
            using var db = Open(); using var transaction = db.BeginTransaction();
            using var existing = Command(db, "SELECT public_key,blocked FROM members WHERE id=$id", "$id", p.DeviceId); existing.Transaction = transaction;
            var exists = false;
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read()) { exists = true; if (reader.GetInt32(1) != 0) throw new WorkspaceException("设备已停用。", HttpStatusCode.Forbidden); if (reader.GetString(0) != request.PublicKey) throw new WorkspaceException("设备身份密钥不匹配。", HttpStatusCode.Conflict); }
            }
            using var count = Command(db, "SELECT COUNT(*) FROM members"); count.Transaction = transaction;
            if (!exists && Convert.ToInt64(count.ExecuteScalar()) >= options.MaxDevices) throw new WorkspaceException("设备容量已满。", HttpStatusCode.TooManyRequests);
            using var clean = Command(db, "DELETE FROM nonces WHERE time < $time", "$time", now.ToUnixTimeSeconds() - 300); clean.Transaction = transaction; clean.ExecuteNonQuery();
            using var nonce = Command(db, "INSERT OR IGNORE INTO nonces(value,time) VALUES($value,$time)", "$value", p.DeviceId + ":" + request.Nonce, "$time", request.Timestamp); nonce.Transaction = transaction;
            if (nonce.ExecuteNonQuery() == 0) throw new WorkspaceException("重复登记请求。", HttpStatusCode.Conflict);
            var session = new WorkspaceSession(store.ServerId, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now.AddMinutes(30), request.Nonce, "");
            // Bound roster size; large source avatars use the default device icon.
            var profile = p with { Avatar = p.Avatar?.Length <= 32 * 1024 ? p.Avatar : null };
            using var save = Command(db, """
                INSERT INTO members(id,public_key,profile,token_hash,expires,seen,online) VALUES($id,$key,$profile,$token,$expires,$seen,1)
                ON CONFLICT(id) DO UPDATE SET profile=excluded.profile,token_hash=excluded.token_hash,expires=excluded.expires,seen=excluded.seen,online=1
                """, "$id", p.DeviceId, "$key", request.PublicKey, "$profile", JsonSerializer.Serialize(profile, WorkspaceProtocol.Json), "$token", Hash(session.Token), "$expires", session.ExpiresUtc.ToUnixTimeSeconds(), "$seen", now.ToUnixTimeSeconds()); save.Transaction = transaction; save.ExecuteNonQuery(); transaction.Commit();
            Signal(); return session with { Signature = WorkspaceProtocol.Sign(signingKey, WorkspaceProtocol.SessionBytes(session)) };
        }
    }

    private string Authenticate(string token, string device, bool touch)
    {
        if (token.Length != 64 || !Guid.TryParse(device, out _)) throw new WorkspaceException("凭据无效。", HttpStatusCode.Unauthorized);
        using var db = Open(); using var cmd = Command(db, "SELECT token_hash,expires,blocked,online FROM members WHERE id=$id", "$id", device);
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new WorkspaceException("设备不存在。", HttpStatusCode.Unauthorized);
            if (reader.GetInt32(2) != 0) throw new WorkspaceException("设备已停用。", HttpStatusCode.Forbidden);
            if (reader.GetString(0) != Hash(token)) throw new WorkspaceException("连接已被替换。", HttpStatusCode.Conflict);
            if (reader.GetInt64(1) < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new WorkspaceException("凭据已过期。", HttpStatusCode.Unauthorized);
            if (touch && reader.GetInt32(3) == 0) Signal();
        }
        if (touch)
        {
            using var update = Command(db, "UPDATE members SET seen=$seen,expires=$expires,online=1 WHERE id=$id", "$seen", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "$expires", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), "$id", device); update.ExecuteNonQuery();
        }
        return device;
    }

    public async Task<WorkspaceSnapshot> WatchAsync(string token, string device, string? lastCursor, CancellationToken cancellation)
    {
        Task wait;
        lock (gate) { Authenticate(token, device, true); wait = cursor == lastCursor ? changed.Task : Task.CompletedTask; }
        try { await wait.WaitAsync(TimeSpan.FromSeconds(20), cancellation); } catch (TimeoutException) { }
        lock (gate) { Authenticate(token, device, true); return Snapshot(); }
    }
    public WorkspaceSnapshot Snapshot()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = Command(db, "SELECT profile,online,seen FROM members WHERE blocked=0 ORDER BY id"); using var reader = cmd.ExecuteReader(); var list = new List<WorkspaceMember>();
            while (reader.Read()) list.Add(new(JsonSerializer.Deserialize<WorkspaceProfile>(reader.GetString(0), WorkspaceProtocol.Json)!, reader.GetInt32(1) != 0, DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))));
            return new(cursor, list);
        }
    }
    public void Leave(string token, string device)
    {
        lock (gate) { Authenticate(token, device, false); using var db = Open(); using var cmd = Command(db, "UPDATE members SET online=0,expires=0 WHERE id=$id", "$id", device); cmd.ExecuteNonQuery(); Signal(); }
    }
    public void SetBlocked(string device, bool blocked)
    {
        lock (gate) { using var db = Open(); using var cmd = Command(db, "UPDATE members SET blocked=$blocked,online=0,expires=0 WHERE id=$id", "$id", device, "$blocked", blocked ? 1 : 0); if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("设备不存在。"); Signal(); }
    }
    public object AdminMembers()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = Command(db, "SELECT profile,online,seen,blocked FROM members ORDER BY id"); using var reader = cmd.ExecuteReader();
            var members = new List<object>();
            while (reader.Read()) members.Add(new { profile = JsonSerializer.Deserialize<WorkspaceProfile>(reader.GetString(0), WorkspaceProtocol.Json), online = reader.GetInt32(1) != 0, lastSeenUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)), blocked = reader.GetInt32(3) != 0 });
            return new { cursor, members };
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            lock (gate) { using var db = Open(); using var cmd = Command(db, "UPDATE members SET online=0 WHERE online=1 AND seen<$cutoff", "$cutoff", DateTimeOffset.UtcNow.AddSeconds(-50).ToUnixTimeSeconds()); if (cmd.ExecuteNonQuery() > 0) Signal(); }
    }
}

public static class WorkspaceEndpoints
{
    public static void MapWorkspace(this WebApplication app)
    {
        app.MapGet("/api/v1/workspace/capabilities", (WorkspaceHub hub) => Results.Ok(hub.Capabilities));
        app.MapPost("/api/v1/workspace/join", (WorkspaceRegistration request, HttpContext context, WorkspaceHub hub) =>
            Execute(() => Results.Ok(hub.Join(request, context.Connection.RemoteIpAddress?.ToString() ?? "unknown"))));
        app.MapGet("/api/v1/workspace/members", async (string? cursor, HttpContext context, WorkspaceHub hub, CancellationToken token) =>
        {
            try { return Results.Ok(await hub.WatchAsync(Token(context), Device(context), cursor, token)); }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.Code); }
        });
        app.MapPost("/api/v1/workspace/leave", (HttpContext context, WorkspaceHub hub) => Execute(() => { hub.Leave(Token(context), Device(context)); return Results.Ok(); }));
        app.MapGet("/admin/api/workspace/members", (WorkspaceHub hub) => Results.Ok(hub.AdminMembers()));
        app.MapPatch("/admin/api/workspace/members/{id}/blocked", (string id, BlockRequest request, WorkspaceHub hub) => Execute(() => { hub.SetBlocked(id, request.Blocked); return Results.Ok(); }));
    }
    private static string Token(HttpContext context) => context.Request.Headers.Authorization.ToString() is { } value && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : "";
    private static string Device(HttpContext context) => context.Request.Headers["X-RM-Device"].ToString();
    private static IResult Execute(Func<IResult> action) { try { return action(); } catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.Code); } catch (KeyNotFoundException) { return Results.NotFound(); } }
    public sealed record BlockRequest(bool Blocked);
}
