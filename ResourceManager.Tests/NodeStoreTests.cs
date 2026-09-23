using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class NodeStoreTests
{
    [Fact]
    public void ResourceNote_RemovedWithResourceAndNotInheritedOnRepublish()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.Root);
        var source = space.Write("note.txt", "内容");
        var first = store.AddResource(source, PublishMode.Reference);
        store.SetResourceNote(first.Id, "第一次备注");
        Assert.Equal("第一次备注", store.GetResource(first.Id)!.Note);

        store.RemoveResource(first.Id);
        var second = store.AddResource(source, PublishMode.Reference);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("", second.Note);
        Assert.Equal("", store.GetResource(second.Id)!.Note);
    }

    [Fact]
    public void ResourceNote_NormalizesPlainTextAndLimitsLength()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.Root);
        var resource = store.AddResource(space.Write("note.txt", "内容"), PublishMode.Reference);

        store.SetResourceNote(resource.Id, "  第一行\r\n第二行\t结尾  ");
        Assert.Equal("第一行 第二行 结尾", store.GetResource(resource.Id)!.Note);

        store.SetResourceNote(resource.Id, new string('长', 200));
        Assert.Equal(200, store.GetResource(resource.Id)!.Note.Length);
        Assert.Throws<ArgumentException>(() => store.SetResourceNote(resource.Id, new string('长', 201)));
        Assert.Throws<InvalidOperationException>(() => store.SetResourceNote("missing-resource", "备注"));
    }

    [Fact]
    public void RemoteResourceWithoutNote_DeserializesToEmptyString()
    {
        const string json = """
            {"id":"r1","name":"资料","kind":"File","mode":"Reference","size":10,"modifiedUtc":"2026-09-20T00:00:00+00:00","available":true}
            """;

        var resource = JsonSerializer.Deserialize<RemoteResource>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(resource);
        Assert.Equal("", resource.Note);
    }

    [Fact]
    public void LegacyResourcesWithoutNotes_UpgradeToEmptyNote()
    {
        using var space = new TestSpace();
        Directory.CreateDirectory(space.Root);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(space.Root, "resources.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE resources (id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, mode TEXT NOT NULL, source_path TEXT NOT NULL, published_utc TEXT NOT NULL);
                INSERT INTO resources VALUES('legacy-resource','旧资料','File','Reference','C:\\old.txt','2026-09-20T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var store = new NodeStore(space.Root);

        var resource = Assert.Single(store.GetResources());
        Assert.Equal("旧资料", resource.Name);
        Assert.Equal("", resource.Note);
        store.SetResourceNote(resource.Id, "迁移后备注");
        Assert.Equal("迁移后备注", store.GetResource(resource.Id)!.Note);
    }

    [Fact]
    public void PeerNote_SurvivesNicknameAndAddressChanges()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.Root);
        var peer = new PeerInfo("device-1", "10.0.0.5", 37642, "旧昵称", null, DateTimeOffset.UtcNow);
        store.UpsertPeer(peer);
        store.SavePeerNote(peer.DeviceId, "设计部张工");

        store.UpsertPeer(peer with { Nickname = "新昵称", Ip = "10.0.0.9", Port = 38000 });

        Assert.Equal("设计部张工", store.GetPeerNote(peer.DeviceId));
        Assert.Equal("新昵称", Assert.Single(store.GetPeers()).Nickname);
    }

    [Fact]
    public void PeerNote_RemovedWithPeerAndFavorites()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.Root);
        var peer = new PeerInfo("device-1", "10.0.0.5", 37642, "同事", null, DateTimeOffset.UtcNow);
        store.UpsertPeer(peer);
        store.SavePeerNote(peer.DeviceId, "备注");
        store.SaveFavorite(new Favorite(peer.DeviceId, "resource-1", "资料", ResourceKind.File));

        store.RemovePeer(peer.DeviceId);

        Assert.Empty(store.GetPeers());
        Assert.Equal("", store.GetPeerNote(peer.DeviceId));
        Assert.Empty(store.GetFavorites());
    }

    [Fact]
    public void PeerNote_NormalizesPlainTextAndLimitsLength()
    {
        using var space = new TestSpace();
        var store = new NodeStore(space.Root);
        var peer = new PeerInfo("device-1", "10.0.0.5", 37642, "同事", null, DateTimeOffset.UtcNow);
        store.UpsertPeer(peer);

        store.SavePeerNote(peer.DeviceId, "  第一行\r\n第二行\t结尾  ");
        Assert.Equal("第一行 第二行 结尾", store.GetPeerNote(peer.DeviceId));

        store.SavePeerNote(peer.DeviceId, new string('长', 100));
        Assert.Equal(100, store.GetPeerNote(peer.DeviceId).Length);
        Assert.Throws<ArgumentException>(() => store.SavePeerNote(peer.DeviceId, new string('长', 101)));

        store.SavePeerNote(peer.DeviceId, "   ");
        Assert.Equal("", store.GetPeerNote(peer.DeviceId));

        Assert.Throws<InvalidOperationException>(() => store.SavePeerNote("missing-device", "备注"));
    }

    [Fact]
    public void LegacyPeersWithoutNotes_UpgradeToEmptyNote()
    {
        using var space = new TestSpace();
        Directory.CreateDirectory(space.Root);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(space.Root, "resources.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE peers (device_id TEXT PRIMARY KEY, ip TEXT NOT NULL, port INTEGER NOT NULL, nickname TEXT NOT NULL, avatar BLOB, last_seen TEXT);
                INSERT INTO peers VALUES('legacy-peer','10.0.0.8',37642,'旧设备',NULL,NULL);
                """;
            command.ExecuteNonQuery();
        }

        var store = new NodeStore(space.Root);

        Assert.Equal("旧设备", Assert.Single(store.GetPeers()).Nickname);
        Assert.Equal("", store.GetPeerNote("legacy-peer"));
        store.SavePeerNote("legacy-peer", "迁移后备注");
        Assert.Equal("迁移后备注", store.GetPeerNote("legacy-peer"));
    }

    private sealed class TestSpace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerTests", Guid.NewGuid().ToString("N"));

        public string Write(string name, string text)
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
