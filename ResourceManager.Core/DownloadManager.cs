using System.Net;

namespace ResourceManager.Core;

public sealed class DownloadManager(NodeStore store, IResourceClient client, Func<DownloadJob, PeerInfo?>? resolvePeer = null)
{
    public DownloadJob CreateJob(PeerInfo peer, RemoteResource resource, string destinationDirectory, string? localName = null) =>
        store.CreateDownload(peer, resource, destinationDirectory, localName);

    public async Task<DownloadJob> RunAsync(string jobId, IProgress<DownloadJob>? progress = null, CancellationToken cancellationToken = default)
    {
        var job = store.GetDownload(jobId) ?? throw new ArgumentException("下载任务不存在。", nameof(jobId));
        try
        {
            var peer = resolvePeer?.Invoke(job) ?? store.GetPeer(job.PeerId) ?? throw new InvalidOperationException("发布者已从设备列表删除。");
            var resource = await client.GetResourceAsync(peer, job.ResourceId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("发布者已撤销资源。");
            if (!resource.Available || resource.Kind != job.Kind) throw new IOException("资源已不可用或类型已变化。");
            var entries = await client.GetFilesAsync(peer, job.ResourceId, cancellationToken).ConfigureAwait(false);
            if (job.Kind == ResourceKind.File && (entries.Count != 1 || entries[0].IsDirectory))
                throw new InvalidDataException("文件目录信息无效。");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Size < 0 || !targets.Add(TargetFor(job, entry))) throw new InvalidDataException("远端目录包含无效大小或重复路径。");
            }
            if (entries.Where(e => !e.IsDirectory).Any(e => targets.Contains(TargetFor(job, e) + ".rm-part") ||
                    targets.Contains(TargetFor(job, e) + ".rm-etag")))
                throw new InvalidDataException("远端文件名与下载临时文件冲突。");
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

    public void RemoveJob(string jobId, bool deleteLocalData)
    {
        var job = store.GetDownload(jobId);
        if (job is null) return;
        if (deleteLocalData) DeleteLocalData(job);
        store.RemoveDownload(jobId);
    }

    private static void DeleteLocalData(DownloadJob job)
    {
        if (job.Kind == ResourceKind.Folder)
        {
            if (Directory.Exists(job.TargetPath)) Directory.Delete(job.TargetPath, true);
            return;
        }
        foreach (var path in new[] { job.TargetPath, job.TargetPath + ".rm-part", job.TargetPath + ".rm-etag" })
            if (File.Exists(path)) File.Delete(path);
    }

    private static string TargetFor(DownloadJob job, RemoteFile entry)
    {
        if (job.Kind == ResourceKind.File)
        {
            if (entry.RelativePath != "") throw new InvalidDataException("文件路径无效。");
            return job.TargetPath;
        }
        var relative = entry.RelativePath.Replace('\\', '/');
        if (!StoredPaths.Valid(relative)) throw new InvalidDataException("远端目录包含无效路径。");
        return StoredPaths.Resolve(job.TargetPath, relative);
    }

    private async Task DownloadFileAsync(PeerInfo peer, string resourceId, RemoteFile file, string target,
        Action<long> report, CancellationToken cancellationToken)
    {
        var metadata = await client.GetFileMetadataAsync(peer, resourceId, file.RelativePath, cancellationToken).ConfigureAwait(false);
        if (metadata is not null && metadata.Size != file.Size) throw new IOException("文件已变化，请重新下载。");
        async Task<bool> Verified(string path)
        {
            if (metadata is null) return true;
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        if (File.Exists(target))
        {
            var existing = new FileInfo(target);
            if (existing.Length == file.Size && Math.Abs((existing.LastWriteTimeUtc - file.ModifiedUtc.UtcDateTime).TotalSeconds) < 2)
            { if (await Verified(target).ConfigureAwait(false)) { report(file.Size); return; } }
        }
        var partial = target + ".rm-part";
        var tagPath = target + ".rm-etag";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        var tag = File.Exists(tagPath) ? await File.ReadAllTextAsync(tagPath, cancellationToken).ConfigureAwait(false) : null;
        if (offset > file.Size || (offset > 0 && (string.IsNullOrEmpty(tag) || metadata is not null && tag != metadata.Etag)))
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset == file.Size && offset > 0)
        {
            if (metadata is not null)
            {
                if (!await Verified(partial).ConfigureAwait(false)) { File.Delete(partial); File.Delete(tagPath); throw new IOException("续传文件校验失败，请重试。"); }
                File.Move(partial, target, true);
                File.SetLastWriteTimeUtc(target, file.ModifiedUtc.UtcDateTime);
                File.Delete(tagPath);
                report(file.Size);
                return;
            }
            // A complete partial file may belong to an older same-size version. Without a
            // current content hash it must be fetched again, not promoted based on length.
            offset = 0;
        }
        using var response = await client.OpenFileAsync(peer, resourceId, file.RelativePath, offset, tag, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            response.Dispose();
            File.Delete(partial);
            File.Delete(tagPath);
            await DownloadFileAsync(peer, resourceId, file, target, report, cancellationToken).ConfigureAwait(false);
            return;
        }
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != file.Size ||
             response.Content.Headers.ContentRange?.To != file.Size - 1))
            throw new InvalidDataException("远端返回的续传范围无效。");
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) offset = 0;
        var responseTag = response.Headers.ETag?.ToString();
        if (append && responseTag != tag) throw new InvalidDataException("续传期间远端文件版本已变化。");
        if (response.Content.Headers.ContentLength is long length && length != file.Size - offset)
            throw new IOException("下载响应大小与远端目录不一致。");
        StorageLocation.EnsureSpace(Path.GetDirectoryName(target)!, Math.Max(0, file.Size - offset));
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            // Opening/truncating must succeed before a new remote version can label
            // the partial file; otherwise an I/O failure could retag old bytes.
            if (responseTag is not null) await File.WriteAllTextAsync(tagPath, responseTag, cancellationToken).ConfigureAwait(false);
            else File.Delete(tagPath);
            var buffer = new byte[1024 * 64];
            var copied = offset;
            var lastReport = DateTime.UtcNow;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (count > file.Size - copied) throw new IOException("下载内容超过远端声明的大小。");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                copied += count;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250)
                { report(copied); lastReport = DateTime.UtcNow; }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (copied != file.Size) throw new IOException("下载大小与远端目录不一致，可重试。");
        }
        if (!await Verified(partial).ConfigureAwait(false)) { File.Delete(partial); File.Delete(tagPath); throw new IOException("文件 SHA-256 校验失败，未保存损坏文件。"); }
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
