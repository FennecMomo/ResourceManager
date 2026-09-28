namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    public void SetServerPublication(string server, string kind, string id, bool enabled)
    {
        if (kind is not ("group" or "resource") || !GetServerBindings().Any(b => b.Id == server)) throw new ArgumentException("服务器或发布类型无效。");
        lock (gate) { using var db = Open(); using var cmd = Cmd(db, "INSERT INTO server_publications VALUES($server,$kind,$id,$enabled) ON CONFLICT(server,kind,id) DO UPDATE SET enabled=$enabled", "$server", server, "$kind", kind, "$id", id, "$enabled", enabled ? 1 : 0); cmd.ExecuteNonQuery(); }
    }
    public bool IsServerPublished(string server, string kind, string id)
    {
        lock (gate)
        {
            using var db = Open();
            bool? Selected(string type, string key) { using var cmd = Cmd(db, "SELECT enabled FROM server_publications WHERE server=$server AND kind=$kind AND id=$id", "$server", server, "$kind", type, "$id", key); var value = cmd.ExecuteScalar(); return value is null ? null : Convert.ToInt32(value) != 0; }
            if (kind == "resource") { if (Selected(kind, id) is { } selection) return selection; id = GetResource(id)?.GroupId ?? ""; }
            var groups = GetResourceGroups().ToDictionary(g => g.Id); var seen = new HashSet<string>();
            while (groups.TryGetValue(id, out var group) && seen.Add(id)) { if (Selected("group", id) is { } selection) return selection; id = group.ParentId ?? ""; }
            return false;
        }
    }
    public IReadOnlyList<WorkspaceMember> GetWorkspaceDevices() => GetServerBindings().SelectMany(b => b.Cached?.Members ?? []).Where(m => m.PublicKey is not null).GroupBy(m => m.Profile.DeviceId).Where(g => g.Select(m => m.PublicKey).Distinct().Count() == 1).Select(g => g.First()).ToArray();
    public void SaveServerDownload(string job, string server, string ownerName, bool stored = false)
    { using var db = Open(); using var cmd = Cmd(db, "INSERT OR REPLACE INTO server_downloads (job,server,owner_name,stored) VALUES($job,$server,$name,$stored)", "$job", job, "$server", server, "$name", ownerName, "$stored", stored ? 1 : 0); cmd.ExecuteNonQuery(); }
    public (string Server, string OwnerName, bool Stored)? GetServerDownload(string job)
    { using var db = Open(); using var cmd = Cmd(db, "SELECT server,owner_name,stored FROM server_downloads WHERE job=$job", "$job", job); using var reader = cmd.ExecuteReader(); return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0) : null; }
}
