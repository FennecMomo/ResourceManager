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

public sealed class ResourceAccessTests
{
    [Fact]
    public void InheritanceOverridesMovesAndRemovalApplyWithoutChangingIds()
    {
        using var s = new Space(); var store = s.Store("publisher");
        store.UpsertPeer(new("a", "127.0.0.1", 1, "A", null, null));
        var parent = store.SaveResourceGroup("Parent"); var child = store.SaveResourceGroup("Child", parent.Id);
        var resource = store.AddResource(s.File("data.txt"), PublishMode.Reference, groupId: child.Id);
        store.SetGroupPermission(parent.Id, GroupAccess.AllowList, ["a"]);
        Assert.Equal(parent.Id, store.GetEffectiveGroupPermission(child.Id).SourceGroupId);
        Assert.True(store.CanAccessGroup(child.Id, "a")); Assert.False(store.CanAccessGroup(child.Id, "b")); Assert.False(store.CanAccessGroup(child.Id, null));
        store.SetGroupPermission(child.Id, GroupAccess.Private, []); Assert.False(store.CanAccessGroup(child.Id, "a"));
        store.SetGroupPermission(child.Id, GroupAccess.Public, []); Assert.True(store.CanAccessGroup(child.Id, null));
        store.SetGroupPermission(child.Id, GroupAccess.Inherit, []);
        store.SaveResourceGroup(child.Name, null, child.Id); Assert.True(store.CanAccessGroup(child.Id, null));
        store.MoveResourceToGroup(resource.Id, parent.Id); Assert.Equal(resource.Id, store.GetResource(resource.Id)!.Id);
        Assert.Equal(1, store.CountAllowedGroups("a")); store.RemovePeer("a");
        Assert.Empty(store.GetGroupPermission(parent.Id).DeviceIds);
        store.UpsertPeer(new("a", "127.0.0.2", 2, "Renamed", null, null)); Assert.False(store.CanAccessGroup(parent.Id, "a"));
        store.DeleteResourceGroup(parent.Id, false); Assert.Equal("default", store.GetResource(resource.Id)!.GroupId);
        Assert.Equal(GroupAccess.Inherit, store.GetGroupPermission(parent.Id).Access);
    }

    [Fact]
    public async Task SameIpPeersHaveDistinctAccessAndLegacyOnlySeesPublic()
    {
        using var s = new Space(); var publisher = s.Store("publisher"); var a = s.Store("a"); var b = s.Store("b");
        await using var node = await Start(publisher);
        using var clientA = new PeerClient(a); using var clientB = new PeerClient(b);
        var peerA = await clientA.ConnectAsync("127.0.0.1", node.Port); var peerB = await clientB.ConnectAsync("127.0.0.1", node.Port);
        var allow = publisher.SaveResourceGroup("Allowed"); var hidden = publisher.SaveResourceGroup("Secret parent"); var visible = publisher.SaveResourceGroup("Public child", hidden.Id);
        publisher.SetGroupPermission(allow.Id, GroupAccess.AllowList, [a.GetSettings().Profile.DeviceId]);
        publisher.SetGroupPermission(hidden.Id, GroupAccess.Private, []); publisher.SetGroupPermission(visible.Id, GroupAccess.Public, []);
        var restricted = publisher.AddResource(s.File("restricted.txt"), PublishMode.Reference, groupId: allow.Id);
        var secret = publisher.AddResource(s.File("secret.txt"), PublishMode.Reference, groupId: hidden.Id);
        var open = publisher.AddResource(s.File("open.txt"), PublishMode.Reference, groupId: visible.Id);
        var ca = await clientA.GetCatalogAsync(peerA); var cb = await clientB.GetCatalogAsync(peerB);
        Assert.Contains(ca.Resources, r => r.Id == restricted.Id); Assert.DoesNotContain(cb.Resources, r => r.Id == restricted.Id);
        Assert.DoesNotContain(ca.Groups, g => g.Id == hidden.Id); Assert.Null(ca.Groups.Single(g => g.Id == visible.Id).ParentId);
        Assert.DoesNotContain(ca.Resources, r => r.Id == secret.Id); Assert.Equal(open.Id, Assert.Single(cb.Resources).Id);
        Assert.NotEmpty(await clientA.GetFilesAsync(peerA, restricted.Id));
        using (var response = await clientA.OpenFileAsync(peerA, restricted.Id, "", 2, null)) Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => clientB.GetResourceAsync(peerB, restricted.Id));
        using (var response = await clientB.OpenFileAsync(peerB, restricted.Id, "", 2, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var legacy = new HttpClient();
        var flat = await legacy.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{node.Port}/api/v1/resources");
        Assert.Equal(open.Id, flat[0].GetProperty("id").GetString()); Assert.Equal(1, flat.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"http://127.0.0.1:{node.Port}/api/v1/resources/{restricted.Id}/content")).StatusCode);
        publisher.SetGroupPermission(allow.Id, GroupAccess.Private, []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => clientA.GetResourceAsync(peerA, restricted.Id));
        using (var response = await clientA.OpenFileAsync(peerA, restricted.Id, "", 2, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        publisher.RemoveResource(restricted.Id); Assert.Null(await clientA.GetResourceAsync(peerA, restricted.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => clientB.GetResourceAsync(peerB, restricted.Id));
    }

    [Fact]
    public async Task SignedRequestsRejectReplayChangedRangeExpiredProofAndImpersonation()
    {
        using var s = new Space(); var publisher = s.Store("publisher"); var receiver = s.Store("receiver");
        await using var node = await Start(publisher); using var client = new PeerClient(receiver);
        var peer = await client.ConnectAsync("127.0.0.1", node.Port);
        var resource = publisher.AddResource(s.File("sample.txt"), PublishMode.Reference);
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{node.Port}/api/v1/resources/{resource.Id}/content";
        using var signed = new HttpRequestMessage(HttpMethod.Get, url); PeerProof.SignRequest(receiver, signed, peer.DeviceId, []);
        using var replay = Copy(signed);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(signed)).StatusCode);
        // Persisted replay protection remains effective after the publisher restarts.
        await node.StopAsync(); await node.StartAsync(peer.Port, "127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(replay)).StatusCode);
        using var range = new HttpRequestMessage(HttpMethod.Get, url); PeerProof.SignRequest(receiver, range, peer.DeviceId, []);
        range.Headers.Range = new(2, null); Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(range)).StatusCode);
        using var expired = new HttpRequestMessage(HttpMethod.Get, url); PeerProof.SignRequest(receiver, expired, peer.DeviceId, []);
        expired.Headers.Remove("X-RM-Time"); expired.Headers.Add("X-RM-Time", "1");
        expired.Headers.Remove("X-RM-Signature");
        expired.Headers.Add("X-RM-Signature", PeerProof.Sign(receiver, PeerProof.RequestPayload(receiver.GetSettings().Profile.DeviceId,
            peer.DeviceId, "GET", expired.RequestUri!.PathAndQuery + "\n\n", "1", expired.Headers.GetValues("X-RM-Nonce").Single(), [])));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(expired)).StatusCode);
        var impostor = s.Store("impostor");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(impostor.DataDirectory, "resources.db")}"))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE settings SET value=$id WHERE key='device_id'"; cmd.Parameters.AddWithValue("$id", receiver.GetSettings().Profile.DeviceId); cmd.ExecuteNonQuery(); }
        using var fake = new PeerClient(impostor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.ConnectAsync("127.0.0.1", node.Port));
        // A claimed ID without a valid signature never grants authenticated identity.
        using var identityOnly = new HttpRequestMessage(HttpMethod.Get, url); identityOnly.Headers.Add("X-RM-Device", receiver.GetSettings().Profile.DeviceId);
        var group = publisher.SaveResourceGroup("Protected"); publisher.MoveResourceToGroup(resource.Id, group.Id);
        publisher.SetGroupPermission(group.Id, GroupAccess.AllowList, [receiver.GetSettings().Profile.DeviceId]);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(identityOnly)).StatusCode);
    }

    [Fact]
    public async Task TrustedPublisherCannotReplaceKeyOrDowngradeToLegacy()
    {
        using var s = new Space(); var publisher = s.Store("publisher"); var receiver = s.Store("receiver");
        await using var node = await Start(publisher);
        using (var client = new PeerClient(receiver)) await client.ConnectAsync("127.0.0.1", node.Port);
        var id = publisher.GetSettings().Profile.DeviceId;
        var replacement = s.Store("replacement");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(replacement.DataDirectory, "resources.db")}"))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE settings SET value=$id WHERE key='device_id'"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
        await using var fake = await Start(replacement); using var verifier = new PeerClient(receiver);
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.ConnectAsync("127.0.0.1", fake.Port));
        Assert.Contains("密钥", changed.Message);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var legacy = builder.Build(); legacy.MapGet("/api/v1/health", () => Results.Json(new PeerHello(id, "Legacy claim", 1, null, [])));
        await legacy.StartAsync();
        var downgrade = await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.ConnectAsync("127.0.0.1", new Uri(legacy.Urls.Single()).Port));
        Assert.Contains("降级", downgrade.Message);
    }

    [Fact]
    public async Task PublicKeyAndWhitelistSurviveNicknameAddressChangeAndRestart()
    {
        using var s = new Space(); var publisher = s.Store("publisher"); var receiver = s.Store("receiver");
        await using var node = await Start(publisher); using var first = new PeerClient(receiver);
        var peer = await first.ConnectAsync("127.0.0.1", node.Port); var id = receiver.GetSettings().Profile.DeviceId;
        var group = publisher.SaveResourceGroup("Team"); publisher.SetGroupPermission(group.Id, GroupAccess.AllowList, [id]);
        publisher.AddResource(s.File("team.txt"), PublishMode.Reference, groupId: group.Id);
        var key = publisher.GetTrustedDeviceKey(id); receiver.SaveSettings("New name", null, 12345, true);
        using var second = new PeerClient(new NodeStore(receiver.DataDirectory));
        await second.ConnectAsync("127.0.0.1", node.Port);
        Assert.Equal(key, publisher.GetTrustedDeviceKey(id)); Assert.Equal("New name", publisher.GetPeer(id)!.Nickname);
        publisher.UpsertPeer(publisher.GetPeer(id)! with { Ip = "127.0.0.2" });
        Assert.Single((await second.GetCatalogAsync(peer)).Resources);
        publisher.RemovePeer(id); Assert.Equal(key, publisher.GetTrustedDeviceKey(id));
        Assert.Empty(publisher.GetGroupPermission(group.Id).DeviceIds);
    }

    private static HttpRequestMessage Copy(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }
    private static async Task<PeerNode> Start(NodeStore store)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        store.SaveSettings("Publisher", null, port, true); var node = new PeerNode(store); await node.StartAsync(port, "127.0.0.1"); return node;
    }
    private sealed class Space : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ResourceAccessTests", Guid.NewGuid().ToString("N"));
        public NodeStore Store(string name) => new(Path.Combine(root, name));
        public string File(string name) { Directory.CreateDirectory(root); var path = Path.Combine(root, name); System.IO.File.WriteAllText(path, "test file contents"); return path; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
