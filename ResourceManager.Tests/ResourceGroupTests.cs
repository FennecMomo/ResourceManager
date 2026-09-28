using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class ResourceGroupTests
{
    [Fact]
    public async Task NewClientReadsLegacyCatalogWithNoGroupFields()
    {
        using var space = new Space(); var store = space.Store();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var legacy = builder.Build();
        legacy.MapGet("/api/v1/health", () => Results.Json(new PeerHello("legacy", "Old client", 1, null, [])));
        legacy.MapGet("/api/v1/resources", () => Results.Text("""
            [{"id":"old-file","name":"old.txt","kind":"File","mode":"Reference","size":7,"modifiedUtc":"2026-01-01T00:00:00Z","available":true,"note":"old note"}]
            """, "application/json"));
        await legacy.StartAsync();
        var peer = new PeerInfo("legacy", "127.0.0.1", new Uri(legacy.Urls.Single()).Port, "Old", null, null);
        store.UpsertPeer(peer);
        using var client = new PeerClient(store); var catalog = await client.GetCatalogAsync(peer);
        Assert.Equal("default", Assert.Single(catalog.Groups).Id);
        Assert.Equal("default", Assert.Single(catalog.Resources).GroupId);
        Assert.Equal("old note", catalog.Resources[0].Note);
    }

    [Fact]
    public void UpgradeAssignsDefaultGroupWithoutChangingResourceOrFavorite()
    {
        using var space = new Space();
        var store = space.Store();
        var resource = store.AddResource(space.File("old.txt"), PublishMode.Reference);
        store.SaveFavorite(new("peer", resource.Id, resource.Name, resource.Kind));
        using (var db = new SqliteConnection($"Data Source={Path.Combine(store.DataDirectory, "resources.db")}"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE resources DROP COLUMN group_id; DROP TABLE resource_groups;";
            command.ExecuteNonQuery();
        }
        var upgraded = space.Store();
        Assert.Equal(resource with { GroupId = "default" }, upgraded.GetResource(resource.Id));
        Assert.Equal(resource.Id, Assert.Single(upgraded.GetFavorites()).ResourceId);
        Assert.Equal("默认组", Assert.Single(upgraded.GetResourceGroups()).Name);
        Assert.Single(space.Store().GetResourceGroups());
    }

    [Fact]
    public void GroupValidationRejectsDuplicatesCyclesAndDefaultMutation()
    {
        using var space = new Space(); var store = space.Store();
        var a = store.SaveResourceGroup("Project"); var b = store.SaveResourceGroup("Child", a.Id);
        Assert.Throws<ArgumentException>(() => store.SaveResourceGroup("project"));
        Assert.Throws<ArgumentException>(() => store.SaveResourceGroup(" "));
        Assert.Throws<ArgumentException>(() => store.SaveResourceGroup("bad/name"));
        Assert.Throws<ArgumentException>(() => store.SaveResourceGroup("x", "missing"));
        Assert.Throws<ArgumentException>(() => store.SaveResourceGroup("Project", b.Id, a.Id));
        Assert.Throws<ArgumentException>(() => store.SaveResourceGroup("Project", a.Id, a.Id));
        Assert.Throws<InvalidOperationException>(() => store.SaveResourceGroup("changed", null, "default"));
        Assert.Throws<InvalidOperationException>(() => store.DeleteResourceGroup("default", true));
        var renamed = store.SaveResourceGroup("Renamed", null, b.Id);
        Assert.Equal(b.Id, renamed.Id); Assert.Equal(b.CreatedUtc, renamed.CreatedUtc); Assert.Null(renamed.ParentId);
    }

    [Fact]
    public void MovingResourcePreservesFileIdNotesAndFavorites()
    {
        using var space = new Space(); var store = space.Store();
        var file = space.File("source.txt"); var resource = store.AddResource(file, PublishMode.Reference);
        store.SetResourceNote(resource.Id, "note"); store.SaveFavorite(new("self", resource.Id, resource.Name, resource.Kind));
        var group = store.SaveResourceGroup("资料"); store.MoveResourceToGroup(resource.Id, group.Id);
        var moved = store.GetResource(resource.Id)!;
        Assert.Equal(file, moved.SourcePath); Assert.Equal("note", moved.Note); Assert.Equal(group.Id, moved.GroupId);
        Assert.True(System.IO.File.Exists(file)); Assert.Equal(resource.Id, Assert.Single(store.GetFavorites()).ResourceId);
        Assert.Equal(group.Id, Assert.Single(new ResourceCatalog(store).List()).GroupId);
        Assert.Throws<ArgumentException>(() => store.MoveResourceToGroup(resource.Id, "missing"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingSubtreeMovesOrRevokesOnlyItsPublicResources(bool revoke)
    {
        using var space = new Space(); var store = space.Store();
        var a = store.SaveResourceGroup("A"); var b = store.SaveResourceGroup("B", a.Id);
        var source = space.File("source.txt");
        var reference = store.AddResource(source, PublishMode.Reference, groupId: a.Id);
        var copy = store.AddResource(source, PublishMode.Copy, groupId: b.Id);
        var untouched = store.AddResource(source, PublishMode.Reference);
        store.UpsertPeer(new("peer", "127.0.0.1", 1, "Peer", null, null));
        var privateCopy = store.AddResource(source, PublishMode.Copy, "peer");
        store.DeleteResourceGroup(a.Id, revoke);
        Assert.Single(store.GetResourceGroups()); Assert.True(System.IO.File.Exists(source));
        Assert.NotNull(store.GetResource(untouched.Id)); Assert.True(System.IO.File.Exists(privateCopy.SourcePath));
        if (revoke) { Assert.Null(store.GetResource(reference.Id)); Assert.Null(store.GetResource(copy.Id)); Assert.False(System.IO.File.Exists(copy.SourcePath)); }
        else { Assert.Equal("default", store.GetResource(reference.Id)!.GroupId); Assert.Equal("default", store.GetResource(copy.Id)!.GroupId); Assert.True(System.IO.File.Exists(copy.SourcePath)); }
    }

    [Fact]
    public async Task HttpCatalogProvidesGroupsWhileLegacyFlatEndpointRemainsCompatible()
    {
        using var space = new Space(); var publisher = space.Store(); var receiver = new NodeStore(Path.Combine(space.Root, "receiver"));
        var group = publisher.SaveResourceGroup("共享资料"); var nested = publisher.SaveResourceGroup("子组", group.Id);
        var resource = publisher.AddResource(space.File("share.txt"), PublishMode.Reference, groupId: nested.Id);
        publisher.UpsertPeer(new("private-peer", "127.0.0.1", 1, "Private", null, null));
        var hidden = publisher.AddResource(space.File("private.txt"), PublishMode.Copy, "private-peer");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        publisher.SaveSettings("Publisher", null, port, true);
        await using var node = new PeerNode(publisher); await node.StartAsync(port, "127.0.0.1");
        using var client = new PeerClient(receiver); var peer = await client.ConnectAsync("127.0.0.1", port);
        var catalog = await client.GetCatalogAsync(peer);
        Assert.Contains(catalog.Groups, g => g.Id == nested.Id && g.ParentId == group.Id);
        Assert.Equal(nested.Id, Assert.Single(catalog.Resources).GroupId);
        Assert.DoesNotContain(catalog.Resources, r => r.Id == hidden.Id);
        using var http = new HttpClient();
        var legacy = await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{port}/api/v1/resources");
        Assert.Equal(JsonValueKind.Array, legacy.ValueKind); Assert.Equal(resource.Id, legacy[0].GetProperty("id").GetString());
        var files = new ResourceCatalog(publisher).ResolveFile(resource.Id, null);
        Assert.Equal("content", System.IO.File.ReadAllText(files.FullName));
    }

    private sealed class Space : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerGroupTests", Guid.NewGuid().ToString("N"));
        public NodeStore Store() => new(Path.Combine(Root, "data"));
        public string File(string name) { Directory.CreateDirectory(Root); var path = Path.Combine(Root, name); System.IO.File.WriteAllText(path, "content"); return path; }
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
