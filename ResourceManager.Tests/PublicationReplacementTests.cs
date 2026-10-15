using System.Net;
using System.Net.Sockets;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class PublicationReplacementTests
{
    private sealed class TestSpace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerReplacementTests", Guid.NewGuid().ToString("N"));
        public string PathFor(params string[] parts) => Path.Combine([Root, .. parts]);
        public string Write(string name, string contents)
        {
            var path = PathFor(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [Theory]
    [InlineData(PublishMode.Copy)]
    [InlineData(PublishMode.Reference)]
    public void ReplacementPreservesIdentityMetadataAndOriginal(PublishMode mode)
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("data"));
        var group = store.SaveResourceGroup("private");
        store.SetGroupPermission(group.Id, GroupAccess.Private, []);
        var original = space.Write("old.txt", "old");
        var replacement = space.Write("new.txt", "new");
        var resource = store.AddResource(original, mode, groupId: group.Id);
        store.SetResourceNote(resource.Id, "keep");
        var result = store.ReplaceResourceReference(resource.Id, replacement);
        var reloaded = new NodeStore(store.DataDirectory).GetResource(resource.Id)!;
        Assert.Equal(resource.Id, reloaded.Id);
        Assert.Equal(resource.Name, reloaded.Name);
        Assert.Equal("keep", reloaded.Note);
        Assert.Equal(group.Id, reloaded.GroupId);
        Assert.False(store.CanAccessGroup(group.Id, null));
        Assert.Equal(PublishMode.Reference, reloaded.Mode);
        Assert.Equal(replacement, reloaded.SourcePath);
        Assert.Equal("old", File.ReadAllText(original));
        Assert.Equal("new", File.ReadAllText(new ResourceCatalog(store).ResolveFile(resource.Id, "").FullName));
        Assert.Null(result.CleanupWarning);
        Assert.Equal(mode == PublishMode.Copy, result.ManagedCopyDeleted);
        if (mode == PublishMode.Copy) Assert.False(Directory.Exists(Path.GetDirectoryName(resource.SourcePath)));
    }

    [Fact]
    public void InvalidReplacementLeavesPublicationAndCopyUntouched()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("data"));
        var resource = store.AddResource(space.Write("old.txt", "old"), PublishMode.Copy);
        Assert.Throws<FileNotFoundException>(() => store.ReplaceResourceReference(resource.Id, space.PathFor("missing")));
        Assert.Throws<ArgumentException>(() => store.ReplaceResourceReference(resource.Id, space.Root));
        Assert.Throws<ArgumentException>(() => store.ReplaceResourceReference(resource.Id, resource.SourcePath));
        Assert.Throws<ArgumentException>(() => store.ReplaceResourceReference(resource.Id, space.Write(".git/secret", "secret")));
        Assert.Equal(resource, store.GetResource(resource.Id));
        Assert.True(File.Exists(resource.SourcePath));
    }

    [Fact]
    public void SharedCopyIsRetainedAndReportedAfterSuccessfulSwitch()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("data"));
        var copy = store.AddResource(space.Write("old.txt", "old"), PublishMode.Copy);
        var other = store.AddResource(copy.SourcePath, PublishMode.Reference);
        var result = store.ReplaceResourceReference(copy.Id, space.Write("new.txt", "new"));
        Assert.NotNull(result.CleanupWarning);
        Assert.False(result.ManagedCopyDeleted);
        Assert.Equal("old", File.ReadAllText(new ResourceCatalog(store).ResolveFile(other.Id, "").FullName));
        Assert.Equal(PublishMode.Reference, store.GetResource(copy.Id)!.Mode);
    }

    [Fact]
    public void FolderReplacementDeletesOnlyManagedCopyAndKeepsChildrenResolvable()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("data"));
        space.Write("old/child.txt", "old");
        space.Write("new/child.txt", "new");
        var resource = store.AddResource(space.PathFor("old"), PublishMode.Copy);
        var result = store.ReplaceResourceReference(resource.Id, space.PathFor("new"));
        Assert.True(result.ManagedCopyDeleted);
        Assert.False(Directory.Exists(resource.SourcePath));
        Assert.True(File.Exists(space.PathFor("old", "child.txt")));
        Assert.Equal("new", File.ReadAllText(new ResourceCatalog(store).ResolveFile(resource.Id, "child.txt").FullName));
    }

    [Fact]
    public void LockedCopyReportsCleanupFailureWithoutRollingBackReference()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("data"));
        var resource = store.AddResource(space.Write("old.txt", "old"), PublishMode.Copy);
        using var held = new FileStream(resource.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = store.ReplaceResourceReference(resource.Id, space.Write("new.txt", "new"));
        Assert.NotNull(result.CleanupWarning);
        Assert.False(result.ManagedCopyDeleted);
        Assert.Equal(PublishMode.Reference, store.GetResource(resource.Id)!.Mode);
        Assert.True(File.Exists(resource.SourcePath));
    }

    [Fact]
    public async Task SameSizeAndTimestampReplacementInvalidatesResumeTag()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.PathFor("data"));
        var oldPath = space.Write("old.txt", "AAAA");
        var newPath = space.Write("new.txt", "BBBB");
        File.SetLastWriteTimeUtc(newPath, File.GetLastWriteTimeUtc(oldPath));
        var resource = store.AddResource(oldPath, PublishMode.Copy);
        File.SetLastWriteTimeUtc(newPath, File.GetLastWriteTimeUtc(resource.SourcePath));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        store.SaveSettings("test", null, port, true);
        await using var node = new PeerNode(store);
        await node.StartAsync(port, "127.0.0.1");
        using var client = new PeerClient(new NodeStore(space.PathFor("receiver")));
        var peer = await client.ConnectAsync("127.0.0.1", port);
        using var before = await client.OpenFileAsync(peer, resource.Id, "", 0, null);
        before.EnsureSuccessStatusCode();
        Assert.Equal("AAAA", await before.Content.ReadAsStringAsync());
        store.ReplaceResourceReference(resource.Id, newPath);
        using var after = await client.OpenFileAsync(peer, resource.Id, "", 2, before.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(before.Headers.ETag, after.Headers.ETag);
        Assert.Equal("BBBB", await after.Content.ReadAsStringAsync());
    }
}
