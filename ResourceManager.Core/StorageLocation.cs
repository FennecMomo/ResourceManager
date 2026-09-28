using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ResourceManager.Core;

public sealed record StorageMigration(string Source, string Target, string Stage, string Id);
public sealed record StorageConfiguration(string Directory, bool Initialized = false,
    StorageMigration? Pending = null, string? PreviousDirectory = null);
public sealed record StorageUsage(long Total, long Available, long ProgramBytes)
{
    public long Used => Total - Available;
    public bool LowSpace => Available < Math.Max(512L * 1024 * 1024, Total / 20);
}

/// <summary>Bootstrap configuration is separate from movable data. Migration runs before any stores or services open.</summary>
public sealed class StorageLocation(string configurationPath)
{
    private const string MarkerName = ".storage-migration";
    private sealed record VerifiedFile(string Path, long Length, string Hash);
    private sealed record MigrationManifest(string Id, VerifiedFile[] Files);
    public static string DefaultConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceManager.bootstrap", "storage.json");
    public StorageConfiguration? Read() => File.Exists(configurationPath)
        ? JsonSerializer.Deserialize<StorageConfiguration>(File.ReadAllText(configurationPath))
            ?? throw new InvalidDataException("存储位置配置为空，请恢复配置后重试。") : null;

    public void Save(StorageConfiguration configuration)
    {
        if (IsWithin(configurationPath, configuration.Directory))
            throw new IOException("存储目录不能包含程序的引导配置目录。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configurationPath))!);
        var temporary = configurationPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, configuration);
            stream.Flush(true);
        }
        File.Move(temporary, configurationPath, true);
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("请选择完整的本地文件夹路径。");
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (result.StartsWith(@"\\", StringComparison.Ordinal) || result == Path.GetPathRoot(result))
            throw new IOException("请选择本地磁盘中的独立文件夹，不能直接使用磁盘根目录。");
        // Reject junctions/symlinks in every existing ancestor, including the root.
        for (var current = new DirectoryInfo(result); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存储位置不能经过符号链接或目录联接。");
        return result;
    }

    public static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void ValidateWritable(string path, bool mustExist = false)
    {
        path = Normalize(path);
        if (mustExist && !Directory.Exists(path)) throw new DirectoryNotFoundException("存储位置不可用，请连接原磁盘后重试。");
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.WriteByte(42);
                stream.Flush(true);
                stream.Position = 0;
                if (stream.ReadByte() != 42) throw new IOException("存储目录读写检查失败。");
            }
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    public static void ValidateCurrent(StorageConfiguration configuration)
    {
        ValidateWritable(configuration.Directory, mustExist: true);
        if (configuration.Initialized && !File.Exists(Path.Combine(configuration.Directory, "resources.db")))
            throw new IOException("原数据库不存在。为避免生成空资料，已停止启动，请恢复原数据或重新选择已有数据目录。");
        ValidateDatabases(configuration.Directory);
        if (configuration.Initialized)
        {
            using var db = OpenDatabase(Path.Combine(configuration.Directory, "resources.db"));
            using var command = db.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key='device_id'";
            if (command.ExecuteScalar() is not string deviceId || !Guid.TryParse(deviceId, out _))
                throw new IOException("数据库缺少原设备身份，已停止启动，请恢复原资料。");
        }
    }

    public StorageConfiguration ScheduleMigration(StorageConfiguration configuration, string target)
    {
        var source = Normalize(configuration.Directory);
        target = Normalize(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || IsWithin(source, target) || IsWithin(target, source))
            throw new IOException("新旧存储位置不能相同，也不能互相包含。");
        if (IsWithin(configurationPath, target) || IsWithin(configurationPath, source))
            throw new IOException("存储目录不能包含程序的引导配置目录。");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("迁移目标必须是空文件夹，避免覆盖已有文件。");
        ValidateWritable(target);
        var id = Guid.NewGuid().ToString("N");
        var migration = new StorageMigration(source, target, target + ".rm-migration-" + id, id);
        var updated = configuration with { Pending = migration };
        Save(updated);
        return updated;
    }

    public async Task<StorageConfiguration> CompleteMigrationAsync(StorageConfiguration configuration,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var migration = configuration.Pending ?? throw new InvalidOperationException("没有待迁移的数据。");
        ValidateMigration(configuration, migration);
        var marker = Path.Combine(migration.Target, MarkerName);
        if (File.Exists(marker))
        {
            // Atomic directory rename completed before a crash; finish the pointer switch.
            await VerifyManifestAsync(migration.Target, migration.Id, token);
            ValidateCurrent(configuration with { Directory = migration.Target });
            return Commit(configuration, migration);
        }
        if (Directory.Exists(migration.Target) && Directory.EnumerateFileSystemEntries(migration.Target).Any())
            throw new IOException("目标文件夹已出现其他文件，迁移已停止，原数据保持不变。");
        if (File.Exists(Path.Combine(migration.Stage, MarkerName)))
        {
            await VerifyManifestAsync(migration.Stage, migration.Id, token);
            ValidateCurrent(configuration with { Directory = migration.Stage });
            if (Directory.Exists(migration.Target)) Directory.Delete(migration.Target, false);
            Directory.Move(migration.Stage, migration.Target);
            return Commit(configuration, migration);
        }
        ValidateCurrent(configuration);
        progress?.Report("正在统计迁移空间…");
        var files = PayloadFiles(migration.Source, token).ToArray();
        if (Directory.Exists(migration.Stage)) _ = EnumerateFiles(migration.Stage, token).Count();
        var required = files.Sum(file =>
        {
            var staged = Path.Combine(migration.Stage, Path.GetRelativePath(migration.Source, file));
            return Math.Max(0, new FileInfo(file).Length - (File.Exists(staged) ? new FileInfo(staged).Length : 0));
        });
        EnsureSpace(migration.Target, required);
        Directory.CreateDirectory(migration.Stage);
        foreach (var directory in EnumerateDirectories(migration.Source, token))
            Directory.CreateDirectory(Path.Combine(migration.Stage, Path.GetRelativePath(migration.Source, directory)));
        var index = 0;
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(migration.Source, file);
            index++;
            if (index == 1 || index == files.Length || lastReport.ElapsedMilliseconds >= 100)
            {
                progress?.Report($"正在复制并校验 {index}/{files.Length}：{relative}");
                lastReport.Restart();
            }
            var destination = Path.Combine(migration.Stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = File.OpenRead(file))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                await input.CopyToAsync(output, token);
                output.Flush(true);
            }
            if (new FileInfo(file).Length != new FileInfo(destination).Length ||
                await HashAsync(file, token) != await HashAsync(destination, token))
                throw new IOException($"文件校验失败：{relative}");
        }
        if (PayloadFiles(migration.Stage, token).Count() != files.Length)
            throw new IOException("暂存目录文件数量不一致，请保留原目录并选择新的空目录重试。");
        progress?.Report("正在校验数据库和调整内部路径…");
        ValidateDatabases(migration.Stage);
        RewritePaths(migration.Stage, migration.Source, migration.Target);
        ValidateDatabases(migration.Stage);
        var verifiedFiles = new List<VerifiedFile>();
        foreach (var file in PayloadFiles(migration.Stage, token))
            verifiedFiles.Add(new VerifiedFile(Path.GetRelativePath(migration.Stage, file), new FileInfo(file).Length, await HashAsync(file, token)));
        using (var markerStream = new FileStream(Path.Combine(migration.Stage, MarkerName), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(markerStream, new MigrationManifest(migration.Id, verifiedFiles.ToArray()));
            markerStream.Flush(true);
        }
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(migration.Target)) Directory.Delete(migration.Target, false);
        Directory.Move(migration.Stage, migration.Target);
        return Commit(configuration, migration);
    }

    private void ValidateMigration(StorageConfiguration configuration, StorageMigration migration)
    {
        var source = Normalize(migration.Source);
        var target = Normalize(migration.Target);
        var stage = Normalize(migration.Stage);
        if (!Guid.TryParseExact(migration.Id, "N", out _) ||
            !source.Equals(Normalize(configuration.Directory), StringComparison.OrdinalIgnoreCase) ||
            !stage.Equals(target + ".rm-migration-" + migration.Id, StringComparison.OrdinalIgnoreCase) ||
            source.Equals(target, StringComparison.OrdinalIgnoreCase) || IsWithin(source, target) || IsWithin(target, source) ||
            IsWithin(stage, source) || IsWithin(source, stage) || source.Equals(stage, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(configurationPath, source) || IsWithin(configurationPath, target))
            throw new IOException("迁移配置中的路径关系无效，已停止操作。");
    }

    private static IEnumerable<string> PayloadFiles(string root, CancellationToken token) =>
        EnumerateFiles(root, token).Where(path => !path.Equals(Path.Combine(root, MarkerName), StringComparison.OrdinalIgnoreCase));

    private static async Task VerifyManifestAsync(string root, string id, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(Path.Combine(root, MarkerName)));
        if (manifest?.Id != id || manifest.Files is null) throw new IOException("迁移恢复标记无效，原目录仍然保留。");
        var files = PayloadFiles(root, token).ToDictionary(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase);
        if (files.Count != manifest.Files.Length) throw new IOException("迁移恢复时文件数量不符。");
        foreach (var item in manifest.Files)
        {
            if (!files.TryGetValue(item.Path, out var path) || new FileInfo(path).Length != item.Length ||
                await HashAsync(path, token) != item.Hash) throw new IOException("迁移恢复时文件校验失败。");
        }
    }

    private StorageConfiguration Commit(StorageConfiguration configuration, StorageMigration migration)
    {
        var updated = configuration with { Directory = migration.Target, Pending = null, PreviousDirectory = migration.Source };
        Save(updated);
        return updated;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static SqliteConnection OpenDatabase(string file)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static void ValidateDatabases(string root)
    {
        foreach (var name in new[] { "resources.db", "feedback.db" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            using var db = OpenDatabase(path);
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            if (!Equals(command.ExecuteScalar(), "ok")) throw new IOException($"数据库校验失败：{name}");
        }
    }

    private static string Remap(string path, string source, string target) =>
        IsWithin(path, source) ? Path.Combine(target, Path.GetRelativePath(source, path)) : path;

    private static void RewritePaths(string stage, string source, string target)
    {
        foreach (var (database, table, column, filter) in new[]
        {
            ("resources.db", "resources", "source_path", "mode='Copy'"),
            ("feedback.db", "draft_attachments", "staged_path", "1=1")
        })
        {
            var file = Path.Combine(stage, database);
            if (!File.Exists(file)) continue;
            using var db = OpenDatabase(file);
            using var transaction = db.BeginTransaction();
            using var query = db.CreateCommand();
            query.CommandText = $"SELECT rowid,{column} FROM {table} WHERE {filter}";
            var rows = new List<(long Id, string Path)>();
            using (var reader = query.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
            foreach (var row in rows)
            {
                using var update = db.CreateCommand();
                update.CommandText = $"UPDATE {table} SET {column}=$path WHERE rowid=$id";
                update.Parameters.AddWithValue("$path", Remap(row.Path, source, target));
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            using var checkpoint = db.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }
        var stateFile = Path.Combine(stage, "git-collaboration", "state.json");
        if (File.Exists(stateFile))
        {
            var state = JsonNode.Parse(File.ReadAllText(stateFile)) ?? throw new InvalidDataException("Git 协作资料为空。");
            if (state["Bindings"] is JsonArray bindings)
                foreach (var binding in bindings)
                    if (binding?["RepositoryPath"]?.GetValue<string>() is string path)
                        binding["RepositoryPath"] = Remap(path, source, target);
            File.WriteAllText(stateFile, state.ToJsonString());
        }
    }

    public static IEnumerable<string> EnumerateDirectories(string root, CancellationToken token = default)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"目录含有链接，无法安全迁移或统计：{directory}");
            yield return directory;
            foreach (var child in EnumerateDirectories(directory, token)) yield return child;
        }
    }

    public static IEnumerable<string> EnumerateFiles(string root, CancellationToken token = default)
    {
        foreach (var directory in new[] { root }.Concat(EnumerateDirectories(root, token)))
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"文件含有链接，无法安全迁移或统计：{file}");
                yield return file;
            }
    }

    public static StorageUsage Measure(string path, CancellationToken token = default)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        var bytes = EnumerateFiles(path, token).Sum(file =>
        {
            try { return new FileInfo(file).Length; }
            catch (FileNotFoundException) { return 0L; }
        });
        return new StorageUsage(drive.TotalSize, drive.AvailableFreeSpace, bytes);
    }

    public static void EnsureSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (bytes < 0 || bytes > drive.AvailableFreeSpace - 32L * 1024 * 1024)
            throw new IOException("磁盘剩余空间不足，请先清理空间或选择其他磁盘。");
    }
}
