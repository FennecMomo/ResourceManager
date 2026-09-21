using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ResourceManager.Core;

public sealed class NodeStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    public string DataDirectory { get; }
    public string LibraryDirectory => Path.Combine(DataDirectory, "library");

    public NodeStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? NodeDefaults.DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "resources.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS peers (device_id TEXT PRIMARY KEY, ip TEXT NOT NULL, port INTEGER NOT NULL, nickname TEXT NOT NULL, avatar BLOB, last_seen TEXT);
            CREATE TABLE IF NOT EXISTS resources (id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, mode TEXT NOT NULL, source_path TEXT NOT NULL, published_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS favorites (peer_id TEXT NOT NULL, resource_id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, PRIMARY KEY(peer_id, resource_id));
            CREATE TABLE IF NOT EXISTS downloads (id TEXT PRIMARY KEY, peer_id TEXT NOT NULL, resource_id TEXT NOT NULL, resource_name TEXT NOT NULL, kind TEXT NOT NULL, target_path TEXT NOT NULL, status TEXT NOT NULL, downloaded_bytes INTEGER NOT NULL, total_bytes INTEGER NOT NULL, error TEXT);
            """;
        command.ExecuteNonQuery();
        if (GetSetting("device_id") is null) SetSetting("device_id", Guid.NewGuid().ToString("N"));
        if (GetSetting("nickname") is null) SetSetting("nickname", Environment.UserName);
        if (GetSetting("port") is null) SetSetting("port", NodeDefaults.Port.ToString(CultureInfo.InvariantCulture));
        if (GetSetting("close_to_tray") is null) SetSetting("close_to_tray", "1");
        if (GetSetting("auto_update") is null) SetSetting("auto_update", "0");
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Cmd(SqliteConnection db, string sql, params object?[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2)
            command.Parameters.AddWithValue((string)args[i]!, args[i + 1] ?? DBNull.Value);
        return command;
    }

    private string? GetSetting(string key)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT value FROM settings WHERE key=$key", "$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    private void SetSetting(string key, string value)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", "$key", key, "$value", value);
            command.ExecuteNonQuery();
        }
    }

    public AppSettings GetSettings()
    {
        var avatarPath = Path.Combine(DataDirectory, "avatar.png");
        var profile = new NodeProfile(GetSetting("device_id")!, GetSetting("nickname")!, File.Exists(avatarPath) ? File.ReadAllBytes(avatarPath) : null);
        return new AppSettings(profile, int.Parse(GetSetting("port")!, CultureInfo.InvariantCulture), GetSetting("close_to_tray") == "1", GetSetting("auto_update") == "1");
    }

    public void SaveSettings(string nickname, byte[]? avatar, int port, bool closeToTray)
        => SaveSettings(nickname, avatar, port, closeToTray, GetSetting("auto_update") == "1");

    public void SaveSettings(string nickname, byte[]? avatar, int port, bool closeToTray, bool autoUpdate)
    {
        nickname = nickname.Trim();
        if (nickname.Length is < 1 or > 80) throw new ArgumentException("昵称须为 1 至 80 个字符。");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (avatar?.Length > 512 * 1024) throw new ArgumentException("头像文件过大。");
        SetSetting("nickname", nickname);
        SetSetting("port", port.ToString(CultureInfo.InvariantCulture));
        SetSetting("close_to_tray", closeToTray ? "1" : "0");
        SetSetting("auto_update", autoUpdate ? "1" : "0");
        var path = Path.Combine(DataDirectory, "avatar.png");
        if (avatar is null) { if (File.Exists(path)) File.Delete(path); }
        else File.WriteAllBytes(path, avatar);
    }

    public IReadOnlyList<PeerInfo> GetPeers()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT device_id,ip,port,nickname,avatar,last_seen FROM peers ORDER BY nickname COLLATE NOCASE");
            using var reader = command.ExecuteReader();
            var result = new List<PeerInfo>();
            while (reader.Read()) result.Add(new PeerInfo(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : (byte[])reader[4], reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            return result;
        }
    }

    public PeerInfo? GetPeer(string deviceId) => GetPeers().FirstOrDefault(p => p.DeviceId == deviceId);
    public bool IsKnownIp(string ip) => GetPeers().Any(p => p.Ip == ip);

    public void UpsertPeer(PeerInfo peer)
    {
        if (peer.DeviceId.Length > 100 || peer.Nickname.Length > 80 || peer.Avatar?.Length > 512 * 1024 || peer.Port is < 1 or > 65535)
            throw new ArgumentException("设备资料无效。");
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO peers(device_id,ip,port,nickname,avatar,last_seen) VALUES($id,$ip,$port,$name,$avatar,$seen)
                ON CONFLICT(device_id) DO UPDATE SET ip=excluded.ip,port=excluded.port,nickname=excluded.nickname,avatar=excluded.avatar,last_seen=excluded.last_seen
                """, "$id", peer.DeviceId, "$ip", peer.Ip, "$port", peer.Port, "$name", peer.Nickname, "$avatar", peer.Avatar, "$seen", peer.LastSeenUtc?.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void UpdatePeerAddress(string deviceId, string ip, int port)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE peers SET ip=$ip,port=$port WHERE device_id=$id", "$ip", ip, "$port", port, "$id", deviceId);
            command.ExecuteNonQuery();
        }
    }

    public void RemovePeer(string deviceId)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM favorites WHERE peer_id=$id", "DELETE FROM peers WHERE device_id=$id" })
            {
                using var command = Cmd(db, sql, "$id", deviceId);
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public LocalResource AddResource(string sourcePath, PublishMode mode)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var kind = File.Exists(sourcePath) ? ResourceKind.File : Directory.Exists(sourcePath) ? ResourceKind.Folder : throw new FileNotFoundException("资源路径不存在。", sourcePath);
        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
        var storedPath = sourcePath;
        if (mode == PublishMode.Copy)
        {
            var root = Path.Combine(LibraryDirectory, id);
            storedPath = Path.Combine(root, name);
            Directory.CreateDirectory(root);
            try
            {
                if (kind == ResourceKind.File) File.Copy(sourcePath, storedPath);
                else CopyDirectory(sourcePath, storedPath);
            }
            catch { Directory.Delete(root, true); throw; }
        }
        var resource = new LocalResource(id, name, kind, mode, storedPath, DateTimeOffset.UtcNow);
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "INSERT INTO resources(id,name,kind,mode,source_path,published_utc) VALUES($id,$name,$kind,$mode,$path,$time)", "$id", id, "$name", name, "$kind", kind.ToString(), "$mode", mode.ToString(), "$path", storedPath, "$time", resource.PublishedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return resource;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyDirectory(entry, destination);
            else File.Copy(entry, destination);
        }
    }

    public IReadOnlyList<LocalResource> GetResources()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT id,name,kind,mode,source_path,published_utc FROM resources ORDER BY published_utc DESC");
            using var reader = command.ExecuteReader();
            var result = new List<LocalResource>();
            while (reader.Read()) result.Add(new LocalResource(reader.GetString(0), reader.GetString(1), Enum.Parse<ResourceKind>(reader.GetString(2)), Enum.Parse<PublishMode>(reader.GetString(3)), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            return result;
        }
    }

    public LocalResource? GetResource(string id) => GetResources().FirstOrDefault(r => r.Id == id);

    public void RemoveResource(string id)
    {
        LocalResource? resource;
        lock (gate)
        {
            resource = GetResource(id);
            if (resource is null) return;
            using var db = Open();
            using var command = Cmd(db, "DELETE FROM resources WHERE id=$id", "$id", id);
            command.ExecuteNonQuery();
        }
        if (resource.Mode == PublishMode.Copy)
        {
            var root = Path.Combine(LibraryDirectory, id);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public IReadOnlyList<Favorite> GetFavorites()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT peer_id,resource_id,name,kind FROM favorites ORDER BY name COLLATE NOCASE");
            using var reader = command.ExecuteReader();
            var result = new List<Favorite>();
            while (reader.Read()) result.Add(new Favorite(reader.GetString(0), reader.GetString(1), reader.GetString(2), Enum.Parse<ResourceKind>(reader.GetString(3))));
            return result;
        }
    }

    public void SaveFavorite(Favorite favorite)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "INSERT INTO favorites(peer_id,resource_id,name,kind) VALUES($peer,$id,$name,$kind) ON CONFLICT(peer_id,resource_id) DO UPDATE SET name=excluded.name,kind=excluded.kind", "$peer", favorite.PeerId, "$id", favorite.ResourceId, "$name", favorite.Name, "$kind", favorite.Kind.ToString());
            command.ExecuteNonQuery();
        }
    }

    public void RemoveFavorite(string peerId, string resourceId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "DELETE FROM favorites WHERE peer_id=$peer AND resource_id=$id", "$peer", peerId, "$id", resourceId);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<DownloadJob> GetDownloads()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT id,peer_id,resource_id,resource_name,kind,target_path,status,downloaded_bytes,total_bytes,error FROM downloads ORDER BY rowid DESC");
            using var reader = command.ExecuteReader();
            var result = new List<DownloadJob>();
            while (reader.Read()) result.Add(new DownloadJob(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Enum.Parse<ResourceKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            return result;
        }
    }

    public DownloadJob? GetDownload(string id) => GetDownloads().FirstOrDefault(d => d.Id == id);

    public void SaveDownload(DownloadJob job)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO downloads(id,peer_id,resource_id,resource_name,kind,target_path,status,downloaded_bytes,total_bytes,error)
                VALUES($id,$peer,$resource,$name,$kind,$target,$status,$done,$total,$error)
                ON CONFLICT(id) DO UPDATE SET status=excluded.status,downloaded_bytes=excluded.downloaded_bytes,total_bytes=excluded.total_bytes,error=excluded.error
                """, "$id", job.Id, "$peer", job.PeerId, "$resource", job.ResourceId, "$name", job.ResourceName, "$kind", job.Kind.ToString(), "$target", job.TargetPath, "$status", job.Status, "$done", job.DownloadedBytes, "$total", job.TotalBytes, "$error", job.Error);
            command.ExecuteNonQuery();
        }
    }
}
