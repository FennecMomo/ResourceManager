using System.Net;
using Microsoft.Data.Sqlite;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed partial class ChatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateResource_DeliversAndDownloadsAfterRestart_WithoutPublicExposure(bool folder)
    {
        using var room = new Room();
        var a = room.Store("a");
        var b = room.Store("b");
        var portA = FreePort();
        var portB = FreePort();
        a.SaveSettings("A", null, portA, true);
        b.SaveSettings("B", null, portB, true);
        using var clientA = new PeerClient(a, supportsChat: true);
        using var clientB = new PeerClient(b, supportsChat: true);
        var chatA = new ChatService(a, clientA);
        var chatB = new ChatService(b, clientB);
        await using var nodeA = ChatNode(a, chatA);
        await using var nodeB = ChatNode(b, chatB);
        await nodeA.StartAsync(portA, "127.0.0.1");
        await nodeB.StartAsync(portB, "127.0.0.1");
        var peerB = await clientA.ConnectAsync("127.0.0.1", portB);
        var peerA = b.GetPeer(a.GetSettings().Profile.DeviceId)!;
        var source = room.Write("private.txt", "private snapshot contents");
        if (folder)
        {
            var directory = Path.Combine(Path.GetDirectoryName(source)!, "private-folder");
            Directory.CreateDirectory(Path.Combine(directory, "empty"));
            File.Copy(source, Path.Combine(directory, "private.txt"));
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            File.WriteAllText(Path.Combine(directory, ".git", "config"), "must not transfer");
            source = directory;
        }
        await nodeB.StopAsync();
        var queued = chatA.QueuePrivateResource(peerB.DeviceId, source);
        await chatA.PumpAsync();
        Assert.Equal("Queued", a.GetChatMessage(peerB.DeviceId, queued.MessageId, true)!.State);
        if (!folder) File.WriteAllText(source, "changed after sending");
        Assert.Empty(a.GetResources());
        // The pre-0.4.4 SQL query must also stay empty when an older EXE opens this profile.
        using (var db = new SqliteConnection($"Data Source={Path.Combine(a.DataDirectory, "resources.db")};Pooling=False"))
        {
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM resources";
            Assert.Equal(0L, query.ExecuteScalar());
        }
        Assert.Null(a.GetResource(queued.ResourceId!));
        Assert.Empty(await clientB.GetResourcesAsync(peerA));
        Assert.Null(await new ResourceCatalog(a).FindLatestUpdateAsync());
        // Reopen the store and service: durable private IDs survive a sender restart.
        var reopened = new NodeStore(a.DataDirectory);
        var restarted = new ChatService(reopened, clientA);
        await nodeB.StartAsync(portB, "127.0.0.1");
        reopened.WakeChatMessages(peerB.DeviceId);
        await restarted.PumpAsync();
        Assert.Equal("Delivered", reopened.GetChatMessage(peerB.DeviceId, queued.MessageId, true)!.State);
        var received = Assert.Single(b.GetChatMessages(peerA.DeviceId));
        Assert.Equal("PrivateResource", received.Kind);
        Assert.Equal(queued.ResourceName, received.ResourceName);
        var resource = await clientB.GetResourceAsync(peerA, received.ResourceId!);
        Assert.NotNull(resource);
        var files = await clientB.GetFilesAsync(peerA, resource.Id);
        Assert.DoesNotContain(files, file => file.RelativePath.Contains(".git"));

        // Ordinary endpoints cannot resolve even a known private ID. Another registered peer cannot use it.
        using var http = new HttpClient();
        var root = $"http://127.0.0.1:{portA}/api/v1/";
        foreach (var suffix in new[] { "tree", "content" })
        {
            using var denied = await http.GetAsync(root + $"resources/{resource.Id}/{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        a.UpsertPeer(new PeerInfo("third", "127.0.0.1", 12345, "C", null, null));
        foreach (var recipient in new[] { "", "third" })
        foreach (var suffix in new[] { "", "/tree", "/content" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, root + $"chat/resources/{resource.Id}{suffix}");
            if (recipient.Length > 0) request.Headers.Add("X-ResourceManager-Recipient", recipient);
            using var denied = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using (var deniedRequest = new HttpRequestMessage(HttpMethod.Get, root + $"chat/resources/{resource.Id}"))
        {
            deniedRequest.Headers.Add("X-ResourceManager-Recipient", peerB.DeviceId);
            // Removing the recipient also revokes access even when it still has the capability.
            a.RemovePeer(peerB.DeviceId);
            using var denied = await http.SendAsync(deniedRequest);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            await clientA.ConnectAsync("127.0.0.1", portB);
        }
        var relative = folder ? "private.txt" : "";
        using (var ranged = await clientB.OpenFileAsync(peerA, resource.Id, relative, 8, null))
        {
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("snapshot contents", await ranged.Content.ReadAsStringAsync());
        }
        if (folder)
        {
            using var traversal = await clientB.OpenFileAsync(peerA, resource.Id, "../private.txt", 0, null);
            Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        }
        var downloads = new DownloadManager(b, clientB);
        var job = downloads.CreateJob(peerA, resource, Path.Combine(b.DataDirectory, "downloads"), folder ? null : "saved-as.txt");
        var done = await downloads.RunAsync(job.Id);
        Assert.Equal("已完成", done.Status);
        Assert.Equal("private snapshot contents", File.ReadAllText(folder ? Path.Combine(done.TargetPath, "private.txt") : done.TargetPath));
        if (!folder) Assert.Equal("saved-as.txt", Path.GetFileName(done.TargetPath));
        if (folder) Assert.True(Directory.Exists(Path.Combine(done.TargetPath, "empty")));
        var storedPath = reopened.GetPrivateResource(resource.Id, peerB.DeviceId)!.SourcePath;
        reopened.ClearChatConversation(peerB.DeviceId);
        Assert.Null(await clientB.GetResourceAsync(peerA, resource.Id));
        Assert.False(File.Exists(storedPath) || Directory.Exists(storedPath));
        Assert.Single(b.GetChatMessages(peerA.DeviceId));
    }

    [Fact]
    public void PrivateResource_RejectsLegacyPeer_AndCancelRevokesAndRemovesSnapshot()
    {
        using var room = new Room();
        var store = room.Store("a");
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 12345, "B", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability]);
        using var client = new PeerClient(store, supportsChat: true);
        var chat = new ChatService(store, client);
        var source = room.Write("file.txt", "original");
        Assert.Throws<InvalidOperationException>(() => chat.QueuePrivateResource("b", source));
        Assert.Empty(store.GetResources("b"));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        var message = chat.QueuePrivateResource("b", source);
        var copy = store.GetPrivateResource(message.ResourceId!, "b")!;
        Assert.True(File.Exists(copy.SourcePath));
        chat.Cancel("b", message.MessageId);
        Assert.Null(store.GetPrivateResource(copy.Id, "b"));
        Assert.False(File.Exists(copy.SourcePath));
        Assert.True(File.Exists(source));
        var another = chat.QueuePrivateResource("b", source);
        var anotherCopy = store.GetPrivateResource(another.ResourceId!, "b")!;
        store.RemovePeer("b", deleteChatHistory: true);
        Assert.False(File.Exists(anotherCopy.SourcePath));
        Assert.Empty(store.GetResources("b"));
    }

    [Fact]
    public async Task PrivateResource_MigrationPreservesSnapshotAndRecipientScope()
    {
        using var room = new Room();
        var store = room.Store("a");
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 12345, "B", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        using var client = new PeerClient(store, supportsChat: true);
        var chat = new ChatService(store, client);
        var message = chat.QueuePrivateResource("b", room.Write("migrate.txt", "retained"));
        var parent = Path.GetDirectoryName(store.DataDirectory)!;
        var location = new StorageLocation(Path.Combine(parent, "bootstrap", "storage.json"));
        var pending = location.ScheduleMigration(new StorageConfiguration(store.DataDirectory, true), Path.Combine(parent, "migrated"));
        var result = await location.CompleteMigrationAsync(pending);
        var migrated = new NodeStore(result.Directory);
        var copy = migrated.GetPrivateResource(message.ResourceId!, "b")!;
        Assert.True(StorageLocation.IsWithin(copy.SourcePath, result.Directory));
        Assert.Equal("retained", File.ReadAllText(copy.SourcePath));
        Assert.Empty(migrated.GetResources());
        Assert.Null(migrated.GetPrivateResource(copy.Id, "other"));
    }
}
