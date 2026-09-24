using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class ChatTests
{
    [Fact]
    public async Task OnlineSend_IsDurableAndDuplicateAckDoesNotInsertAgain()
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
        var sent = chatA.QueueText(peerB.DeviceId, "你好");

        await chatA.PumpAsync();

        Assert.Equal("Delivered", a.GetChatMessage(peerB.DeviceId, sent.MessageId, true)!.State);
        var peerAId = a.GetSettings().Profile.DeviceId;
        Assert.Equal("你好", Assert.Single(b.GetChatMessages(peerAId)).Text);
        Assert.Equal(1, Assert.Single(b.GetChatConversations()).Unread);
        var request = new ChatMessageRequest(NodeDefaults.ChatCapability, sent.MessageId, peerAId,
            b.GetSettings().Profile.DeviceId, sent.SentUtc, "Text", "你好");
        var duplicate = await clientA.SendChatAsync(peerB, request);
        Assert.True(duplicate.Accepted);
        Assert.True(duplicate.Duplicate);
        Assert.Single(b.GetChatMessages(peerAId));
        b.MarkChatRead(peerAId);
        Assert.Equal(0, Assert.Single(b.GetChatConversations()).Unread);
    }

    [Fact]
    public async Task OfflineQueueSurvivesRestart_AndExpiresAfterSevenDaysButManualRetryUsesSameId()
    {
        using var room = new Room();
        var a = room.Store("a");
        var peerId = "b";
        a.UpsertPeer(new PeerInfo(peerId, "127.0.0.1", FreePort(), "B", null, null));
        a.SavePeerCapabilities(peerId, [NodeDefaults.ChatCapability]);
        var now = DateTimeOffset.UtcNow;
        using var client = new PeerClient(a, supportsChat: true);
        var first = new ChatService(a, client, () => now);
        var queued = first.QueueText(peerId, "离线消息");
        await first.PumpAsync();
        Assert.Equal("Queued", a.GetChatMessage(peerId, queued.MessageId, true)!.State);

        var restarted = new ChatService(a, client, () => now.AddDays(8));
        a.WakeChatMessages(peerId);
        await restarted.PumpAsync();
        Assert.Equal("Failed", a.GetChatMessage(peerId, queued.MessageId, true)!.State);
        restarted.Retry(peerId, queued.MessageId);
        Assert.Equal("Queued", a.GetChatMessage(peerId, queued.MessageId, true)!.State);
        Assert.Equal(queued.MessageId, Assert.Single(a.GetChatMessages(peerId)).MessageId);
        // Retry renews the automatic retry window without changing the wire ID or original timestamp.
        Assert.True(a.GetChatAutoExpiry(peerId, queued.MessageId) > now.AddDays(14));
        Assert.Equal(queued.SentUtc, a.GetChatMessage(peerId, queued.MessageId, true)!.SentUtc);
    }

    [Fact]
    public async Task QueuedMessageIsDeliveredWhenPeerReturnsAfterSenderRestart()
    {
        using var room = new Room();
        var a = room.Store("a");
        var b = room.Store("b");
        var portA = FreePort();
        var portB = FreePort();
        a.SaveSettings("A", null, portA, true);
        b.SaveSettings("B", null, portB, true);
        a.UpsertPeer(new PeerInfo(b.GetSettings().Profile.DeviceId, "127.0.0.1", portB, "B", null, null));
        a.SavePeerCapabilities(b.GetSettings().Profile.DeviceId, [NodeDefaults.ChatCapability]);
        b.UpsertPeer(new PeerInfo(a.GetSettings().Profile.DeviceId, "127.0.0.1", portA, "A", null, null));
        using var clientA = new PeerClient(a, supportsChat: true);
        using var clientB = new PeerClient(b, supportsChat: true);
        var beforeRestart = new ChatService(a, clientA);
        var pending = beforeRestart.QueueText(b.GetSettings().Profile.DeviceId, "恢复后收到");
        await beforeRestart.PumpAsync();
        Assert.Equal("Queued", a.GetChatMessage(pending.PeerId, pending.MessageId, true)!.State);

        var afterRestart = new ChatService(a, clientA);
        var chatB = new ChatService(b, clientB);
        await using var nodeB = ChatNode(b, chatB);
        await nodeB.StartAsync(portB, "127.0.0.1");
        a.WakeChatMessages(pending.PeerId);
        await afterRestart.PumpAsync();

        Assert.Equal("Delivered", a.GetChatMessage(pending.PeerId, pending.MessageId, true)!.State);
        Assert.Equal("恢复后收到", Assert.Single(b.GetChatMessages(a.GetSettings().Profile.DeviceId)).Text);
    }

    [Fact]
    public async Task ReceiverRejectsSpoofedDeviceAndLimitsToSixtyPerMinute()
    {
        using var room = new Room();
        var receiver = room.Store("receiver");
        var sender = room.Store("sender");
        using var client = new PeerClient(receiver);
        var chat = new ChatService(receiver, client);
        var senderId = sender.GetSettings().Profile.DeviceId;
        var targetId = receiver.GetSettings().Profile.DeviceId;
        receiver.UpsertPeer(new PeerInfo(senderId, "127.0.0.1", 12345, "sender", null, null));
        ChatMessageRequest Request(int index) => new(NodeDefaults.ChatCapability, Guid.NewGuid().ToString("N"),
            senderId, targetId, DateTimeOffset.UtcNow, "Text", $"message {index}");

        Assert.Equal(403, (await chat.ReceiveAsync(Request(0), "192.0.2.10")).StatusCode);
        for (var i = 0; i < 60; i++) Assert.True((await chat.ReceiveAsync(Request(i), "127.0.0.1")).Accepted);
        Assert.Equal(429, (await chat.ReceiveAsync(Request(61), "127.0.0.1")).StatusCode);
        Assert.Equal(60, receiver.GetChatMessages(senderId).Count);
    }

    [Fact]
    public async Task ResourceRevocationFailsQueuedCard_AndOldPeersCannotQueueChat()
    {
        using var room = new Room();
        var store = room.Store("a");
        var peerId = "b";
        store.UpsertPeer(new PeerInfo(peerId, "127.0.0.1", FreePort(), "B", null, null));
        store.SavePeerCapabilities(peerId, [NodeDefaults.ChatCapability]);
        var path = room.Write("shared.txt", "data");
        var resource = store.AddResource(path, PublishMode.Reference);
        using var client = new PeerClient(store, supportsChat: true);
        var chat = new ChatService(store, client);
        var queued = chat.QueueResource(peerId, resource.Id);
        store.RemoveResource(resource.Id);
        await chat.PumpAsync();
        Assert.Equal("Failed", store.GetChatMessage(peerId, queued.MessageId, true)!.State);

        store.SavePeerCapabilities(peerId, [NodeDefaults.ReminderCapability]);
        Assert.Throws<InvalidOperationException>(() => chat.QueueText(peerId, "legacy"));
    }

    [Fact]
    public async Task ReceiverRejectsResourceCardAfterPublisherRevokesIt()
    {
        using var room = new Room();
        var publisher = room.Store("publisher");
        var receiver = room.Store("receiver");
        var port = FreePort();
        publisher.SaveSettings("publisher", null, port, true);
        publisher.UpsertPeer(new PeerInfo(receiver.GetSettings().Profile.DeviceId, "127.0.0.1", 12345,
            "receiver", null, null));
        receiver.UpsertPeer(new PeerInfo(publisher.GetSettings().Profile.DeviceId, "127.0.0.1", port,
            "publisher", null, null));
        var resource = publisher.AddResource(room.Write("published.txt", "body"), PublishMode.Reference);
        using var publisherClient = new PeerClient(publisher, supportsChat: true);
        using var receiverClient = new PeerClient(receiver, supportsChat: true);
        var chatReceiver = new ChatService(receiver, receiverClient);
        await using var node = ChatNode(publisher, new ChatService(publisher, publisherClient));
        await node.StartAsync(port, "127.0.0.1");
        var request = new ChatMessageRequest(NodeDefaults.ChatCapability, Guid.NewGuid().ToString("N"),
            publisher.GetSettings().Profile.DeviceId, receiver.GetSettings().Profile.DeviceId,
            DateTimeOffset.UtcNow, "Resource", ResourceId: resource.Id);
        var before = await chatReceiver.ReceiveAsync(request, "127.0.0.1");
        Assert.True(before.Accepted);
        publisher.RemoveResource(resource.Id);
        var after = await chatReceiver.ReceiveAsync(request with { MessageId = Guid.NewGuid().ToString("N") },
            "127.0.0.1");
        Assert.Equal(409, after.StatusCode);
        Assert.Single(receiver.GetChatMessages(publisher.GetSettings().Profile.DeviceId));
    }

    [Fact]
    public void RemovePeerCanRetainOrDeleteHistory_AndClearingIsLocal()
    {
        using var room = new Room();
        var store = room.Store("a");
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 12345, "B", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability]);
        var message = store.QueueChatMessage("b", "Text", "hello", null, null, DateTimeOffset.UtcNow);
        store.RemovePeer("b");
        Assert.Equal("Canceled", store.GetChatMessage("b", message.MessageId, true)!.State);
        Assert.True(Assert.Single(store.GetChatConversations()).Removed);
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 12345, "B renamed", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability]);
        Assert.False(Assert.Single(store.GetChatConversations()).Removed);
        Assert.Single(store.GetChatMessages("b"));
        store.ClearChatConversation("b");
        Assert.Empty(store.GetChatMessages("b"));
        store.QueueChatMessage("b", "Text", "again", null, null, DateTimeOffset.UtcNow);
        store.RemovePeer("b", deleteChatHistory: true);
        Assert.Empty(store.GetChatConversations());
        Assert.Empty(store.GetChatMessages("b"));
    }

    [Fact]
    public void PendingConversationIsLimitedToFiveHundred()
    {
        using var room = new Room();
        var store = room.Store("a");
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 12345, "B", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability]);
        for (var i = 0; i < 500; i++)
            store.QueueChatMessage("b", "Text", $"{i}", null, null, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() =>
            store.QueueChatMessage("b", "Text", "501", null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ChatEndpointRejectsOversizedBodyAndText()
    {
        using var room = new Room();
        var receiver = room.Store("receiver");
        var port = FreePort();
        receiver.SaveSettings("receiver", null, port, true);
        receiver.UpsertPeer(new PeerInfo("sender", "127.0.0.1", 12345, "sender", null, null));
        using var client = new PeerClient(receiver);
        await using var node = ChatNode(receiver, new ChatService(receiver, client));
        await node.StartAsync(port, "127.0.0.1");
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{port}/api/v1/chat/messages";
        var tooLong = new ChatMessageRequest(NodeDefaults.ChatCapability, Guid.NewGuid().ToString("N"),
            "sender", receiver.GetSettings().Profile.DeviceId, DateTimeOffset.UtcNow, "Text", new string('a', 2001));
        using var rejected = await http.PostAsJsonAsync(url, tooLong);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var oversize = await http.PostAsync(url, new StringContent(new string('x', ChatService.MaxBodyBytes + 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversize.StatusCode);
    }

    private static PeerNode ChatNode(NodeStore store, ChatService chat) => new(store,
        new LanDiscoveryService(store), new UpnpPortMappingManager(store, new UpnpGatewayClient()),
        chat: chat);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Room : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "ResourceManagerChatTests", Guid.NewGuid().ToString("N"));
        public NodeStore Store(string name) => new(Path.Combine(path, name));
        public string Write(string name, string content)
        {
            Directory.CreateDirectory(path);
            var file = Path.Combine(path, name);
            File.WriteAllText(file, content);
            return file;
        }
        public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
