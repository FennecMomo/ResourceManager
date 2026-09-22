using System.Net;

namespace ResourceManager.Core;

public sealed class DownloadManager(NodeStore store, PeerClient client)
{
    public DownloadJob CreateJob(PeerInfo peer, RemoteResource resource, string destinationDirectory)
    {
        if (!resource.Available) throw new InvalidOperationException("资源当前不可下载。");
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var name = Path.GetFileName(resource.Name);
        if (name is "" or "." or ".." || name != resource.Name) throw new InvalidDataException("资源名称无效。");
        var target = Path.Combine(destinationDirectory, name);
        var stem = resource.Kind == ResourceKind.File ? Path.GetFileNameWithoutExtension(name) : name;
        var extension = resource.Kind == ResourceKind.File ? Path.GetExtension(name) : "";
        for (var index = 2; File.Exists(target) || Directory.Exists(target) || File.Exists(target + ".rm-part"); index++)
            target = Path.Combine(destinationDirectory, $"{stem} ({index}){extension}");
        var job = new DownloadJob(Guid.NewGuid().ToString("N"), peer.DeviceId, resource.Id, resource.Name,
            resource.Kind, target, "等待下载", 0, resource.Size, null);
        store.SaveDownload(job);
        return job;
    }

    public async Task<DownloadJob> RunAsync(string jobId, IProgress<DownloadJob>? progress = null, CancellationToken cancellationToken = default)
    {
        var job = store.GetDownload(jobId) ?? throw new ArgumentException("下载任务不存在。", nameof(jobId));
        var peer = store.GetPeer(job.PeerId) ?? throw new InvalidOperationException("发布者已从设备列表删除。");
        try
        {
            var resources = await client.GetResourcesAsync(peer, cancellationToken).ConfigureAwait(false);
            var resource = resources.FirstOrDefault(r => r.Id == job.ResourceId)
                ?? throw new FileNotFoundException("发布者已撤销资源。");
            if (!resource.Available || resource.Kind != job.Kind) throw new IOException("资源已不可用或类型已变化。");
            var entries = await client.GetFilesAsync(peer, job.ResourceId, cancellationToken).ConfigureAwait(false);
            if (job.Kind == ResourceKind.File && (entries.Count != 1 || entries[0].IsDirectory))
                throw new InvalidDataException("文件目录信息无效。");
            var total = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            job = job with { Status = "下载中", TotalBytes = total, Error = null };
            SaveAndReport(job, progress);
            if (job.Kind == ResourceKind.Folder) Directory.CreateDirectory(job.TargetPath);
            var completed = 0L;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = TargetFor(job, entry);
                if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var baseCompleted = completed;
                await DownloadFileAsync(peer, job.ResourceId, entry, target, bytes =>
                {
                    job = job with { DownloadedBytes = Math.Min(total, baseCompleted + bytes) };
                    SaveAndReport(job, progress);
                }, cancellationToken).ConfigureAwait(false);
                completed += entry.Size;
                job = job with { DownloadedBytes = completed };
                SaveAndReport(job, progress);
            }
            job = job with { Status = "已完成", DownloadedBytes = total, Error = null };
            SaveAndReport(job, progress);
            return job;
        }
        catch (OperationCanceledException)
        {
            job = job with { Status = "已暂停", Error = null };
            SaveAndReport(job, progress);
            throw;
        }
        catch (Exception exception)
        {
            job = job with { Status = "已中断", Error = exception.Message };
            SaveAndReport(job, progress);
            throw;
        }
    }

    private static string TargetFor(DownloadJob job, RemoteFile entry)
    {
        if (job.Kind == ResourceKind.File)
        {
            if (entry.RelativePath != "") throw new InvalidDataException("文件路径无效。");
            return job.TargetPath;
        }
        var parts = entry.RelativePath.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.Contains(':'))) throw new InvalidDataException("远端目录包含无效路径。");
        var path = Path.GetFullPath(Path.Combine([job.TargetPath, .. parts]));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.TargetPath)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("远端目录越界。");
        return path;
    }

    private async Task DownloadFileAsync(PeerInfo peer, string resourceId, RemoteFile file, string target,
        Action<long> report, CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            var existing = new FileInfo(target);
            if (existing.Length == file.Size && Math.Abs((existing.LastWriteTimeUtc - file.ModifiedUtc.UtcDateTime).TotalSeconds) < 2)
            { report(file.Size); return; }
        }
        var partial = target + ".rm-part";
        var tagPath = target + ".rm-etag";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        var tag = File.Exists(tagPath) ? await File.ReadAllTextAsync(tagPath, cancellationToken).ConfigureAwait(false) : null;
        if (offset > file.Size || (offset > 0 && string.IsNullOrEmpty(tag)))
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset == file.Size && offset > 0)
        {
            File.Move(partial, target, true);
            File.SetLastWriteTimeUtc(target, file.ModifiedUtc.UtcDateTime);
            File.Delete(tagPath);
            report(file.Size);
            return;
        }
        using var response = await client.OpenFileAsync(peer, resourceId, file.RelativePath, offset, tag, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            File.Delete(tagPath);
            await DownloadFileAsync(peer, resourceId, file, target, report, cancellationToken).ConfigureAwait(false);
            return;
        }
        response.EnsureSuccessStatusCode();
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
        if (!append) offset = 0;
        var responseTag = response.Headers.ETag?.ToString();
        if (responseTag is not null) await File.WriteAllTextAsync(tagPath, responseTag, cancellationToken).ConfigureAwait(false);
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[1024 * 64];
            var copied = offset;
            var lastReport = DateTime.UtcNow;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                copied += count;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250)
                { report(copied); lastReport = DateTime.UtcNow; }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (copied != file.Size) throw new IOException("下载大小与远端目录不一致，可重试。");
        }
        File.Move(partial, target, true);
        File.SetLastWriteTimeUtc(target, file.ModifiedUtc.UtcDateTime);
        File.Delete(tagPath);
        report(file.Size);
    }

    private void SaveAndReport(DownloadJob job, IProgress<DownloadJob>? progress)
    {
        store.SaveDownload(job);
        progress?.Report(job);
    }
}
