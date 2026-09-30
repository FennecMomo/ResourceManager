using System.Net;
using System.Net.Http.Headers;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class DownloadSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ResourceManagerDownloadSafety", Guid.NewGuid().ToString("N"));
    private readonly PeerInfo peer = new("publisher", "127.0.0.1", 37642, "Publisher", null, null);
    private readonly RemoteResource resource = new("resource", "data.bin", ResourceKind.File, PublishMode.Reference,
        4, DateTimeOffset.UtcNow, true);

    [Fact]
    public void QueuedJobsReserveDistinctPathsAcrossManagersBeforeAnyDataExists()
    {
        var store = new NodeStore(Path.Combine(root, "store"));
        var client = new FakeClient(resource, (_, _) => throw new InvalidOperationException());
        var jobs = Enumerable.Range(0, 8).AsParallel().Select(_ =>
            new DownloadManager(store, client).CreateJob(peer, resource, Path.Combine(root, "downloads"))).ToArray();
        Assert.Equal(jobs.Length, jobs.Select(j => j.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(jobs, j => Assert.False(File.Exists(j.TargetPath)));
        var sidecar = new DownloadManager(store, client).CreateJob(peer, resource with { Name = "data.bin.rm-part" }, Path.Combine(root, "downloads"));
        Assert.DoesNotContain(jobs, j => sidecar.TargetPath == j.TargetPath + ".rm-part");
    }

    [Fact]
    public async Task RepeatedRangeRejectionStopsAfterOneFullRestart()
    {
        var client = new FakeClient(resource, (_, call) => call <= 3
            ? new(HttpStatusCode.RequestedRangeNotSatisfiable)
            : throw new IOException("Fixture stops an unbounded retry."));
        var (manager, job) = Setup(client);
        File.WriteAllText(job.TargetPath + ".rm-part", "AA");
        File.WriteAllText(job.TargetPath + ".rm-etag", "\"old\"");
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.RunAsync(job.Id));
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task MismatchedPartialRangeCannotBeAcceptedAsACompleteFile()
    {
        var client = new FakeClient(resource, (_, _) =>
        {
            var response = Response("BBBB", HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new(1, 4, 5);
            return response;
        });
        var (manager, job) = Setup(client);
        File.WriteAllText(job.TargetPath + ".rm-part", "AA");
        File.WriteAllText(job.TargetPath + ".rm-etag", "\"old\"");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RunAsync(job.Id));
        Assert.False(File.Exists(job.TargetPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedResponseIsRejectedBeforeWritingPastTheDeclaredSize(bool unknownLength)
    {
        var client = new FakeClient(resource, (_, _) => unknownLength
            ? new(HttpStatusCode.OK) { Content = new UnknownLengthContent("TOO-LONG"u8.ToArray()) }
            : Response("TOO-LONG"));
        var (manager, job) = Setup(client);
        await Assert.ThrowsAnyAsync<IOException>(() => manager.RunAsync(job.Id));
        Assert.False(File.Exists(job.TargetPath));
        Assert.True(!File.Exists(job.TargetPath + ".rm-part") || new FileInfo(job.TargetPath + ".rm-part").Length <= resource.Size);
    }

    [Fact]
    public async Task CompletePartialWithoutHashMustRevalidateTheRemoteVersion()
    {
        var client = new FakeClient(resource, (_, _) => Response("NEW!"));
        var (manager, job) = Setup(client);
        File.WriteAllText(job.TargetPath + ".rm-part", "OLD!");
        File.WriteAllText(job.TargetPath + ".rm-etag", "\"old\"");
        await manager.RunAsync(job.Id);
        Assert.Equal("NEW!", File.ReadAllText(job.TargetPath));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task FailedFullRestartMustNotRetagOldPartialBytes()
    {
        var client = new FakeClient(resource, (_, _) => Response("NEW!"));
        var (manager, job) = Setup(client);
        File.WriteAllText(job.TargetPath + ".rm-part", "OL");
        File.WriteAllText(job.TargetPath + ".rm-etag", "\"old\"");
        using var occupied = new FileStream(job.TargetPath + ".rm-part", FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => manager.RunAsync(job.Id));
        Assert.Equal("\"old\"", File.ReadAllText(job.TargetPath + ".rm-etag"));
    }

    private (DownloadManager Manager, DownloadJob Job) Setup(FakeClient client)
    {
        var store = new NodeStore(Path.Combine(root, "store"));
        store.UpsertPeer(peer);
        var manager = new DownloadManager(store, client);
        return (manager, manager.CreateJob(peer, resource, Path.Combine(root, "downloads")));
    }

    private static HttpResponseMessage Response(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content)) };
        response.Headers.ETag = new EntityTagHeaderValue("\"new\"");
        return response;
    }

    private sealed class FakeClient(RemoteResource resource, Func<long, int, HttpResponseMessage> response) : IResourceClient
    {
        public int Calls { get; private set; }
        public Task<RemoteResource?> GetResourceAsync(PeerInfo peer, string id, CancellationToken token = default) => Task.FromResult<RemoteResource?>(resource);
        public Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string id, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<RemoteFile>>([new("", resource.Size, resource.ModifiedUtc)]);
        public Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string id, string path, long offset, string? etag, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(response(offset, ++Calls)); }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
