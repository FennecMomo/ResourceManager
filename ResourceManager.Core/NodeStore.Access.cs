using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    private static void InitializeAccessTables(SqliteConnection db)
    {
        using var command = Cmd(db, """
            CREATE TABLE IF NOT EXISTS device_nonces(device_id TEXT NOT NULL,nonce TEXT NOT NULL,time INTEGER NOT NULL,PRIMARY KEY(device_id,nonce));
            CREATE TABLE IF NOT EXISTS device_trust(device_id TEXT PRIMARY KEY,public_key TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS group_access(group_id TEXT PRIMARY KEY,access TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS group_allowlist(group_id TEXT NOT NULL,device_id TEXT NOT NULL,PRIMARY KEY(group_id,device_id));
            CREATE TABLE IF NOT EXISTS resource_viewers(resource_id TEXT NOT NULL,peer_id TEXT NOT NULL,PRIMARY KEY(resource_id,peer_id));
            """);
        command.ExecuteNonQuery();
    }

    internal bool AcceptDeviceNonce(string device, string nonce, long time, long now)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            using var clean = Cmd(db, "DELETE FROM device_nonces WHERE time < $time", "$time", now - 300); clean.Transaction = tx; clean.ExecuteNonQuery();
            using var count = Cmd(db, "SELECT COUNT(*) FROM device_nonces"); count.Transaction = tx;
            if (Convert.ToInt64(count.ExecuteScalar()) >= 20000) return false;
            using var add = Cmd(db, "INSERT OR IGNORE INTO device_nonces(device_id,nonce,time) VALUES($id,$nonce,$time)", "$id", device, "$nonce", nonce, "$time", time); add.Transaction = tx;
            var accepted = add.ExecuteNonQuery() == 1; tx.Commit(); return accepted;
        }
    }

    internal byte[] GetDeviceSigningKey()
    {
        lock (gate)
        {
            var saved = GetSetting("device_signing_key_v1");
            if (saved is not null) return Convert.FromBase64String(saved);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var bytes = key.ExportPkcs8PrivateKey();
            SetSetting("device_signing_key_v1", Convert.ToBase64String(bytes));
            return bytes;
        }
    }

    internal string? GetTrustedDeviceKey(string id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = Cmd(db, "SELECT public_key FROM device_trust WHERE device_id=$id", "$id", id);
            return cmd.ExecuteScalar() as string;
        }
    }

    internal void TrustDeviceKey(string id, string publicKey)
    {
        lock (gate)
        {
            var existing = GetTrustedDeviceKey(id);
            if (existing is not null && existing != publicKey) throw new InvalidOperationException("设备身份密钥已变化，拒绝自动替换信任。");
            using var db = Open(); using var cmd = Cmd(db, "INSERT OR IGNORE INTO device_trust(device_id,public_key) VALUES($id,$key)", "$id", id, "$key", publicKey);
            cmd.ExecuteNonQuery();
        }
    }

    public GroupPermission GetGroupPermission(string id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = Cmd(db, "SELECT access FROM group_access WHERE group_id=$id", "$id", id);
            var mode = cmd.ExecuteScalar() as string;
            using var members = Cmd(db, "SELECT device_id FROM group_allowlist WHERE group_id=$id ORDER BY device_id", "$id", id);
            using var reader = members.ExecuteReader(); var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0));
            return new(id, mode is null ? GroupAccess.Inherit : Enum.Parse<GroupAccess>(mode), ids);
        }
    }

    public EffectiveGroupPermission GetEffectiveGroupPermission(string id)
    {
        var groups = GetResourceGroups().ToDictionary(g => g.Id); var visited = new HashSet<string>();
        while (groups.TryGetValue(id, out var group) && visited.Add(id))
        {
            var permission = GetGroupPermission(id);
            if (permission.Access != GroupAccess.Inherit) return new(id, permission.Access, permission.DeviceIds);
            if (group.ParentId is null) return new(id, GroupAccess.Public, []);
            id = group.ParentId;
        }
        return new(id, GroupAccess.Private, []);
    }

    public void SetGroupPermission(string id, GroupAccess access, IEnumerable<string> deviceIds)
    {
        if (!Enum.IsDefined(access)) throw new ArgumentException("无效的权限类型。");
        lock (gate)
        {
            if (!GetResourceGroups().Any(g => g.Id == id)) throw new ArgumentException("分组不存在。");
            var ids = access == GroupAccess.AllowList ? deviceIds.Distinct().ToArray() : [];
            if (ids.Any(peer => GetPeer(peer) is null)) throw new ArgumentException("白名单中有已移除的设备，请重新选择。");
            using var db = Open(); using var tx = db.BeginTransaction();
            using var mode = Cmd(db, "INSERT INTO group_access(group_id,access) VALUES($id,$mode) ON CONFLICT(group_id) DO UPDATE SET access=excluded.access", "$id", id, "$mode", access.ToString());
            mode.Transaction = tx; mode.ExecuteNonQuery();
            using var clear = Cmd(db, "DELETE FROM group_allowlist WHERE group_id=$id", "$id", id); clear.Transaction = tx; clear.ExecuteNonQuery();
            foreach (var peer in ids) { using var add = Cmd(db, "INSERT INTO group_allowlist(group_id,device_id) VALUES($id,$peer)", "$id", id, "$peer", peer); add.Transaction = tx; add.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public int CountAllowedGroups(string peerId)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT COUNT(*) FROM group_allowlist WHERE device_id=$id", "$id", peerId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool CanAccessGroup(string groupId, string? authenticatedPeer)
    {
        lock (gate)
        {
            var p = GetEffectiveGroupPermission(groupId);
            return p.Access == GroupAccess.Public || p.Access == GroupAccess.AllowList && authenticatedPeer is not null &&
                GetPeer(authenticatedPeer) is not null && p.DeviceIds.Contains(authenticatedPeer, StringComparer.Ordinal);
        }
    }

    internal ResourceTreeCatalog VisibleCatalog(string? peerId, ResourceCatalog catalog)
    {
        lock (gate)
        {
            var groups = GetResourceGroups().Where(g => CanAccessGroup(g.Id, peerId)).ToArray();
            var ids = groups.Select(g => g.Id).ToHashSet();
            // Explicitly public children of a hidden parent become roots; no hidden ancestor names are exposed.
            groups = groups.Select(g => g.ParentId is not null && !ids.Contains(g.ParentId) ? g with { ParentId = null } : g).ToArray();
            var resources = GetResources().Where(r => ids.Contains(r.GroupId)).Select(r => catalog.Describe(r)).ToArray();
            if (peerId is not null)
            {
                using var db = Open(); using var tx = db.BeginTransaction();
                foreach (var r in resources) { using var cmd = Cmd(db, "INSERT OR IGNORE INTO resource_viewers(resource_id,peer_id) VALUES($id,$peer)", "$id", r.Id, "$peer", peerId); cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return new(groups, resources);
        }
    }

    internal bool PreviouslySawResource(string id, string? peer)
    {
        if (peer is null) return false;
        using var db = Open(); using var cmd = Cmd(db, "SELECT 1 FROM resource_viewers WHERE resource_id=$id AND peer_id=$peer", "$id", id, "$peer", peer);
        return cmd.ExecuteScalar() is not null;
    }
}
