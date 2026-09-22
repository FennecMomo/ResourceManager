using System.Net;
using System.Net.Sockets;
using ResourceManager.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ResourceManager.Tests;

public sealed class PeerIntegrationTests
{
    [Fact]
    public async Task LanDiscovery_FindsAnotherRunningDevice()
    {
        using var space = new TestSpace();
        var scannerStore = new NodeStore(space.PathFor("scanner"));
        var responderStore = new NodeStore(space.PathFor("responder"));
        responderStore.SaveSettings("设计部电脑", null, 40123, true);
        var discoveryPort = FreeUdpPort();
        await using var responder = new LanDiscoveryService(responderStore, discoveryPort);
        await using var scanner = new LanDiscoveryService(scannerStore, discoveryPort);
        await responder.StartAsync();

        var peers = await scanner.DiscoverAsync(TimeSpan.FromSeconds(2),
            broadcastAddresses: [IPAddress.Loopback]);

        var peer = Assert.Single(peers);
        Assert.Equal(responderStore.GetSettings().Profile.DeviceId, peer.DeviceId);
        Assert.Equal("设计部电脑", peer.Nickname);
        Assert.Equal("127.0.0.1", peer.Ip);
        Assert.Equal(40123, peer.Port);
    }

    [Fact]
    public void RemovingDownload_DeletesPartialData_ButKeepsCompletedFile()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("store"));
        using var client = new PeerClient(store);
        var downloads = new DownloadManager(store, client);

        var partialTarget = space.PathFor("output", "partial.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(partialTarget)!);
        File.WriteAllText(partialTarget + ".rm-part", "partial");
        File.WriteAllText(partialTarget + ".rm-etag", "tag");
        var partial = new DownloadJob("partial", "peer", "resource", "partial.zip", ResourceKind.File,
            partialTarget, "已暂停", 7, 100, null);
        store.SaveDownload(partial);
        downloads.RemoveJob(partial.Id, true);
        Assert.Null(store.GetDownload(partial.Id));
        Assert.False(File.Exists(partialTarget + ".rm-part"));
        Assert.False(File.Exists(partialTarget + ".rm-etag"));

        var completedTarget = space.Write("completed.zip", "complete");
        var completed = new DownloadJob("completed", "peer", "resource", "completed.zip", ResourceKind.File,
            completedTarget, "已完成", 8, 8, null);
        store.SaveDownload(completed);
        downloads.RemoveJob(completed.Id, false);
        Assert.Null(store.GetDownload(completed.Id));
        Assert.True(File.Exists(completedTarget));

        var folderTarget = space.PathFor("folder-download");
        Directory.CreateDirectory(folderTarget);
        File.WriteAllText(Path.Combine(folderTarget, "received.txt"), "partial folder");
        var folder = new DownloadJob("folder", "peer", "resource", "folder-download", ResourceKind.Folder,
            folderTarget, "已中断", 14, 100, null);
        store.SaveDownload(folder);
        downloads.RemoveJob(folder.Id, true);
        Assert.Null(store.GetDownload(folder.Id));
        Assert.False(Directory.Exists(folderTarget));
    }

    [Fact]
    public async Task Peers_ExchangeCatalog_AndDownloadFileAndFolder()
    {
        using var space = new TestSpace();
        var a = new NodeStore(space.PathFor("a"));
        var b = new NodeStore(space.PathFor("b"));
        var aFile = space.Write("a-public.txt", "来自 A");
        var bFile = space.Write("b-public.txt", "来自 B");
        var folder = space.PathFor("source-folder");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "nested", "readme.txt"), "文件夹内容");
        a.AddResource(aFile, PublishMode.Reference);
        var sharedFile = b.AddResource(bFile, PublishMode.Reference);
        var sharedFolder = b.AddResource(folder, PublishMode.Copy);
        var portA = FreePort();
        var portB = FreePort();
        a.SaveSettings("甲", null, portA, true);
        b.SaveSettings("乙", null, portB, true);
        await using var nodeA = new PeerNode(a);
        await using var nodeB = new PeerNode(b);
        await nodeA.StartAsync(portA, "127.0.0.1");
        await nodeB.StartAsync(portB, "127.0.0.1");
        using var clientA = new PeerClient(a);
        using var clientB = new PeerClient(b);
        var peerB = await clientA.ConnectAsync("127.0.0.1", portB);
        Assert.Equal("乙", peerB.Nickname);
        Assert.Equal("甲", b.GetPeers().Single().Nickname);
        var bCatalog = await clientA.GetResourcesAsync(peerB);
        Assert.Equal(2, bCatalog.Count);
        Assert.Contains(bCatalog, r => r.Id == sharedFile.Id && r.Available);
        Assert.Contains(bCatalog, r => r.Id == sharedFolder.Id && r.Kind == ResourceKind.Folder && r.Available);
        Assert.Single(await clientB.GetResourcesAsync(b.GetPeers().Single()));

        var downloads = new DownloadManager(a, clientA);
        var output = space.PathFor("output");
        var fileJob = downloads.CreateJob(peerB, bCatalog.Single(r => r.Id == sharedFile.Id), output);
        await downloads.RunAsync(fileJob.Id);
        Assert.Equal("来自 B", File.ReadAllText(fileJob.TargetPath));
        var folderJob = downloads.CreateJob(peerB, bCatalog.Single(r => r.Id == sharedFolder.Id), output);
        await downloads.RunAsync(folderJob.Id);
        Assert.Equal("文件夹内容", File.ReadAllText(Path.Combine(folderJob.TargetPath, "nested", "readme.txt")));
        Assert.True(Directory.Exists(Path.Combine(folderJob.TargetPath, "empty")));
    }

    [Fact]
    public async Task Favorites_RemainOffline_AndWithdrawnResourceIsDistinct()
    {
        using var space = new TestSpace();
        var a = new NodeStore(space.PathFor("a"));
        var b = new NodeStore(space.PathFor("b"));
        var source = space.Write("source.txt", "初始内容");
        var reference = b.AddResource(source, PublishMode.Reference);
        var copy = b.AddResource(source, PublishMode.Copy);
        File.WriteAllText(source, "修改后的内容更长");
        var catalog = new ResourceCatalog(b).List();
        Assert.True(catalog.Single(r => r.Id == reference.Id).Size > catalog.Single(r => r.Id == copy.Id).Size);
        File.Delete(source);
        catalog = new ResourceCatalog(b).List();
        Assert.False(catalog.Single(r => r.Id == reference.Id).Available);
        Assert.True(catalog.Single(r => r.Id == copy.Id).Available);
        var portA = FreePort();
        var portB = FreePort();
        a.SaveSettings("甲", null, portA, true);
        b.SaveSettings("乙", null, portB, true);
        await using var nodeA = new PeerNode(a);
        await using var nodeB = new PeerNode(b);
        await nodeA.StartAsync(portA, "127.0.0.1");
        await nodeB.StartAsync(portB, "127.0.0.1");
        using var clientA = new PeerClient(a);
        var peerB = await clientA.ConnectAsync("127.0.0.1", portB);
        a.SaveFavorite(new Favorite(peerB.DeviceId, reference.Id, reference.Name, ResourceKind.File));
        await nodeB.StopAsync();
        Assert.Single(a.GetFavorites());
        await Assert.ThrowsAnyAsync<Exception>(() => clientA.GetResourcesAsync(peerB));
        await nodeB.StartAsync(portB, "127.0.0.1");
        b.RemoveResource(reference.Id);
        Assert.DoesNotContain(await clientA.GetResourcesAsync(peerB), r => r.Id == reference.Id);
        Assert.Single(a.GetFavorites());
        Assert.NotNull(b.GetResource(copy.Id));
    }

    [Fact]
    public async Task PartialFile_ResumesWithRange_AndTraversalIsRejected()
    {
        using var space = new TestSpace();
        var a = new NodeStore(space.PathFor("a"));
        var b = new NodeStore(space.PathFor("b"));
        var source = space.PathFor("large.bin");
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(source, bytes);
        var shared = b.AddResource(source, PublishMode.Reference);
        var folder = space.PathFor("folder");
        Directory.CreateDirectory(folder);
        var publishedFolder = b.AddResource(folder, PublishMode.Reference);
        Assert.Throws<ArgumentException>(() => new ResourceCatalog(b).ResolveFile(publishedFolder.Id, "../large.bin"));
        var portA = FreePort();
        var portB = FreePort();
        a.SaveSettings("甲", null, portA, true);
        b.SaveSettings("乙", null, portB, true);
        await using var nodeA = new PeerNode(a);
        await using var nodeB = new PeerNode(b);
        await nodeA.StartAsync(portA, "127.0.0.1");
        await nodeB.StartAsync(portB, "127.0.0.1");
        using var clientA = new PeerClient(a);
        var peerB = await clientA.ConnectAsync("127.0.0.1", portB);
        var resource = (await clientA.GetResourcesAsync(peerB)).Single(r => r.Id == shared.Id);
        var downloads = new DownloadManager(a, clientA);
        var job = downloads.CreateJob(peerB, resource, space.PathFor("output"));
        using (var response = await clientA.OpenFileAsync(peerB, resource.Id, "", 0, null))
        {
            response.EnsureSuccessStatusCode();
            var tag = response.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(tag));
            await File.WriteAllTextAsync(job.TargetPath + ".rm-etag", tag);
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(job.TargetPath + ".rm-part");
            var prefix = new byte[128 * 1024];
            await input.ReadExactlyAsync(prefix);
            await output.WriteAsync(prefix);
        }
        await downloads.RunAsync(job.Id);
        Assert.Equal(bytes, File.ReadAllBytes(job.TargetPath));
        Assert.False(File.Exists(job.TargetPath + ".rm-part"));
        Assert.Equal("已完成", a.GetDownload(job.Id)?.Status);
    }

    [Fact]
    public async Task Download_DoesNotCaptureCallingSynchronizationContext()
    {
        using var space = new TestSpace();
        var receiver = new NodeStore(space.PathFor("receiver"));
        var publisher = new NodeStore(space.PathFor("publisher"));
        var source = space.PathFor("background.bin");
        var bytes = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(source, bytes);
        publisher.AddResource(source, PublishMode.Reference);
        var port = FreePort();
        publisher.SaveSettings("发布者", null, port, true);
        await using var node = new PeerNode(publisher);
        await node.StartAsync(port, "127.0.0.1");
        using var client = new PeerClient(receiver);
        var peer = await client.ConnectAsync("127.0.0.1", port);
        var resource = Assert.Single(await client.GetResourcesAsync(peer));
        var downloads = new DownloadManager(receiver, client);
        var job = downloads.CreateJob(peer, resource, space.PathFor("output"));

        var previous = SynchronizationContext.Current;
        var context = new RecordingSynchronizationContext();
        Task<DownloadJob> run;
        SynchronizationContext.SetSynchronizationContext(context);
        try { run = downloads.RunAsync(job.Id); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        await run;
        Assert.Equal(0, context.PostCount);
        Assert.Equal(bytes, File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task PortConflict_ReportsFailure_WithoutStartingSecondNode()
    {
        using var space = new TestSpace();
        var port = FreePort();
        await using var first = new PeerNode(new NodeStore(space.PathFor("a")));
        await using var second = new PeerNode(new NodeStore(space.PathFor("b")));
        await first.StartAsync(port, "127.0.0.1");
        await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync(port, "127.0.0.1"));
        Assert.True(first.IsRunning);
        Assert.False(second.IsRunning);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int postCount;
        public int PostCount => Volatile.Read(ref postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref postCount);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    private sealed class TestSpace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ResourceManagerTests", Guid.NewGuid().ToString("N"));
        public string PathFor(params string[] names) => Path.Combine([root, .. names]);
        public string Write(string name, string text)
        {
            Directory.CreateDirectory(root);
            var path = PathFor(name);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
