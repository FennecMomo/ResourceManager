using System.Text.Json;

namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    public IReadOnlyList<ServerBinding> GetServerBindings()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = Cmd(db, "SELECT json FROM server_bindings ORDER BY id"); using var reader = cmd.ExecuteReader();
            var result = new List<ServerBinding>(); while (reader.Read()) result.Add(JsonSerializer.Deserialize<ServerBinding>(reader.GetString(0), WorkspaceProtocol.Json)!);
            return result;
        }
    }
    public void SaveServerBinding(ServerBinding binding)
    {
        binding = binding with { Address = WorkspaceProtocol.NormalizeAddress(binding.Address), Name = binding.Name.Trim() };
        if (binding.Name.Length is < 1 or > 80) throw new ArgumentException("服务器名称须为 1–80 个字符。");
        lock (gate)
        {
            if (GetServerBindings().Any(s => s.Id != binding.Id && (s.Address == binding.Address || s.ServerId == binding.ServerId))) throw new ArgumentException("这台服务器已经添加。");
            using var db = Open(); using var cmd = Cmd(db, "INSERT INTO server_bindings(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json", "$id", binding.Id, "$json", JsonSerializer.Serialize(binding, WorkspaceProtocol.Json)); cmd.ExecuteNonQuery();
        }
    }
    public void RemoveServerBinding(string id)
    {
        lock (gate) { using var db = Open(); using var cmd = Cmd(db, "DELETE FROM server_bindings WHERE id=$id", "$id", id); cmd.ExecuteNonQuery(); }
    }
}
