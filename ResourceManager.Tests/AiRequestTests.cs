using System.Net;
using System.Net.Sockets;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class AiRequestTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerAiTests", Guid.NewGuid().ToString("N"));
        public string Folder => Path.Combine(Root, "approved");
        public NodeStore Store { get; }
        public PeerClient Client { get; }
        public ChatService Chat { get; }
        public Fixture()
        {
            Store = new NodeStore(Path.Combine(Root, "data"));
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "manual.txt"), "这是焊接操作说明");
            File.WriteAllText(Path.Combine(Root, "outside.txt"), "not authorized");
            Store.UpsertPeer(new PeerInfo("requester", "127.0.0.1", 37642, "A", null, null));
            Store.SavePeerCapabilities("requester", [NodeDefaults.PrivateResourceCapability, NodeDefaults.ChatCapability]);
            Store.SetAiCodexExecutable(Environment.ProcessPath!);
            Client = new PeerClient(Store, supportsChat: true);
            Chat = new ChatService(Store, Client);
        }
        public void Dispose() { Client.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class FakeFinder : IAiFileFinder
    {
        public int Calls;
        public bool Wait;
        public string[] Paths = ["manual.txt"];
        public async Task<AiFinderResult> FindAsync(string executable, string directory, string question, string? previousAnswer, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            return new("找到操作说明", Paths);
        }
    }
    private static async Task<AiSearchView> WaitFor(AiRequestService service, string device, string id, string state)
    {
        for (var i = 0; i < 100; i++)
        {
            var view = service.Get(device, id);
            if (view.State == state) { await Task.Delay(20); return view; }
            await Task.Delay(30);
        }
        throw new TimeoutException(service.Get(device, id).State);
    }

    [Fact]
    public async Task ConsentFollowupTransferAndCancellationKeepIdentityAndScope()
    {
        using var fixture = new Fixture(); var finder = new FakeFinder();
        await using var service = new AiRequestService(fixture.Store, fixture.Chat, finder);
        var id = Guid.NewGuid().ToString("N");
        var request = service.Receive("requester", new(id, "找焊接说明"));
        Assert.Equal("AwaitingApproval", request.State); Assert.Equal(0, finder.Calls);
        Assert.Throws<KeyNotFoundException>(() => service.Get("other", id));
        Assert.Throws<KeyNotFoundException>(() => service.Act("other", id, new("cancel")));
        Assert.Equal(id, service.Receive("requester", new(id, "重复请求")).Id);
        var scope = fixture.Store.SaveAiSearchScope("requester", fixture.Folder, false);
        service.Approve(id, scope.Id);
        var ready = await WaitFor(service, "requester", id, "Ready");
        Assert.Single(ready.Files);
        Assert.Throws<ArgumentException>(() => service.Act("requester", id, new("send_file", FileId: "../outside.txt")));
        service.Act("requester", id, new("continue", "还有哪些说明？"));
        ready = await WaitFor(service, "requester", id, "Ready"); Assert.Equal(2, ready.Turns);
        service.Act("requester", id, new("send_file", FileId: ready.Files[0].Id));
        var sent = await WaitFor(service, "requester", id, "Ready");
        Assert.NotNull(sent.Files[0].MessageId);
        var privateCopy = fixture.Store.GetPrivateResource(sent.Files[0].ResourceId!, "requester");
        Assert.NotNull(privateCopy); Assert.Equal(PublishMode.Copy, privateCopy.Mode);
        Assert.Equal("这是焊接操作说明", File.ReadAllText(privateCopy.SourcePath));
        Assert.Empty(fixture.Store.GetResources());
        service.Act("requester", id, new("send_file", FileId: sent.Files[0].Id));
        Assert.Single(fixture.Store.GetResources("requester"));
        service.Act("requester", id, new("cancel"));
        Assert.Equal("Canceled", service.Get("requester", id).State);
        Assert.Throws<InvalidOperationException>(() => service.Act("requester", id, new("continue", "再找")));
    }

    [Fact]
    public async Task AutoApprovalIsDeviceSpecificAndRevocationStopsRunningWork()
    {
        using var fixture = new Fixture(); var finder = new FakeFinder { Wait = true };
        await using var service = new AiRequestService(fixture.Store, fixture.Chat, finder);
        var scope = fixture.Store.SaveAiSearchScope("requester", fixture.Folder, true);
        fixture.Store.UpsertPeer(new PeerInfo("other", "127.0.0.1", 37643, "C", null, null));
        var other = service.Receive("other", new(Guid.NewGuid().ToString("N"), "查找", scope.Id));
        Assert.Equal("AwaitingApproval", other.State);
        Assert.Throws<ArgumentException>(() => service.Approve(other.Id, scope.Id));
        var own = service.Receive("requester", new(Guid.NewGuid().ToString("N"), "查找", scope.Id));
        Assert.Equal("Running", own.State);
        service.RemoveScope(scope.Id);
        await WaitFor(service, "requester", own.Id, "Canceled");
        Assert.Empty(fixture.Store.GetAiSearchScopes());
    }

    [Fact]
    public async Task RemovingDeviceCancelsWorkAndRemovesItsPersistentAutoApproval()
    {
        using var fixture = new Fixture(); var finder = new FakeFinder { Wait = true };
        await using var service = new AiRequestService(fixture.Store, fixture.Chat, finder);
        var scope = fixture.Store.SaveAiSearchScope("requester", fixture.Folder, true);
        var request = service.Receive("requester", new(Guid.NewGuid().ToString("N"), "查找", scope.Id));
        service.RevokeDevice("requester");
        fixture.Store.RemovePeer("requester");
        await WaitFor(service, "requester", request.Id, "Canceled");
        Assert.Empty(fixture.Store.GetAiSearchScopes());
        Assert.Throws<InvalidOperationException>(() => service.Act("requester", request.Id, new("continue", "继续")));
    }

    [Fact]
    public async Task EscapingModelPathFailsAndRestartDoesNotReplayJobs()
    {
        using var fixture = new Fixture(); var finder = new FakeFinder { Paths = ["../outside.txt"] };
        var scope = fixture.Store.SaveAiSearchScope("requester", fixture.Folder, true);
        await using (var service = new AiRequestService(fixture.Store, fixture.Chat, finder))
        {
            var request = service.Receive("requester", new(Guid.NewGuid().ToString("N"), "查找", scope.Id));
            await WaitFor(service, "requester", request.Id, "Failed");
            Assert.Empty(fixture.Store.GetResources("requester"));
        }
        var interrupted = new AiSearchSession(Guid.NewGuid().ToString("N"), "requester", "查找", "Running",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), scope.Id, fixture.Folder);
        fixture.Store.SaveAiSearchSession(interrupted);
        var calls = finder.Calls;
        await using var restored = new AiRequestService(fixture.Store, fixture.Chat, finder);
        Assert.Equal("Failed", restored.Get("requester", interrupted.Id).State); Assert.Equal(calls, finder.Calls);
    }

    [Fact]
    public void ScopedReadsRejectTraversalLinksCredentialsAndMissingFiles()
    {
        using var fixture = new Fixture(); var files = new ScopedAiFiles(fixture.Folder);
        Assert.Throws<UnauthorizedAccessException>(() => files.OpenRead("../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => files.OpenRead(".git/config"));
        Assert.Throws<UnauthorizedAccessException>(() => files.OpenRead(".env"));
        Assert.Throws<UnauthorizedAccessException>(() => files.OpenRead("manual.txt:secret"));
        Assert.Throws<FileNotFoundException>(() => files.OpenRead("missing.txt"));
        Assert.Contains("焊接", files.ReadExcerpt("manual.txt"));
        Assert.Single(files.List(default));
        var credentialRoot = Path.Combine(fixture.Root, ".ssh"); Directory.CreateDirectory(credentialRoot);
        Assert.Throws<UnauthorizedAccessException>(() => ScopedAiFiles.ValidateRoot(credentialRoot));
        Assert.Throws<ArgumentException>(() => ScopedAiFiles.ValidateRoot(Path.GetPathRoot(fixture.Root)!));
        Assert.Throws<ArgumentException>(() => ScopedAiFiles.ValidateRoot("relative"));
    }

    [Fact]
    public async Task ExpiryAndRateLimitPreventUnboundedRemoteWork()
    {
        using var fixture = new Fixture(); var finder = new FakeFinder();
        await using var service = new AiRequestService(fixture.Store, fixture.Chat, finder);
        var expired = new AiSearchSession(Guid.NewGuid().ToString("N"), "requester", "old", "Ready",
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-1));
        fixture.Store.SaveAiSearchSession(expired);
        Assert.Equal("Expired", service.Get("requester", expired.Id).State);
        Assert.Throws<InvalidOperationException>(() => service.Act("requester", expired.Id, new("continue", "查找")));
        for (var i = 0; i < 5; i++) service.Receive("requester", new(Guid.NewGuid().ToString("N"), "查找"));
        Assert.Throws<InvalidOperationException>(() => service.Receive("requester", new(Guid.NewGuid().ToString("N"), "查找")));
        Assert.Equal(0, finder.Calls);
    }

    [Fact]
    public void CliLaunchDisablesExternalToolsAndUsesArgumentsInsteadOfShellInterpolation()
    {
        using var fixture = new Fixture();
        var start = CodexFileFinder.CreateStartInfo(Environment.ProcessPath!, fixture.Root, Path.Combine(fixture.Root, "schema.json"));
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.Contains("--ignore-user-config", start.ArgumentList);
        Assert.Contains("--ignore-rules", start.ArgumentList);
        Assert.Contains("features.shell_tool=false", start.ArgumentList);
        Assert.Contains("features.plugins=false", start.ArgumentList);
        Assert.Contains("features.view_image=false", start.ArgumentList);
        Assert.Contains("approval_policy=\"never\"", start.ArgumentList);
        Assert.Contains("permissions.rm-search.network.enabled=false", start.ArgumentList);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", start.ArgumentList);
    }

    [Fact]
    public async Task SignedHttpRequestsAreIsolatedBetweenThreeDevices()
    {
        using var fixture = new Fixture();
        await using var service = new AiRequestService(fixture.Store, fixture.Chat, new FakeFinder());
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        fixture.Store.SaveSettings("B", null, port, true);
        await using var discovery = new LanDiscoveryService(fixture.Store);
        using var gateway = new UpnpGatewayClient();
        var mapping = new UpnpPortMappingManager(fixture.Store, gateway);
        await using var node = new PeerNode(fixture.Store, discovery, mapping, chat: fixture.Chat, aiRequests: service);
        await node.StartAsync(port, "127.0.0.1");
        var aStore = new NodeStore(Path.Combine(fixture.Root, "a"));
        listener.Start(); var portA = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        aStore.SaveSettings("A", null, portA, true);
        using var a = new PeerClient(aStore, supportsChat: true);
        var aChat = new ChatService(aStore, a);
        await using var aDiscovery = new LanDiscoveryService(aStore);
        using var aGateway = new UpnpGatewayClient();
        await using var aNode = new PeerNode(aStore, aDiscovery, new UpnpPortMappingManager(aStore, aGateway), chat: aChat);
        await aNode.StartAsync(portA, "127.0.0.1");
        using var c = new PeerClient(new NodeStore(Path.Combine(fixture.Root, "c")), supportsChat: true);
        var bForA = await a.ConnectAsync("127.0.0.1", port);
        var bForC = await c.ConnectAsync("127.0.0.1", port);
        var request = await a.CreateAiRequestAsync(bForA, new(Guid.NewGuid().ToString("N"), "找说明"));
        Assert.Equal("AwaitingApproval", request.State);
        Assert.Equal(request.Id, (await a.GetAiRequestAsync(bForA, request.Id)).Id);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetAiRequestAsync(bForC, request.Id));
        await Assert.ThrowsAsync<HttpRequestException>(() => c.ActAiRequestAsync(bForC, request.Id, new("cancel")));
        using var unsigned = new HttpClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await unsigned.GetAsync($"http://127.0.0.1:{port}/api/v1/ai/requests/{request.Id}")).StatusCode);
        var aId = aStore.GetSettings().Profile.DeviceId;
        var scope = fixture.Store.SaveAiSearchScope(aId, fixture.Folder, false);
        service.Approve(request.Id, scope.Id);
        var ready = await WaitFor(service, aId, request.Id, "Ready");
        Assert.Equal("Ready", (await a.GetAiRequestAsync(bForA, request.Id)).State);
        await a.ActAiRequestAsync(bForA, request.Id, new("send_file", FileId: ready.Files[0].Id));
        var sent = await WaitFor(service, aId, request.Id, "Ready");
        await fixture.Chat.PumpAsync();
        var received = Assert.Single(aStore.GetChatMessages(bForA.DeviceId));
        Assert.Equal(sent.Files[0].ResourceId, received.ResourceId);
        Assert.Equal("PrivateResource", received.Kind);
        var resource = await a.GetResourceAsync(bForA, received.ResourceId!);
        Assert.NotNull(resource);
        using var download = await a.OpenFileAsync(bForA, resource.Id, "", 0, null);
        download.EnsureSuccessStatusCode();
        Assert.Equal("这是焊接操作说明", await download.Content.ReadAsStringAsync());
        Assert.Null(await c.GetResourceAsync(bForC, resource.Id));
        Assert.Equal("Canceled", (await a.ActAiRequestAsync(bForA, request.Id, new("cancel"))).State);
    }
}
