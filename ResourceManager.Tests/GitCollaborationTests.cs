using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class GitCollaborationTests
{
    [Fact]
    public async Task ProjectEvents_ConvergeAfterCreatorWasOffline_AndBundleCanBeRelayed()
    {
        using var space = new GitTestSpace();
        var git = await GitEnvironment.DetectAsync();
        if (git is null) return;
        var repository = new GitRepositoryService(git);
        var source = space.PathFor("A1");
        Directory.CreateDirectory(source);
        await git.RunAsync(source, default, "init", "-b", "main");
        File.WriteAllText(Path.Combine(source, "file.txt"), "initial");
        await CommitAsync(git, source, "A1");
        File.WriteAllText(Path.Combine(source, "file.txt"), "second");
        await CommitAsync(git, source, "A2");

        var a = new GitCollaborationStore(space.PathFor("a-data"));
        var b = new GitCollaborationStore(space.PathFor("b-data"));
        var c = new GitCollaborationStore(space.PathFor("c-data"));
        var projectId = Guid.NewGuid().ToString("N");
        var initial = await repository.InspectAsync(source, default);
        var aBundle = await repository.CreateBundleAsync(source, "main", a.BundleDirectory, default);
        var created = a.CreateEvent(projectId, "base", "main", "A-device", "A", "Created",
            initial.Head, null, await repository.RecentCommitsAsync(source, initial.Head, default), aBundle);
        Assert.Equal(2, created.Commits.Length);
        Assert.Equal(1, b.Merge(a.GetEvents()));
        Assert.Equal(1, c.Merge(a.GetEvents()));

        var b1 = space.PathFor("B1");
        await repository.CloneAsync(aBundle.Path, "main", b1, default);
        b.SetBinding(new GitProjectBinding(projectId, b1, "main"));
        b.CreateEvent(projectId, "base", "main", "B-device", "B", "Joined",
            initial.Head, initial.Head, [], aBundle);
        File.WriteAllText(Path.Combine(b1, "file.txt"), "B change");
        await CommitAsync(git, b1, "B commit");
        var bHead = (await repository.InspectAsync(b1, default)).Head;
        var bBundle = await repository.CreateBundleAsync(b1, "main", b.BundleDirectory, default);
        b.CreateEvent(projectId, "base", "main", "B-device", "B", "Published",
            bHead, initial.Head, await repository.RecentCommitsAsync(b1, bHead, default), bBundle);

        Assert.Equal(2, c.Merge(b.GetEvents()));
        var c1 = space.PathFor("C1");
        await repository.CloneAsync(aBundle.Path, "main", c1, default);
        c.CreateEvent(projectId, "base", "main", "C-device", "C", "Joined",
            initial.Head, initial.Head, [], aBundle);
        await repository.FetchIntoInboxAsync(c1, bBundle.Path, "main", b.MemberId, default);
        Assert.True(await repository.IsAncestorAsync(c1, initial.Head, bHead, default));
        await git.RunAsync(c1, default, "merge", "--ff-only", bHead);
        c.CreateEvent(projectId, "base", "main", "C-device", "C", "Synced", bHead, bHead, [], null);
        File.WriteAllText(Path.Combine(c1, "c.txt"), "C change");
        await CommitAsync(git, c1, "C commit");
        var cHead = (await repository.InspectAsync(c1, default)).Head;
        var cBundle = await repository.CreateBundleAsync(c1, "main", c.BundleDirectory, default);
        c.CreateEvent(projectId, "base", "main", "C-device", "C", "Published",
            cHead, bHead, await repository.RecentCommitsAsync(c1, cHead, default), cBundle);

        // A misses B's push but later meets C. C forwards B's signed line as well as its own.
        Assert.Equal(5, a.Merge(c.GetEvents()));
        Assert.Equal(6, a.GetEvents(projectId).Count);
        Assert.Equal(bHead, a.GetEvents(projectId).Single(item => item.MemberId == b.MemberId && item.Kind == "Published").CommitHash);
        Assert.Equal(cHead, a.GetEvents(projectId).Single(item => item.MemberId == c.MemberId && item.Kind == "Published").CommitHash);
        Assert.Equal(6, new GitCollaborationStore(space.PathFor("a-data")).GetEvents(projectId).Count);
    }

    [Fact]
    public async Task KnownPeer_ExchangesSignedEvents_AndDownloadsVerifiedBundle()
    {
        using var space = new GitTestSpace();
        var git = await GitEnvironment.DetectAsync();
        if (git is null) return;
        var repository = new GitRepositoryService(git);
        var source = space.PathFor("source");
        Directory.CreateDirectory(source);
        await git.RunAsync(source, default, "init", "-b", "main");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        await CommitAsync(git, source, "Initial");
        var publisher = new NodeStore(space.PathFor("publisher"));
        var receiver = new NodeStore(space.PathFor("receiver"));
        var projects = new GitCollaborationStore(publisher.DataDirectory);
        var inspected = await repository.InspectAsync(source, default);
        var bundle = await repository.CreateBundleAsync(source, "main", projects.BundleDirectory, default);
        projects.CreateEvent(Guid.NewGuid().ToString("N"), "base", "main", publisher.GetSettings().Profile.DeviceId,
            "A", "Created", inspected.Head, null,
            await repository.RecentCommitsAsync(source, inspected.Head, default), bundle);
        var port = FreePort();
        publisher.SaveSettings("A", null, port, true);
        await using var node = new PeerNode(publisher, new LanDiscoveryService(publisher),
            new UpnpPortMappingManager(publisher, new UpnpGatewayClient()), gitProjects: projects);
        await node.StartAsync(port, "127.0.0.1");
        using var client = new PeerClient(receiver);
        var peer = await client.ConnectAsync("127.0.0.1", port);
        var response = await client.SyncGitAsync(peer, [], []);
        Assert.Single(response.Events);
        var target = space.PathFor("download", bundle.Hash + ".bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var bytes = File.ReadAllBytes(bundle.Path);
        File.WriteAllBytes(target + ".download", bytes[..(bytes.Length / 2)]);
        var received = await client.DownloadGitBundleAsync(peer, bundle with { Path = target });
        Assert.Equal(bytes, File.ReadAllBytes(received));
        File.WriteAllBytes(target + ".download", bytes);
        File.Delete(target);
        received = await client.DownloadGitBundleAsync(peer, bundle with { Path = target });
        Assert.Equal(bytes, File.ReadAllBytes(received));
    }

    [Fact]
    public void InvalidOrImpersonatedEvents_AreRejected_AndCorruptStateIsPreserved()
    {
        using var space = new GitTestSpace();
        var a = new GitCollaborationStore(space.PathFor("a"));
        var b = new GitCollaborationStore(space.PathFor("b"));
        var c = new GitCollaborationStore(space.PathFor("c"));
        const string head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projectId = Guid.NewGuid().ToString("N");
        var commit = new GitCommitPoint(head, [], "first", DateTimeOffset.UtcNow);
        var bundle = new GitBundleInfo(new string('b', 64), 100, "unused");
        var created = a.CreateEvent(projectId, "base", "main", "device-a", "A", "Created",
            head, null, [commit], bundle);
        Assert.Equal(0, b.Merge([created with { ProjectName = "forged" }]));
        Assert.Equal(1, b.Merge([created]));
        Assert.Equal(0, b.Merge([created with { Sequence = 2 }]));
        c.Merge([created]);
        Assert.Throws<InvalidOperationException>(() => c.CreateEvent(projectId, "base", "main",
            "device-a", "Fake A", "Joined", head, head, [], bundle));

        var statePath = space.PathFor("damaged", "git-collaboration", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, "{bad json");
        var recovered = new GitCollaborationStore(space.PathFor("damaged"));
        Assert.Empty(recovered.GetEvents());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(statePath)!, "state.json.invalid-*"));
    }

    [Fact]
    public void OrdinaryFolderSharing_DoesNotExposeGitMetadata()
    {
        using var space = new GitTestSpace();
        var root = space.PathFor("repository");
        var gitMetadata = Path.Combine(root, ".git");
        Directory.CreateDirectory(gitMetadata);
        File.WriteAllText(Path.Combine(root, "readme.txt"), "public");
        File.WriteAllText(Path.Combine(gitMetadata, "config"), "private-remote-url");
        var node = new NodeStore(space.PathFor("node"));
        var published = node.AddResource(root, PublishMode.Reference);
        var catalog = new ResourceCatalog(node);
        Assert.Contains(catalog.ListFiles(published.Id), item => item.RelativePath == "readme.txt");
        Assert.DoesNotContain(catalog.ListFiles(published.Id), item => item.RelativePath.Contains(".git"));
        Assert.Throws<ArgumentException>(() => catalog.ResolveFile(published.Id, ".git/config"));
        Assert.Throws<ArgumentException>(() => node.AddResource(gitMetadata, PublishMode.Reference));
        var copied = node.AddResource(root, PublishMode.Copy);
        Assert.False(Directory.Exists(Path.Combine(copied.SourcePath, ".git")));
    }

    [Fact]
    public async Task LfsObjects_ArePackagedAndRestoredAlongsideGitHistory()
    {
        using var space = new GitTestSpace();
        var git = await GitEnvironment.DetectAsync();
        if (git is null) return;
        try { await git.RunAsync(null, default, "lfs", "version"); }
        catch (InvalidOperationException) { return; }
        var root = space.PathFor("lfs-attributes-only");
        Directory.CreateDirectory(root);
        await git.RunAsync(root, default, "init", "-b", "main");
        File.WriteAllText(Path.Combine(root, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "No LFS objects yet");
        await CommitAsync(git, root, "Attributes only");
        var repository = new GitRepositoryService(git);
        var inspected = await repository.InspectAsync(root, default);
        Assert.Equal("main", inspected.Branch);

        File.WriteAllBytes(Path.Combine(root, "large.bin"), new byte[] { 1, 2, 3, 4 });
        await CommitAsync(git, root, "LFS object");
        var first = await repository.InspectAsync(root, default);
        var bundle = await repository.CreateBundleAsync(root, first.Branch, space.PathFor("bundles"), default);
        using (var stream = File.OpenRead(bundle.Path))
            Assert.Equal((int)'P', stream.ReadByte());
        var clone = space.PathFor("clone");
        await repository.CloneAsync(bundle.Path, first.Branch, clone, default);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(clone, "large.bin")));

        File.WriteAllBytes(Path.Combine(root, "large.bin"), new byte[] { 5, 6, 7, 8, 9 });
        await CommitAsync(git, root, "New LFS object");
        var second = await repository.InspectAsync(root, default);
        var nextBundle = await repository.CreateBundleAsync(root, second.Branch, space.PathFor("bundles"), default);
        using (var archive = ZipFile.OpenRead(nextBundle.Path))
            Assert.Equal(3, archive.Entries.Count); // Git history plus both LFS revisions.
        var memberId = new string('c', 24);
        await repository.FetchIntoInboxAsync(clone, nextBundle.Path, "main", memberId, default);
        await git.RunAsync(clone, default, "merge", "--ff-only", second.Head);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, File.ReadAllBytes(Path.Combine(clone, "large.bin")));
    }

    private static async Task CommitAsync(GitCommandRunner git, string root, string message)
    {
        await git.RunAsync(root, default, "add", ".");
        await git.RunAsync(root, default, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", message);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class GitTestSpace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ResourceManagerGitTests", Guid.NewGuid().ToString("N"));
        public string PathFor(params string[] names) => Path.Combine([root, .. names]);
        public void Dispose()
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
