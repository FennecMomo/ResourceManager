using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.Core;
using ResourceManager.Server;

namespace ResourceManager.Tests;

public sealed class WorkspaceResourceTests
{
    [Fact]
    public async Task RelayDownloadsFoldersChecksHashesAndResumesWithoutLocalPeers()
    {
        await using var f = await Fixture.Start();
        var folder = Path.Combine(f.Root, "folder"); Directory.CreateDirectory(Path.Combine(folder, "nested")); Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var bytes = RandomNumberGenerator.GetBytes(700000); await File.WriteAllBytesAsync(Path.Combine(folder, "nested", "file.bin"), bytes);
        var resource = f.Owner.AddResource(folder, PublishMode.Reference); f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, true); await f.Publish();
        using var transport = f.Transport(); var peer = f.Peer;
        var manager = new DownloadManager(f.Reader, transport, _ => peer); var remote = (await transport.GetResourceAsync(peer, resource.Id))!;
        var job = manager.CreateJob(peer, remote, Path.Combine(f.Root, "downloads")); f.Reader.SaveServerDownload(job.Id, f.BindingB.Id, "Owner");
        var metadata = (await transport.GetFileMetadataAsync(peer, resource.Id, "nested/file.bin", default))!;
        Directory.CreateDirectory(Path.Combine(job.TargetPath, "nested"));
        var target = Path.Combine(job.TargetPath, "nested", "file.bin");
        await File.WriteAllBytesAsync(target + ".rm-part", bytes[..200000]); await File.WriteAllTextAsync(target + ".rm-etag", metadata.Etag);
        var completed = await manager.RunAsync(job.Id);
        Assert.Equal("已完成", completed.Status); Assert.Equal(bytes, await File.ReadAllBytesAsync(target)); Assert.True(Directory.Exists(Path.Combine(job.TargetPath, "empty")));
        Assert.Equal("服务器中继", transport.RouteName); Assert.Empty(f.Reader.GetPeers()); Assert.Equal(f.BindingB.Id, f.Reader.GetServerDownload(job.Id)!.Value.Server);
        using var resumed = await transport.OpenFileAsync(peer, resource.Id, "nested/file.bin", 123456, metadata.Etag);
        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode); Assert.Equal(bytes[123456..], await resumed.Content.ReadAsByteArrayAsync());
        using var reset = await transport.OpenFileAsync(peer, resource.Id, "nested/file.bin", 123456, "\"old\"");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [Fact]
    public async Task PublicationSelectionTracksGroupChangesAndResourceExclusions()
    {
        await using var f = await Fixture.Start(); var group = f.Owner.SaveResourceGroup("Shared"); var child = f.Owner.SaveResourceGroup("Child", group.Id);
        f.Owner.SetServerPublication(f.BindingA.Id, "group", group.Id, true);
        var resource = f.AddFile("file.txt", "contents", child.Id); Assert.True(f.Owner.IsServerPublished(f.BindingA.Id, "resource", resource.Id));
        await f.Publish();
        var published = (await f.ClientB.CatalogsAsync(f.BindingB, f.SessionB, default)).Single(c => c.Owner == f.Peer.DeviceId).Catalog;
        Assert.Single(published.Resources); Assert.Contains(published.Groups, g => g.Id == group.Id); Assert.Contains(published.Groups, g => g.Id == child.Id && g.ParentId == group.Id);
        f.Owner.SetGroupPermission(group.Id, GroupAccess.Private, []); f.Owner.SetGroupPermission(child.Id, GroupAccess.Public, []); await f.Publish();
        var hiddenParent = (await f.ClientB.CatalogsAsync(f.BindingB, f.SessionB, default)).Single(c => c.Owner == f.Peer.DeviceId).Catalog;
        Assert.DoesNotContain(hiddenParent.Groups, g => g.Id == group.Id); Assert.Null(Assert.Single(hiddenParent.Groups).ParentId);
        f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, false); await f.Publish();
        Assert.Empty((await f.ClientB.CatalogsAsync(f.BindingB, f.SessionB, default)).Single(c => c.Owner == f.Peer.DeviceId).Catalog.Resources);
        f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, true); f.Owner.MoveResourceToGroup(resource.Id, NodeStore.DefaultResourceGroupId); await f.Publish();
        Assert.Single((await f.ClientB.CatalogsAsync(f.BindingB, f.SessionB, default)).Single(c => c.Owner == f.Peer.DeviceId).Catalog.Resources);
        f.Owner.RemoveResource(resource.Id); await f.Publish();
        Assert.Empty((await f.ClientB.CatalogsAsync(f.BindingB, f.SessionB, default)).Single(c => c.Owner == f.Peer.DeviceId).Catalog.Resources);
    }

    [Fact]
    public async Task ServerAndPublisherBothEnforcePermissionsAndRevocation()
    {
        await using var f = await Fixture.Start(); var group = f.Owner.SaveResourceGroup("Private name"); var resource = f.AddFile("secret.txt", "secret", group.Id);
        f.Owner.SetServerPublication(f.BindingA.Id, "group", group.Id, true); f.Owner.SetGroupPermission(group.Id, GroupAccess.Private, []); await f.Publish();
        var visible = (await f.ClientB.CatalogsAsync(f.BindingB, f.SessionB, default)).Single(c => c.Owner == f.Peer.DeviceId);
        Assert.Empty(visible.Catalog.Groups); Assert.Empty(visible.Catalog.Resources); Assert.Empty(visible.Updates);
        var request = f.ClientB.SignResourceRequest(f.BindingB, f.Peer.DeviceId, resource.Id, "describe");
        var denied = await Assert.ThrowsAsync<WorkspaceException>(() => f.ClientB.RelayAsync(f.BindingB, f.SessionB, request, default)); Assert.Equal(HttpStatusCode.Forbidden, denied.Code);
        f.Owner.SetGroupPermission(group.Id, GroupAccess.AllowList, [f.Reader.GetSettings().Profile.DeviceId]); await f.Publish();
        using var transport = f.Transport(); Assert.NotNull(await transport.GetResourceAsync(f.Peer, resource.Id));
        // Before the next catalog sync, publisher still denies the stale server's allowance.
        f.Owner.SetGroupPermission(group.Id, GroupAccess.Private, []);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetFilesAsync(f.Peer, resource.Id));
        f.Owner.SetGroupPermission(group.Id, GroupAccess.Public, []); await f.Publish();
        f.Owner.SetServerPublication(f.BindingA.Id, "group", group.Id, false);
        Assert.Null(await transport.GetResourceAsync(f.Peer, resource.Id));
    }

    [Fact]
    public async Task SignedRequestsCannotChangeIdentityOrEscapeFolder()
    {
        await using var f = await Fixture.Start(); var folder = Path.Combine(f.Root, "shared"); Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder, "safe.txt"), "safe");
        var resource = f.Owner.AddResource(folder, PublishMode.Reference); f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, true); await f.Publish();
        var signed = f.ClientB.SignResourceRequest(f.BindingB, f.Peer.DeviceId, resource.Id, "meta", "../outside");
        var response = await f.ClientB.RelayAsync(f.BindingB, f.SessionB, signed, default); Assert.Equal(400, response.Status);
        var forged = signed with { Requester = f.Peer.DeviceId };
        await Assert.ThrowsAsync<WorkspaceException>(() => f.ClientB.RelayAsync(f.BindingB, f.SessionB, forged, default));
        var replay = await Assert.ThrowsAsync<WorkspaceException>(() => f.ClientB.RelayAsync(f.BindingB, f.SessionB, signed, default)); Assert.Equal(HttpStatusCode.Conflict, replay.Code);
        var wrongServer = signed with { ServerId = Guid.NewGuid().ToString("N") };
        Assert.Equal(403, (await new WorkspacePublisher(f.Owner).HandleAsync(wrongServer, WorkspaceProtocol.PublicKey(f.Reader.GetDeviceSigningKey()), default)).Status);
    }

    [Fact]
    public async Task ConfiguredDirectRouteUsesSignedPeerAndNeverFallsBackOnFailure()
    {
        await using var f = await Fixture.Start(); var resource = f.AddFile("direct.txt", "direct contents"); f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, true); await f.Publish();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var node = new PeerNode(f.Owner); await node.StartAsync(port, "127.0.0.1");
        var peer = f.Peer with { Ip = "127.0.0.1", Port = node.Port }; f.Reader.UpsertPeer(peer, source: "Manual");
        using (var transport = f.Transport())
        {
            using var response = await transport.OpenFileAsync(peer, resource.Id, "", 0, null);
            Assert.Equal("direct contents", await response.Content.ReadAsStringAsync()); Assert.Equal("直连", transport.RouteName);
        }
        await node.DisposeAsync();
        using var unavailable = f.Transport();
        var error = await Assert.ThrowsAsync<IOException>(() => unavailable.SelectRouteAsync(default));
        Assert.Contains("未切换服务器中继", error.Message);
    }

    [Fact]
    public async Task CancelledRelayCannotBeAnsweredByAnotherDeviceAndOfflineOwnerIsUnavailable()
    {
        await using var f = await Fixture.Start(); var resource = f.AddFile("pending.txt", "pending"); f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, true); await f.Publish();
        await f.StopRelay();
        using var cancellation = new CancellationTokenSource();
        var request = f.ClientB.SignResourceRequest(f.BindingB, f.Peer.DeviceId, resource.Id, "describe");
        var pending = f.ClientB.RelayAsync(f.BindingB, f.SessionB, request, cancellation.Token);
        var ticket = Assert.Single(await f.ClientA.PollRelayAsync(f.BindingA, f.SessionA, default));
        var wrongOwner = await Assert.ThrowsAsync<WorkspaceException>(() => f.ClientB.ReplyRelayAsync(f.BindingB, f.SessionB, ticket.Id, new(200, []), default));
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.Code);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.Delay(100);
        Assert.False(await f.ClientA.ReplyRelayAsync(f.BindingA, f.SessionA, ticket.Id, new(200, []), default));
        await f.ClientA.LeaveAsync(f.BindingA, f.SessionA, default);
        var offline = await Assert.ThrowsAsync<WorkspaceException>(() => f.ClientB.RelayAsync(f.BindingB, f.SessionB, f.ClientB.SignResourceRequest(f.BindingB, f.Peer.DeviceId, resource.Id, "describe"), default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.Code);
        Assert.Contains("发布者离线", offline.Message);
    }

    [Fact]
    public async Task DamagedPartialFileFailsHashCheckAndCanBeRetried()
    {
        await using var f = await Fixture.Start(); var resource = f.AddFile("hash.txt", new string('A', 400000)); f.Owner.SetServerPublication(f.BindingA.Id, "resource", resource.Id, true); await f.Publish();
        using var transport = f.Transport(); var manager = new DownloadManager(f.Reader, transport, _ => f.Peer);
        var job = manager.CreateJob(f.Peer, (await transport.GetResourceAsync(f.Peer, resource.Id))!, Path.Combine(f.Root, "download"));
        var meta = (await transport.GetFileMetadataAsync(f.Peer, resource.Id, "", default))!;
        await File.WriteAllTextAsync(job.TargetPath + ".rm-part", new string('B', 10000)); await File.WriteAllTextAsync(job.TargetPath + ".rm-etag", meta.Etag);
        await Assert.ThrowsAsync<IOException>(() => manager.RunAsync(job.Id)); Assert.False(File.Exists(job.TargetPath)); Assert.False(File.Exists(job.TargetPath + ".rm-part"));
        Assert.Equal("已完成", (await manager.RunAsync(job.Id)).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "RM-resource-tests-" + Guid.NewGuid().ToString("N"));
        public NodeStore Owner = null!, Reader = null!; public WorkspaceClient ClientA = null!, ClientB = null!;
        public ServerBinding BindingA = null!, BindingB = null!; public WorkspaceSession SessionA = null!, SessionB = null!;
        private WebApplication app = null!; private CancellationTokenSource cancellation = new(); private Task relay = Task.CompletedTask;
        public PeerInfo Peer => new(Owner.GetSettings().Profile.DeviceId, "", 0, "Owner", null, null);
        public static async Task<Fixture> Start()
        {
            var f = new Fixture(); f.Owner = new(Path.Combine(f.Root, "a")); f.Reader = new(Path.Combine(f.Root, "b"));
            f.ClientA = new(f.Owner, "0.6.2"); f.ClientB = new(f.Reader, "0.6.2");
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var data = Path.Combine(f.Root, "server"); builder.Services.AddSingleton(new ServerStore(data, "Resources")); builder.Services.AddSingleton(new ServerRuntimeOptions(data, "Resources", 1, 2, 3, "0.2.0"));
            builder.Services.AddSingleton<WorkspaceHub>(); builder.Services.AddHostedService(s => s.GetRequiredService<WorkspaceHub>());
            f.app = builder.Build(); f.app.MapWorkspace(); await f.app.StartAsync(); var address = f.app.Urls.Single(); var info = await f.ClientA.InspectAsync(address);
            f.BindingA = new(Guid.NewGuid().ToString("N"), "Server", address, info.ServerId, info.PublicKey, "在线", null, null); f.BindingB = f.BindingA with { Id = Guid.NewGuid().ToString("N") };
            f.SessionA = await f.ClientA.JoinAsync(f.BindingA); f.SessionB = await f.ClientB.JoinAsync(f.BindingB);
            var snapshot = await f.ClientA.WatchAsync(f.BindingA, f.SessionA, "", default);
            f.BindingA = f.BindingA with { Cached = snapshot }; f.BindingB = f.BindingB with { Cached = snapshot };
            f.Owner.SaveServerBinding(f.BindingA); f.Reader.SaveServerBinding(f.BindingB);
            // No LAN broadcasts and no user data; reverse poll loop handles only this temporary server.
            f.relay = f.RunRelay();
            await Task.Delay(100); return f;
        }
        private async Task RunRelay()
        {
            var publisher = new WorkspacePublisher(Owner);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var tickets = await ClientA.PollRelayAsync(BindingA, SessionA, cancellation.Token);
                    foreach (var ticket in tickets) await ClientA.ReplyRelayAsync(BindingA, SessionA, ticket.Id, await publisher.HandleAsync(ticket.Request, ticket.PublicKey, cancellation.Token), cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        public WorkspaceResourceClient Transport() => new(Reader, ClientB, BindingB, () => SessionB, Peer.DeviceId, _ => Task.FromResult<IReadOnlyList<DiscoveredPeer>>([]));
        public LocalResource AddFile(string name, string content, string group = NodeStore.DefaultResourceGroupId) { var path = Path.Combine(Root, name); File.WriteAllText(path, content); return Owner.AddResource(path, PublishMode.Reference, groupId: group); }
        public async Task Publish() => await ClientA.PublishAsync(BindingA, SessionA, await new WorkspacePublisher(Owner).BuildAsync(BindingA, default), default);
        public async Task StopRelay() { cancellation.Cancel(); await relay; }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel(); await relay; await app.StopAsync(); await app.DisposeAsync(); ClientA.Dispose(); ClientB.Dispose(); cancellation.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true);
        }
    }
}
