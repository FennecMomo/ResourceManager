using System.IO;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private string[] updateServerFailures = [];
    private async Task<IReadOnlyList<LocalUpdateCandidate>> FindServerUpdatesAsync(CancellationToken token)
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var targets = Servers.ToArray();
        var tasks = targets.Select(async row =>
        {
            try
            {
                for (var attempt = 0; attempt < 40 && row.Status != "在线" && row.Status != "连接受限"; attempt++) await Task.Delay(200, token);
                if (row.Session is null || !row.SupportsResources) throw new IOException("尚未连接或服务端版本过旧");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var catalogs = await workspaceClient!.CatalogsAsync(row.Binding, row.Session, timeout.Token);
                return catalogs.Where(c => c.Online && c.Owner != store.GetSettings().Profile.DeviceId).SelectMany(c => c.Updates.Select(package =>
                {
                    var member = row.Binding.Cached?.Members.FirstOrDefault(m => m.Profile.DeviceId == c.Owner);
                    if (member is null || !ValidUpdatePackage(package)) return null;
                    return new LocalUpdateCandidate(new(c.Owner, "", 0, member.Profile.Nickname, member.Profile.Avatar, null), package, UpdateClient.ParseVersion(package.Version), row.Binding.Id, row.Name);
                })).OfType<LocalUpdateCandidate>().ToArray();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(row.Name + "：" + ex.Message); return []; }
        }).ToArray();
        var results = await Task.WhenAll(tasks); updateServerFailures = failures.ToArray();
        return results.SelectMany(x => x).ToArray();
    }
    private static bool ValidUpdatePackage(SharedUpdatePackage package)
    {
        try { UpdateClient.ParseVersion(package.Version); return !string.IsNullOrWhiteSpace(package.ResourceId) && package.ResourceId.Length <= 100 && package.Size is > 0 and <= UpdateClient.MaxPackageBytes && package.Sha256?.Length == 64 && package.Sha256.All(Uri.IsHexDigit); }
        catch (Exception) { return false; }
    }
    private async Task<string> DownloadUpdateCandidateAsync(LocalUpdateCandidate candidate, IProgress<long> progress, CancellationToken token)
    {
        if (candidate.ServerBindingId is null) return await updateClient.DownloadAsync(candidate.Package, candidate.Peer, client, progress, token);
        var row = Servers.FirstOrDefault(s => s.Binding.Id == candidate.ServerBindingId) ?? throw new IOException("更新来源服务器已移除。");
        if (row.Session is null) throw new IOException("更新来源服务器未连接。");
        using var transport = new WorkspaceResourceClient(store, workspaceClient!, row.Binding, () => row.Session, candidate.Peer.DeviceId);
        var path = await updateClient.DownloadAsync(candidate.Package, candidate.Peer, transport, progress, token);
        try { ValidateServerUpdateFile(path, candidate.Version); }
        catch { File.Delete(path); throw; }
        return path;
    }
    internal static void ValidateServerUpdateFile(string path, Version expectedVersion)
    {
        // A server's catalog is a discovery hint, not authority over an EXE's embedded version/platform.
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (version != expectedVersion) throw new InvalidDataException("安装包内嵌版本与服务器目录不一致。");
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        stream.Position = 0x3c; var pe = reader.ReadInt32();
        if (pe < 0 || pe > stream.Length - 6) throw new InvalidDataException("更新包 PE 文件头无效。");
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("更新包不是 Windows x64 程序。");
    }
}
