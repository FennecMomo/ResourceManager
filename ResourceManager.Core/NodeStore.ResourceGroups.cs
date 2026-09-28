using System.Globalization;

namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    public const string DefaultResourceGroupId = "default";

    public IReadOnlyList<ResourceGroup> GetResourceGroups()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT id,name,parent_id,sort_order,created_utc FROM resource_groups ORDER BY sort_order,name,id");
            using var reader = command.ExecuteReader();
            var groups = new List<ResourceGroup>();
            while (reader.Read()) groups.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            return groups;
        }
    }

    public ResourceGroup SaveResourceGroup(string name, string? parentId = null, string? id = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new ArgumentException("分组名称须为 1–80 个字符，不能包含斜线或换行。");
        lock (gate)
        {
            var groups = GetResourceGroups();
            if (id == DefaultResourceGroupId) throw new InvalidOperationException("默认组不能重命名或移动。");
            var existing = id is null ? null : groups.FirstOrDefault(g => g.Id == id) ?? throw new ArgumentException("分组不存在。");
            if (parentId is not null && !groups.Any(g => g.Id == parentId)) throw new ArgumentException("上级分组不存在。");
            if (groups.Any(g => g.Id != id && g.ParentId == parentId && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("同一级已经存在这个名称的分组。");
            var cursor = parentId;
            var visited = new HashSet<string>();
            while (cursor is not null)
            {
                if (cursor == id || !visited.Add(cursor)) throw new ArgumentException("分组不能移动到自身或子分组中。");
                cursor = groups.First(g => g.Id == cursor).ParentId;
            }
            var order = existing is not null && existing.ParentId == parentId ? existing.SortOrder
                : groups.Where(g => g.ParentId == parentId).Select(g => g.SortOrder).DefaultIfEmpty(-1).Max() + 1;
            var group = new ResourceGroup(id ?? Guid.NewGuid().ToString("N"), name, parentId, order, existing?.CreatedUtc ?? DateTimeOffset.UtcNow);
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO resource_groups(id,name,parent_id,sort_order,created_utc) VALUES($id,$name,$parent,$sort,$created)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,parent_id=excluded.parent_id,sort_order=excluded.sort_order
                """, "$id", group.Id, "$name", group.Name, "$parent", group.ParentId, "$sort", group.SortOrder, "$created", group.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
            return group;
        }
    }

    public void MoveResourceToGroup(string resourceId, string groupId)
    {
        lock (gate)
        {
            if (!GetResourceGroups().Any(g => g.Id == groupId)) throw new ArgumentException("目标分组不存在。");
            using var db = Open();
            using var command = Cmd(db, "UPDATE resources SET group_id=$group WHERE id=$id", "$group", groupId, "$id", resourceId);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("资源已撤销。");
        }
    }

    public IReadOnlySet<string> GetResourceGroupSubtree(string id)
    {
        var groups = GetResourceGroups();
        if (!groups.Any(g => g.Id == id)) throw new ArgumentException("分组不存在。");
        var result = new HashSet<string> { id };
        var queue = new Queue<string>(); queue.Enqueue(id);
        while (queue.TryDequeue(out var parent))
            foreach (var child in groups.Where(g => g.ParentId == parent))
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
        return result;
    }

    public void DeleteResourceGroup(string id, bool revokeResources)
    {
        if (id == DefaultResourceGroupId) throw new InvalidOperationException("默认组不能删除。");
        List<LocalResource> removed;
        lock (gate)
        {
            var ids = GetResourceGroupSubtree(id);
            removed = revokeResources ? GetResources().Where(r => ids.Contains(r.GroupId)).ToList() : [];
            using var db = Open();
            using var transaction = db.BeginTransaction();
            foreach (var groupId in ids)
            {
                using var resources = Cmd(db, revokeResources ? "DELETE FROM resources WHERE group_id=$id"
                    : "UPDATE resources SET group_id='default' WHERE group_id=$id", "$id", groupId);
                resources.Transaction = transaction; resources.ExecuteNonQuery();
                using var policy = Cmd(db, "DELETE FROM group_access WHERE group_id=$id; DELETE FROM group_allowlist WHERE group_id=$id", "$id", groupId);
                policy.Transaction = transaction; policy.ExecuteNonQuery();
                using var group = Cmd(db, "DELETE FROM resource_groups WHERE id=$id", "$id", groupId);
                group.Transaction = transaction; group.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        // Only managed copies are removed; reference sources and chat-private copies are never touched.
        foreach (var resource in removed.Where(r => r.Mode == PublishMode.Copy))
        {
            var root = Path.GetFullPath(Path.Combine(LibraryDirectory, resource.Id));
            if (!StorageLocation.IsWithin(root, LibraryDirectory)) throw new IOException("发布副本路径异常，未清理文件。");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
