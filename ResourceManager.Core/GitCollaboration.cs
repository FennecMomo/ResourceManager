using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

public sealed record GitCommitPoint(string Hash, string[] Parents, string Subject, DateTimeOffset CommittedUtc);

public sealed record GitProjectEvent(
    string ProjectId, string ProjectName, string Branch, string MemberId, string DeviceId, string MemberName,
    long Sequence, string PreviousHash, string Kind, string CommitHash, string? SourceHash,
    DateTimeOffset CreatedUtc, GitCommitPoint[] Commits, string? BundleHash, long BundleSize,
    string PublicKey, string Signature);

public sealed record GitProjectBinding(string ProjectId, string RepositoryPath, string Branch);
public sealed record GitProjectSummary(string ProjectId, string Name, string CreatorDeviceId,
    int MemberCount, DateTimeOffset LastChangedUtc, bool Joined);
public sealed record GitLineVersion(string ProjectId, string MemberId, long Sequence);
public sealed record GitSyncRequest(GitProjectEvent[] Events, GitLineVersion[] Known);
public sealed record GitSyncResponse(GitProjectEvent[] Events, GitLineVersion[] Known);
public sealed record GitBundleInfo(string Hash, long Size, string Path);

public static class GitBundleFile
{
    public static async Task<bool> VerifyAsync(string path, string hash, long size, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        return string.Equals(actual, hash, StringComparison.Ordinal);
    }
}

public sealed class GitCommandRunner(string executable)
{
    public string Executable { get; } = executable;

    public async Task<string> RunAsync(string? directory, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (directory is not null) start.WorkingDirectory = directory;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Git。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(arguments.Length > 0 && arguments[0] is "bundle" or "clone" or "fetch" or "checkout" or "merge" or "lfs"
            ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30));
        try
        {
            var outputTask = ReadLimitedAsync(process.StandardOutput, 1024 * 1024, timeout.Token);
            var errorTask = ReadLimitedAsync(process.StandardError, 16 * 1024, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(
                $"Git 操作失败（退出码 {process.ExitCode}）：{Redact(error)}");
            return output.TrimEnd('\r', '\n');
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxChars, CancellationToken token)
    {
        var result = new StringBuilder();
        var overflow = false;
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + count > maxChars) overflow = true;
            else if (!overflow) result.Append(buffer, 0, count);
        }
        if (overflow) throw new InvalidDataException("Git 命令输出超过安全限制。");
        return result.ToString();
    }

    private static string Redact(string value)
    {
        value = Regex.Replace(value, @"(?i)(https?://)[^\s/@]+@", "$1***@");
        value = Regex.Replace(value, @"(?i)(token|password|authorization)[=:]\s*\S+", "$1=***");
        return value.Length > 400 ? value[..400] : value;
    }
}

public static class GitEnvironment
{
    public static async Task<GitCommandRunner?> DetectAsync(string? preferredPath = null, CancellationToken token = default)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredPath)) candidates.Add(preferredPath);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(programFiles, "Git", "cmd", "git.exe"));
        candidates.Add(Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"));
        candidates.Add("git.exe");
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var runner = new GitCommandRunner(candidate);
                if ((await runner.RunAsync(null, token, "--version")).StartsWith("git version ", StringComparison.OrdinalIgnoreCase))
                    return runner;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
            }
        }
        return null;
    }
}

public sealed class GitRepositoryService(GitCommandRunner git)
{
    private const long MaxPackageBytes = 1024L * 1024 * 1024;
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex LfsHashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private sealed record LfsObject(string Oid, long Size, string Path);

    public async Task<(string Root, string Branch, string Head, bool Dirty)> InspectAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("仓库目录不存在。");
        var root = await git.RunAsync(path, token, "rev-parse", "--show-toplevel");
        var branch = await git.RunAsync(path, token, "symbolic-ref", "--quiet", "--short", "HEAD");
        var head = await git.RunAsync(path, token, "rev-parse", "HEAD");
        if (!HashPattern.IsMatch(head)) throw new InvalidDataException("Git HEAD 无效。");
        var status = await git.RunAsync(path, token, "status", "--porcelain=v1", "--untracked-files=normal");
        var shallow = await git.RunAsync(path, token, "rev-parse", "--is-shallow-repository");
        if (shallow == "true") throw new InvalidOperationException("浅克隆仓库暂不支持创建协作项目。");
        var submodules = await git.RunAsync(path, token, "submodule", "status");
        if (submodules.Length != 0) throw new InvalidOperationException("含子模块的仓库暂不支持一键协作；Git bundle 不会包含子模块内容。");
        try
        {
            if (await git.RunAsync(path, token, "config", "--bool", "core.sparseCheckout") == "true")
                throw new NotSupportedException("稀疏检出仓库暂不支持一键协作。");
        }
        catch (InvalidOperationException) { /* An unset option returns 1. */ }
        return (Path.GetFullPath(root), branch, head.ToLowerInvariant(), status.Length != 0);
    }

    public async Task<GitCommitPoint[]> RecentCommitsAsync(string root, string head, CancellationToken token)
    {
        if (!HashPattern.IsMatch(head)) throw new ArgumentException("提交号无效。", nameof(head));
        var output = await git.RunAsync(root, token, "log", "--max-count=120", "--format=%H%x00%P%x00%s%x00%cI%x00", head);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var parts = line.TrimEnd('\r', '\0').Split('\0');
            if (parts.Length != 4 || !HashPattern.IsMatch(parts[0])) throw new InvalidDataException("Git 提交资料无效。");
            return new GitCommitPoint(parts[0].ToLowerInvariant(),
                parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => value.ToLowerInvariant()).ToArray(),
                parts[2].Length > 180 ? parts[2][..180] : parts[2], DateTimeOffset.Parse(parts[3]));
        }).Reverse().ToArray();
    }

    public async Task<bool> IsAncestorAsync(string root, string older, string newer, CancellationToken token)
    {
        if (!HashPattern.IsMatch(older) || !HashPattern.IsMatch(newer)) return false;
        try { await git.RunAsync(root, token, "merge-base", "--is-ancestor", older, newer); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public async Task<GitBundleInfo> CreateBundleAsync(string root, string branch, string outputDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var rawBundle = Path.Combine(outputDirectory, operationId + ".raw.bundle");
        var packagedBundle = Path.Combine(outputDirectory, operationId + ".bundle");
        try
        {
            await git.RunAsync(root, token, "bundle", "create", rawBundle, "refs/heads/" + branch);
            await git.RunAsync(root, token, "bundle", "verify", rawBundle);
            var head = await git.RunAsync(root, token, "rev-parse", "refs/heads/" + branch);
            var lfsObjects = await ListLfsObjectsAsync(root, head, rawBundle, token);
            var rawSize = new FileInfo(rawBundle).Length;
            if (rawSize <= 0 || rawSize + lfsObjects.Sum(item => item.Size) > MaxPackageBytes - 1024 * 1024)
                throw new InvalidDataException("Git 历史与 LFS 文件合计超过 1 GB，暂不能发布为协作包。");
            var temporary = rawBundle;
            if (lfsObjects.Length != 0)
            {
                await using (var output = new FileStream(packagedBundle, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var repositoryEntry = archive.CreateEntry("repository.bundle", CompressionLevel.NoCompression);
                    await using (var entryOutput = repositoryEntry.Open())
                    await using (var input = File.OpenRead(rawBundle))
                        await input.CopyToAsync(entryOutput, token);
                    foreach (var item in lfsObjects)
                    {
                        if (!File.Exists(item.Path) || new FileInfo(item.Path).Length != item.Size)
                            throw new FileNotFoundException($"Git LFS 对象 {item.Oid[..12]} 不在本机；请先在 Git 工具中获取完整 LFS 内容。", item.Path);
                        var entry = archive.CreateEntry("lfs/" + item.Oid, CompressionLevel.NoCompression);
                        await using var entryOutput = entry.Open();
                        await using var input = File.OpenRead(item.Path);
                        await CopyAndVerifyAsync(input, entryOutput, item.Size, item.Oid, token);
                    }
                }
                temporary = packagedBundle;
            }
            var size = new FileInfo(temporary).Length;
            if (size is <= 0 or > MaxPackageBytes) throw new InvalidDataException("协作包大小超出 1 GB 限制。");
            string hash;
            await using (var stream = File.OpenRead(temporary))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            var destination = Path.Combine(outputDirectory, hash + ".bundle");
            if (File.Exists(destination)) File.Delete(temporary);
            else File.Move(temporary, destination);
            return new GitBundleInfo(hash, size, destination);
        }
        finally
        {
            if (File.Exists(rawBundle)) File.Delete(rawBundle);
            if (File.Exists(packagedBundle)) File.Delete(packagedBundle);
        }
    }

    public async Task CloneAsync(string bundlePath, string branch, string destination, CancellationToken token)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("目标位置已存在，请选择一个新文件夹。");
        var (rawBundle, temporary) = await PrepareRawBundleAsync(bundlePath, token);
        try
        {
            await git.RunAsync(null, token, "clone", "--no-checkout", "--branch", branch, rawBundle, destination);
            if (temporary) await ImportLfsObjectsAsync(bundlePath, destination, token);
            await git.RunAsync(destination, token, "remote", "remove", "origin");
            await git.RunAsync(destination, token, "checkout", branch);
        }
        finally { if (temporary && File.Exists(rawBundle)) File.Delete(rawBundle); }
    }

    public async Task FetchIntoInboxAsync(string repository, string bundlePath, string branch,
        string memberId, CancellationToken token)
    {
        if (!Regex.IsMatch(memberId, "^[0-9a-f]{24}$")) throw new ArgumentException("成员编号无效。");
        var (rawBundle, temporary) = await PrepareRawBundleAsync(bundlePath, token);
        try
        {
            await git.RunAsync(repository, token, "bundle", "verify", rawBundle);
            await git.RunAsync(repository, token, "fetch", rawBundle,
                $"refs/heads/{branch}:refs/remotes/resourcemanager/{memberId}");
            if (temporary) await ImportLfsObjectsAsync(bundlePath, repository, token);
        }
        finally { if (temporary && File.Exists(rawBundle)) File.Delete(rawBundle); }
    }

    private async Task<LfsObject[]> ListLfsObjectsAsync(string root, string head, string rawBundle,
        CancellationToken token)
    {
        try
        {
            if ((await git.RunAsync(root, token, "grep", "-I", "-l", "-F", "filter=lfs",
                    head, "--", "*.gitattributes")).Length == 0) return [];
        }
        catch (InvalidOperationException) { return []; /* git grep returns 1 without a match. */ }
        try { await git.RunAsync(root, token, "lfs", "version"); }
        catch (InvalidOperationException ex)
        { throw new NotSupportedException("仓库配置了 Git LFS；请先安装 Git LFS，才能打包完整文件。", ex); }
        // Scan a bare clone of the selected-branch bundle. --all then includes old versions of
        // LFS files without accidentally including objects from unrelated local branches.
        var packageDirectory = Path.GetFullPath(Path.GetDirectoryName(rawBundle)!);
        var indexDirectory = Path.GetFullPath(Path.Combine(packageDirectory,
            ".lfs-index-" + Guid.NewGuid().ToString("N")));
        if (!indexDirectory.StartsWith(Path.TrimEndingDirectorySeparator(packageDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Git LFS 临时目录超出协作包目录。");
        string output;
        try
        {
            await git.RunAsync(null, token, "clone", "--bare", rawBundle, indexDirectory);
            output = await git.RunAsync(indexDirectory, token, "lfs", "ls-files", "--all", "--json");
        }
        finally
        {
            if (Directory.Exists(indexDirectory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(indexDirectory, "*",
                             SearchOption.AllDirectories)) File.SetAttributes(entry, FileAttributes.Normal);
                Directory.Delete(indexDirectory, true);
            }
        }
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidDataException("Git LFS 未返回可识别的文件清单。");
        if (files.ValueKind == JsonValueKind.Null) return [];
        var commonDirectory = await git.RunAsync(root, token, "rev-parse", "--git-common-dir");
        var lfsRoot = Path.GetFullPath(Path.Combine(root, commonDirectory, "lfs", "objects"));
        var objects = new Dictionary<string, LfsObject>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateArray())
        {
            var oid = file.GetProperty("oid").GetString() ?? "";
            var size = file.GetProperty("size").GetInt64();
            if (!LfsHashPattern.IsMatch(oid) || size is < 0 or > MaxPackageBytes)
                throw new InvalidDataException("Git LFS 对象编号或大小无效。");
            objects.TryAdd(oid, new LfsObject(oid, size,
                Path.Combine(lfsRoot, oid[..2], oid[2..4], oid)));
        }
        return objects.Values.ToArray();
    }

    private static async Task<(string RawBundle, bool Temporary)> PrepareRawBundleAsync(string packagePath,
        CancellationToken token)
    {
        await using var check = File.OpenRead(packagePath);
        var magic = new byte[4];
        if (await check.ReadAsync(magic, token) != 4 || magic[0] != 'P' || magic[1] != 'K' ||
            magic[2] != 3 || magic[3] != 4) return (packagePath, false);
        using var archive = ZipFile.OpenRead(packagePath);
        var (repositoryEntry, _) = ValidateArchive(archive);
        var temporary = Path.Combine(Path.GetTempPath(), "ResourceManager-" + Guid.NewGuid().ToString("N") + ".bundle");
        try
        {
            await using var input = repositoryEntry.Open();
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await input.CopyToAsync(output, token);
            return (temporary, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private async Task ImportLfsObjectsAsync(string packagePath, string repository, CancellationToken token)
    {
        await git.RunAsync(repository, token, "lfs", "version");
        using var archive = ZipFile.OpenRead(packagePath);
        var (_, lfsEntries) = ValidateArchive(archive);
        var commonDirectory = await git.RunAsync(repository, token, "rev-parse", "--git-common-dir");
        var root = Path.GetFullPath(Path.Combine(repository, commonDirectory, "lfs", "objects"));
        foreach (var entry in lfsEntries)
        {
            var oid = entry.FullName[4..];
            var target = Path.Combine(root, oid[..2], oid[2..4], oid);
            if (await GitBundleFile.VerifyAsync(target, oid, entry.Length, token)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var input = entry.Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                    await CopyAndVerifyAsync(input, output, entry.Length, oid, token);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static (ZipArchiveEntry Repository, ZipArchiveEntry[] Lfs) ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is < 2 or > 5001)
            throw new InvalidDataException("Git 协作包中的文件数无效。");
        var repository = archive.Entries.SingleOrDefault(item => item.FullName == "repository.bundle")
            ?? throw new InvalidDataException("Git 协作包缺少仓库历史。");
        var lfs = archive.Entries.Where(item => item != repository).ToArray();
        if (repository.Length <= 0 || lfs.Any(item => !item.FullName.StartsWith("lfs/", StringComparison.Ordinal) ||
                !LfsHashPattern.IsMatch(item.FullName[4..])) ||
            lfs.Select(item => item.FullName).Distinct(StringComparer.Ordinal).Count() != lfs.Length ||
            archive.Entries.Sum(item => item.Length) > MaxPackageBytes)
            throw new InvalidDataException("Git 协作包中的 LFS 文件清单无效。");
        return (repository, lfs);
    }

    private static async Task CopyAndVerifyAsync(Stream input, Stream output, long expectedSize, string expectedHash,
        CancellationToken token)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            if (total > expectedSize) throw new InvalidDataException("Git LFS 对象超过预期大小。");
            hasher.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (total != expectedSize || Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant() != expectedHash)
            throw new InvalidDataException("Git LFS 对象 SHA-256 校验失败。");
    }
}

public sealed class GitCollaborationStore
{
    private static readonly Regex CommitHashPattern = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex BundleHashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private readonly string statePath;
    private readonly string bundleDirectory;
    private readonly string keyPath;
    private GitState state;
    private readonly ECDsa identity;

    public GitCollaborationStore(string dataDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(dataDirectory), "git-collaboration");
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "state.json");
        keyPath = Path.Combine(root, "identity.key");
        bundleDirectory = Path.Combine(root, "bundles");
        Directory.CreateDirectory(bundleDirectory);
        identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(keyPath)) identity.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);
        else File.WriteAllBytes(keyPath, identity.ExportPkcs8PrivateKey());
        try
        {
            state = File.Exists(statePath)
                ? JsonSerializer.Deserialize<GitState>(File.ReadAllText(statePath)) ?? new GitState()
                : new GitState();
            state.Events ??= [];
            state.Bindings ??= [];
            if (state.Events.Any(item => item is null || !ValidShape(item) || !Verify(item)) ||
                state.Events.GroupBy(item => item.ProjectId).Any(group => group.Count(item => item.Kind == "Created") != 1) ||
                state.Bindings.Any(item => item is null || !Guid.TryParse(item.ProjectId, out _) ||
                    string.IsNullOrWhiteSpace(item.RepositoryPath)))
                throw new JsonException("Git 协作状态包含无效记录。");
        }
        catch (JsonException)
        {
            // Preserve the damaged file for recovery instead of preventing the whole app from starting.
            File.Move(statePath, statePath + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            state = new GitState();
        }
    }

    public string BundleDirectory => bundleDirectory;
    public string MemberId => Convert.ToHexString(SHA256.HashData(identity.ExportSubjectPublicKeyInfo()))[..24].ToLowerInvariant();

    public IReadOnlyList<GitProjectEvent> GetEvents(string? projectId = null)
    {
        lock (gate) return state.Events.Where(item => projectId is null || item.ProjectId == projectId).ToArray();
    }

    public GitLineVersion[] GetVersions()
    {
        lock (gate) return state.Events.GroupBy(item => (item.ProjectId, item.MemberId))
            .Select(group => new GitLineVersion(group.Key.ProjectId, group.Key.MemberId,
                group.Max(item => item.Sequence))).ToArray();
    }

    public GitProjectEvent[] GetMissing(IReadOnlyList<GitLineVersion> known, int limit = 50)
    {
        lock (gate)
        {
            var cursors = known.GroupBy(item => (item.ProjectId, item.MemberId))
                .ToDictionary(group => group.Key, group => group.Max(item => item.Sequence));
            return state.Events.Where(item => item.Sequence > cursors.GetValueOrDefault((item.ProjectId, item.MemberId)))
                .OrderBy(item => item.Kind == "Created" ? 0 : 1)
                .ThenBy(item => item.ProjectId).ThenBy(item => item.MemberId).ThenBy(item => item.Sequence)
                .Take(limit).ToArray();
        }
    }

    public IReadOnlyList<GitProjectBinding> GetBindings()
    {
        lock (gate) return state.Bindings.ToArray();
    }

    public GitProjectBinding? GetBinding(string projectId)
    {
        lock (gate) return state.Bindings.FirstOrDefault(item => item.ProjectId == projectId);
    }

    public void SetBinding(GitProjectBinding binding)
    {
        lock (gate)
        {
            state.Bindings.RemoveAll(item => item.ProjectId == binding.ProjectId);
            state.Bindings.Add(binding);
            Persist();
        }
    }

    public GitProjectEvent CreateEvent(string projectId, string projectName, string branch, string deviceId, string memberName,
        string kind, string commitHash, string? sourceHash, GitCommitPoint[] commits, GitBundleInfo? bundle)
    {
        lock (gate)
        {
            var previous = state.Events.Where(item => item.ProjectId == projectId && item.MemberId == MemberId)
                .OrderByDescending(item => item.Sequence).FirstOrDefault();
            var unsigned = new GitProjectEvent(projectId, projectName, branch, MemberId, deviceId, memberName,
                (previous?.Sequence ?? 0) + 1, previous is null ? "" : EventHash(previous), kind,
                commitHash, sourceHash, DateTimeOffset.UtcNow, commits, bundle?.Hash, bundle?.Size ?? 0,
                Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), "");
            var signature = Convert.ToBase64String(identity.SignData(Payload(unsigned), HashAlgorithmName.SHA256));
            var item = unsigned with { Signature = signature };
            if (Merge([item]) != 1) throw new InvalidOperationException("无法保存本机 Git 协作记录。");
            return item;
        }
    }

    public int Merge(IEnumerable<GitProjectEvent> incoming)
    {
        lock (gate)
        {
            var changed = 0;
            foreach (var item in incoming.OrderBy(item => item.Sequence))
            {
                if (!ValidShape(item)) continue;
                if (state.Events.Any(existing => existing.ProjectId == item.ProjectId &&
                    existing.MemberId == item.MemberId && existing.Sequence == item.Sequence)) continue;
                if (!Verify(item)) continue;
                var previous = state.Events.Where(existing => existing.ProjectId == item.ProjectId &&
                    existing.MemberId == item.MemberId).OrderByDescending(existing => existing.Sequence).FirstOrDefault();
                var projectCreated = state.Events.FirstOrDefault(existing =>
                    existing.ProjectId == item.ProjectId && existing.Kind == "Created");
                if (previous is null)
                {
                    if (item.Sequence != 1 || item.PreviousHash.Length != 0) continue;
                    if (item.Kind == "Created" && projectCreated is not null ||
                        item.Kind != "Created" && projectCreated is null) continue;
                }
                else if (item.Sequence != previous.Sequence + 1 || item.PreviousHash != EventHash(previous) ||
                         item.PublicKey != previous.PublicKey || item.DeviceId != previous.DeviceId) continue;
                if (previous is null && item.Kind is "Published" or "Synced" ||
                    previous is not null && item.Kind is "Created" or "Joined") continue;
                if (projectCreated is not null && (item.ProjectName != projectCreated.ProjectName ||
                    item.Branch != projectCreated.Branch)) continue;
                if (state.Events.Any(existing => existing.ProjectId == item.ProjectId &&
                    existing.DeviceId == item.DeviceId && existing.MemberId != item.MemberId)) continue;
                state.Events.Add(item);
                changed++;
            }
            if (changed > 0) Persist();
            return changed;
        }
    }

    public string? BundlePath(string hash)
    {
        if (!BundleHashPattern.IsMatch(hash)) return null;
        var path = Path.Combine(bundleDirectory, hash + ".bundle");
        return File.Exists(path) ? path : null;
    }

    public IReadOnlyList<GitProjectSummary> GetProjects()
    {
        lock (gate)
        {
            return state.Events.GroupBy(item => item.ProjectId).Select(group =>
            {
                var created = group.First(item => item.Kind == "Created");
                return new GitProjectSummary(group.Key, created.ProjectName, created.DeviceId,
                    group.Select(item => item.MemberId).Distinct().Count(), group.Max(item => item.CreatedUtc),
                    state.Bindings.Any(binding => binding.ProjectId == group.Key));
            }).OrderByDescending(item => item.LastChangedUtc).ToArray();
        }
    }

    private static bool ValidShape(GitProjectEvent item) =>
        Guid.TryParse(item.ProjectId, out _) && item.ProjectName is { Length: > 0 and <= 80 } &&
        ValidBranch(item.Branch) && item.MemberId is not null && Regex.IsMatch(item.MemberId, "^[0-9a-f]{24}$") &&
        item.DeviceId is { Length: > 0 and <= 100 } && item.MemberName is { Length: > 0 and <= 80 } &&
        item.Sequence is > 0 and <= 100000 && item.PreviousHash is not null &&
        (item.PreviousHash.Length == 0 || BundleHashPattern.IsMatch(item.PreviousHash)) &&
        item.Kind is "Created" or "Joined" or "Published" or "Synced" &&
        item.CommitHash is not null && CommitHashPattern.IsMatch(item.CommitHash) &&
        (item.SourceHash is null || CommitHashPattern.IsMatch(item.SourceHash)) &&
        (item.Kind == "Created" && item.SourceHash is null ||
         item.Kind != "Created" && item.SourceHash is not null) &&
        item.Commits is { Length: <= 120 } && item.Commits.All(commit =>
            commit is not null && commit.Hash is not null && CommitHashPattern.IsMatch(commit.Hash) &&
            commit.Parents is { Length: <= 8 } && commit.Parents.All(parent =>
                parent is not null && CommitHashPattern.IsMatch(parent)) &&
            commit.Subject is { Length: <= 180 }) &&
        (item.Kind is not ("Created" or "Published") ||
         item.Commits.Length > 0 && item.Commits.Any(commit => commit.Hash == item.CommitHash)) &&
        item.BundleSize is >= 0 and <= 1024L * 1024 * 1024 &&
        (item.BundleHash is null && item.BundleSize == 0 ||
         item.BundleHash is not null && BundleHashPattern.IsMatch(item.BundleHash) && item.BundleSize > 0) &&
        (item.Kind is not ("Created" or "Published") || item.BundleHash is not null) &&
        item.PublicKey is { Length: > 0 and <= 512 } && item.Signature is { Length: > 0 and <= 512 };

    private static bool ValidBranch(string? branch) => branch is { Length: > 0 and <= 100 } &&
        branch != "@" && !branch.StartsWith('/') && !branch.EndsWith('/') && !branch.EndsWith('.') &&
        !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal) &&
        !branch.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) &&
        !branch.Any(character => char.IsControl(character) || character is ' ' or ':' or '?' or '[' or '\\' or '^' or '~' or '*');

    private static bool Verify(GitProjectEvent item)
    {
        try
        {
            var key = Convert.FromBase64String(item.PublicKey);
            if (Convert.ToHexString(SHA256.HashData(key))[..24].ToLowerInvariant() != item.MemberId) return false;
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(key, out _);
            return verifier.VerifyData(Payload(item), Convert.FromBase64String(item.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static byte[] Payload(GitProjectEvent item) => JsonSerializer.SerializeToUtf8Bytes(item with { Signature = "" });
    private static string EventHash(GitProjectEvent item) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(item))).ToLowerInvariant();

    private void Persist()
    {
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class GitState
    {
        public List<GitProjectEvent> Events { get; set; } = [];
        public List<GitProjectBinding> Bindings { get; set; } = [];
    }
}
