using System.Globalization;
using System.Text;
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
            CREATE TABLE IF NOT EXISTS peer_notes (device_id TEXT PRIMARY KEY, note TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS gateways (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, wan_ip TEXT NOT NULL UNIQUE, router_udn TEXT,
                port_start INTEGER NOT NULL, port_end INTEGER NOT NULL, last_refresh TEXT, last_status TEXT);
            CREATE TABLE IF NOT EXISTS peer_endpoints (
                device_id TEXT NOT NULL, ip TEXT NOT NULL, port INTEGER NOT NULL, kind TEXT NOT NULL,
                gateway_id TEXT, source TEXT NOT NULL, last_success TEXT,
                PRIMARY KEY(device_id, ip, port));
            CREATE INDEX IF NOT EXISTS ix_peer_endpoints_gateway ON peer_endpoints(gateway_id, last_success);
            CREATE TABLE IF NOT EXISTS local_port_mappings (
                router_udn TEXT PRIMARY KEY, router_name TEXT NOT NULL, lan_ip TEXT NOT NULL, wan_ip TEXT NOT NULL,
                external_port INTEGER NOT NULL, internal_port INTEGER NOT NULL, lease_seconds INTEGER NOT NULL,
                last_verified TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS resources (id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, mode TEXT NOT NULL, source_path TEXT NOT NULL, published_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS favorites (peer_id TEXT NOT NULL, resource_id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, PRIMARY KEY(peer_id, resource_id));
            CREATE TABLE IF NOT EXISTS downloads (id TEXT PRIMARY KEY, peer_id TEXT NOT NULL, resource_id TEXT NOT NULL, resource_name TEXT NOT NULL, kind TEXT NOT NULL, target_path TEXT NOT NULL, status TEXT NOT NULL, downloaded_bytes INTEGER NOT NULL, total_bytes INTEGER NOT NULL, error TEXT);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(db, "resources", "note", "TEXT NOT NULL DEFAULT ''");
        using (var migrate = db.CreateCommand())
        {
            migrate.CommandText = """
                INSERT OR IGNORE INTO peer_endpoints(device_id,ip,port,kind,gateway_id,source,last_success)
                SELECT device_id,ip,port,'Direct',NULL,'Legacy',last_seen FROM peers;
                """;
            migrate.ExecuteNonQuery();
        }
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

    private static void EnsureColumn(SqliteConnection db, string table, string column, string definition)
    {
        using (var check = db.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        using var alter = Cmd(db, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        alter.ExecuteNonQuery();
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

    public void UpdatePeerProfile(PeerInfo peer)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                UPDATE peers SET ip=$ip,port=$port,nickname=$name,avatar=$avatar,last_seen=$seen
                WHERE device_id=$id
                """, "$ip", peer.Ip, "$port", peer.Port, "$name", peer.Nickname, "$avatar", peer.Avatar,
                "$seen", peer.LastSeenUtc?.ToString("O"), "$id", peer.DeviceId);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<string, string> GetPeerNotes()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT device_id,note FROM peer_notes");
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }
    }

    public string GetPeerNote(string deviceId) => GetPeerNotes().GetValueOrDefault(deviceId, "");

    public void SavePeerNote(string deviceId, string note)
    {
        if (deviceId.Length is < 1 or > 100) throw new ArgumentException("设备编号无效。");
        note = NormalizeNote(note, 100, "设备备注");
        lock (gate)
        {
            using var db = Open();
            using (var exists = Cmd(db, "SELECT 1 FROM peers WHERE device_id=$id LIMIT 1", "$id", deviceId))
            {
                if (exists.ExecuteScalar() is null) throw new InvalidOperationException("设备已不在本机列表中。");
            }
            if (note.Length == 0)
            {
                using var remove = Cmd(db, "DELETE FROM peer_notes WHERE device_id=$id", "$id", deviceId);
                remove.ExecuteNonQuery();
                return;
            }
            using var command = Cmd(db, """
                INSERT INTO peer_notes(device_id,note) VALUES($id,$note)
                ON CONFLICT(device_id) DO UPDATE SET note=excluded.note
                """, "$id", deviceId, "$note", note);
            command.ExecuteNonQuery();
        }
    }

    internal static string NormalizeNote(string note, int maxLength, string label)
    {
        var builder = new StringBuilder(note.Length);
        var pendingSpace = false;
        foreach (var ch in note)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(ch);
        }
        var normalized = builder.ToString();
        if (normalized.Length > maxLength) throw new ArgumentException($"{label}不能超过 {maxLength} 个字符。");
        return normalized;
    }

    public bool IsKnownIp(string ip)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT 1 FROM peer_endpoints WHERE ip=$ip LIMIT 1", "$ip", ip);
            return command.ExecuteScalar() is not null;
        }
    }

    public void UpsertPeer(PeerInfo peer, PeerEndpointKind endpointKind = PeerEndpointKind.Direct,
        string? gatewayId = null, string source = "Direct")
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
            using var endpoint = Cmd(db, """
                INSERT INTO peer_endpoints(device_id,ip,port,kind,gateway_id,source,last_success)
                VALUES($id,$ip,$port,$kind,$gateway,$source,$seen)
                ON CONFLICT(device_id,ip,port) DO UPDATE SET
                    kind=excluded.kind,gateway_id=excluded.gateway_id,source=excluded.source,last_success=excluded.last_success
                """, "$id", peer.DeviceId, "$ip", peer.Ip, "$port", peer.Port,
                "$kind", endpointKind.ToString(), "$gateway", gatewayId, "$source", source,
                "$seen", peer.LastSeenUtc?.ToString("O"));
            endpoint.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PeerEndpoint> GetPeerEndpoints(string? deviceId = null, string? gatewayId = null)
    {
        lock (gate)
        {
            using var db = Open();
            var clauses = new List<string>();
            if (deviceId is not null) clauses.Add("device_id=$device");
            if (gatewayId is not null) clauses.Add("gateway_id=$gateway");
            var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
            using var command = Cmd(db, $"SELECT device_id,ip,port,kind,gateway_id,source,last_success FROM peer_endpoints{where} ORDER BY last_success DESC",
                "$device", deviceId, "$gateway", gatewayId);
            using var reader = command.ExecuteReader();
            var result = new List<PeerEndpoint>();
            while (reader.Read())
                result.Add(new PeerEndpoint(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    Enum.Parse<PeerEndpointKind>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
            return result;
        }
    }

    public void UpsertPeerEndpoint(PeerEndpoint endpoint)
    {
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint));
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO peer_endpoints(device_id,ip,port,kind,gateway_id,source,last_success)
                VALUES($id,$ip,$port,$kind,$gateway,$source,$seen)
                ON CONFLICT(device_id,ip,port) DO UPDATE SET
                    kind=excluded.kind,gateway_id=excluded.gateway_id,source=excluded.source,last_success=excluded.last_success
                """, "$id", endpoint.DeviceId, "$ip", endpoint.Ip, "$port", endpoint.Port,
                "$kind", endpoint.Kind.ToString(), "$gateway", endpoint.GatewayId, "$source", endpoint.Source,
                "$seen", endpoint.LastSuccessUtc?.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void MarkEndpointDeviceChanged(PeerEndpoint endpoint)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                UPDATE peer_endpoints SET source='DeviceChanged',last_success=NULL
                WHERE device_id=$device AND ip=$ip AND port=$port
                """, "$device", endpoint.DeviceId, "$ip", endpoint.Ip, "$port", endpoint.Port);
            command.ExecuteNonQuery();
        }
    }

    public void TouchPeerEndpoint(string deviceId, string ip, int port)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                UPDATE peer_endpoints SET last_success=$seen
                WHERE device_id=$device AND ip=$ip AND port=$port
                """, "$seen", DateTimeOffset.UtcNow.ToString("O"), "$device", deviceId, "$ip", ip, "$port", port);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<GatewayInfo> GetGateways()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT id,name,wan_ip,router_udn,port_start,port_end,last_refresh,last_status FROM gateways ORDER BY name COLLATE NOCASE");
            using var reader = command.ExecuteReader();
            var result = new List<GatewayInfo>();
            while (reader.Read())
                result.Add(new GatewayInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            return result;
        }
    }

    public GatewayInfo SaveGateway(string name, string wanIp, string? existingId = null)
    {
        name = name.Trim();
        wanIp = wanIp.Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("路由器名称须为 1 至 80 个字符。");
        if (!System.Net.IPAddress.TryParse(wanIp, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("请输入有效的路由器 WAN IPv4 地址。");
        var id = existingId ?? Guid.NewGuid().ToString("N");
        lock (gate)
        {
            using var db = Open();
            using (var duplicate = Cmd(db, "SELECT 1 FROM gateways WHERE wan_ip=$ip AND id<>$id LIMIT 1",
                       "$ip", wanIp, "$id", id))
            {
                if (duplicate.ExecuteScalar() is not null)
                    throw new ArgumentException("这个路由器地址已经保存在其他入口中。");
            }
            using var command = Cmd(db, """
                INSERT INTO gateways(id,name,wan_ip,router_udn,port_start,port_end,last_refresh,last_status)
                VALUES($id,$name,$ip,NULL,$start,$end,NULL,'未检查')
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,wan_ip=excluded.wan_ip
                """, "$id", id, "$name", name, "$ip", wanIp, "$start", NodeDefaults.GatewayPortStart,
                "$end", NodeDefaults.GatewayPortEnd);
            command.ExecuteNonQuery();
        }
        return GetGateways().Single(item => item.Id == id);
    }

    public void SaveGatewayRefresh(string id, string? routerUdn, string status)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE gateways SET router_udn=COALESCE($udn,router_udn),last_refresh=$time,last_status=$status WHERE id=$id",
                "$udn", routerUdn, "$time", DateTimeOffset.UtcNow.ToString("O"), "$status", status, "$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveGateway(string id)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM peer_endpoints WHERE gateway_id=$id", "DELETE FROM gateways WHERE id=$id" })
            {
                using var command = Cmd(db, sql, "$id", id);
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<LocalPortMapping> GetLocalPortMappings()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT router_udn,router_name,lan_ip,wan_ip,external_port,internal_port,lease_seconds,last_verified FROM local_port_mappings ORDER BY router_name COLLATE NOCASE");
            using var reader = command.ExecuteReader();
            var result = new List<LocalPortMapping>();
            while (reader.Read())
                result.Add(new LocalPortMapping(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetInt32(5), checked((uint)reader.GetInt64(6)),
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
            return result;
        }
    }

    public void SaveLocalPortMapping(LocalPortMapping mapping)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO local_port_mappings(router_udn,router_name,lan_ip,wan_ip,external_port,internal_port,lease_seconds,last_verified)
                VALUES($udn,$name,$lan,$wan,$external,$internal,$lease,$seen)
                ON CONFLICT(router_udn) DO UPDATE SET router_name=excluded.router_name,lan_ip=excluded.lan_ip,
                    wan_ip=excluded.wan_ip,external_port=excluded.external_port,internal_port=excluded.internal_port,
                    lease_seconds=excluded.lease_seconds,last_verified=excluded.last_verified
                """, "$udn", mapping.RouterUdn, "$name", mapping.RouterName, "$lan", mapping.LanIp,
                "$wan", mapping.WanIp, "$external", mapping.ExternalPort, "$internal", mapping.InternalPort,
                "$lease", mapping.LeaseSeconds, "$seen", mapping.LastVerifiedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void RemoveLocalPortMapping(string routerUdn)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "DELETE FROM local_port_mappings WHERE router_udn=$udn", "$udn", routerUdn);
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
            using var endpoint = Cmd(db, """
                INSERT INTO peer_endpoints(device_id,ip,port,kind,gateway_id,source,last_success)
                VALUES($id,$ip,$port,'Direct',NULL,'Manual',$seen)
                ON CONFLICT(device_id,ip,port) DO UPDATE SET kind='Direct',gateway_id=NULL,source='Manual',last_success=excluded.last_success
                """, "$id", deviceId, "$ip", ip, "$port", port, "$seen", DateTimeOffset.UtcNow.ToString("O"));
            endpoint.ExecuteNonQuery();
        }
    }

    public void RemovePeer(string deviceId)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM favorites WHERE peer_id=$id", "DELETE FROM peer_notes WHERE device_id=$id", "DELETE FROM peer_endpoints WHERE device_id=$id", "DELETE FROM peers WHERE device_id=$id" })
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
            using var command = Cmd(db, "SELECT id,name,kind,mode,source_path,published_utc,note FROM resources ORDER BY published_utc DESC");
            using var reader = command.ExecuteReader();
            var result = new List<LocalResource>();
            while (reader.Read()) result.Add(new LocalResource(reader.GetString(0), reader.GetString(1), Enum.Parse<ResourceKind>(reader.GetString(2)), Enum.Parse<PublishMode>(reader.GetString(3)), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.IsDBNull(6) ? "" : reader.GetString(6)));
            return result;
        }
    }

    public LocalResource? GetResource(string id) => GetResources().FirstOrDefault(r => r.Id == id);

    public void SetResourceNote(string id, string note)
    {
        note = NormalizeNote(note, 200, "资源备注");
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE resources SET note=$note WHERE id=$id", "$note", note, "$id", id);
            if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("资源已撤销。");
        }
    }

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

    public void RemoveDownload(string id)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "DELETE FROM downloads WHERE id=$id", "$id", id);
            command.ExecuteNonQuery();
        }
    }
}
