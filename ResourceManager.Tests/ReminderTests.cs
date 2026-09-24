using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class ReminderTests
{
    [Fact]
    public async Task Reminder_DeliversCatalogVerifiedData_IgnoringRequestBody()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("报告.pdf", "内容"), PublishMode.Reference);
        pair.Sender.SetResourceNote(published.Id, "正式版");
        var deliveries = new List<ReminderDelivery>();
        pair.ReceiverReminders.ReminderReceived += deliveries.Add;
        var hello = await pair.SenderClient.ProbeAsync(pair.ReceiverPeer);
        Assert.Contains(NodeDefaults.ReminderCapability, hello.Capabilities ?? []);

        var receipt = await pair.SenderClient.SendReminderAsync(pair.ReceiverPeer,
            Request(pair, published.Id, name: "伪造名称", note: "伪造备注"));

        Assert.True(receipt.Accepted);
        var delivery = Assert.Single(deliveries);
        Assert.Equal(published.Name, delivery.Resource.Name);
        Assert.Equal("正式版", delivery.Resource.Note);
        Assert.Equal(ResourceKind.File, delivery.Resource.Kind);
        Assert.Equal(pair.Sender.GetSettings().Profile.DeviceId, delivery.Sender.DeviceId);
    }

    [Fact]
    public async Task Reminder_UnknownSource_IsRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space, connect: false);
        var deliveries = new List<ReminderDelivery>();
        pair.ReceiverReminders.ReminderReceived += deliveries.Add;
        var unknown = new PeerInfo("unknown-device", "127.0.0.1", pair.ReceiverPeer.Port, "陌生设备", null, null);
        var request = new ResourceReminderRequest(NodeDefaults.ReminderCapability, Guid.NewGuid().ToString("N"),
            "unknown-device", "resource-1", "资料", ResourceKind.File, "", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.SenderClient.SendReminderAsync(unknown, request));

        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task Reminder_UnregisteredDeviceId_IsRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("资料.txt", "内容"), PublishMode.Reference);
        var request = Request(pair, published.Id) with { SenderDeviceId = "spoofed-device" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, request));

        Assert.Contains("握手登记", error.Message);
    }

    [Fact]
    public async Task Reminder_RevokedOrUnavailableResource_IsRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var source = space.Write("资料.txt", "内容");
        var published = pair.Sender.AddResource(source, PublishMode.Reference);
        var deliveries = new List<ReminderDelivery>();
        pair.ReceiverReminders.ReminderReceived += deliveries.Add;

        var revokedRequest = Request(pair, published.Id);
        pair.Sender.RemoveResource(published.Id);
        var revoked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, revokedRequest));
        Assert.Equal("资源已撤销。", revoked.Message);

        var again = pair.Sender.AddResource(source, PublishMode.Reference);
        var unavailableRequest = Request(pair, again.Id);
        File.Delete(source);
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, unavailableRequest));
        Assert.Equal("资源原文件当前不可用。", unavailable.Message);
        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task Reminder_DuplicateAndRateLimited_AreRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var first = pair.Sender.AddResource(space.Write("一.txt", "内容"), PublishMode.Reference);
        var second = pair.Sender.AddResource(space.Write("二.txt", "内容"), PublishMode.Reference);

        var accepted = await pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, Request(pair, first.Id));
        Assert.True(accepted.Accepted);

        var duplicate = await pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, Request(pair, first.Id));
        Assert.False(duplicate.Accepted);
        Assert.Contains("相同提醒", duplicate.Reason);

        var limited = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, Request(pair, second.Id)));
        Assert.Contains("频繁", limited.Message);
    }

    [Fact]
    public async Task Reminder_ConcurrentDuplicates_AreSerializedBeforeValidation()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("并发.txt", "内容"), PublishMode.Reference);

        var receipts = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, Request(pair, published.Id))));

        Assert.Equal(1, receipts.Count(receipt => receipt.Accepted));
        Assert.All(receipts.Where(receipt => !receipt.Accepted),
            receipt => Assert.Contains("相同提醒", receipt.Reason));
    }

    [Fact]
    public async Task Reminder_MutedSender_IsRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("资料.txt", "内容"), PublishMode.Reference);
        pair.ReceiverReminders.Mute(pair.Sender.GetSettings().Profile.DeviceId, TimeSpan.FromHours(1));

        var receipt = await pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, Request(pair, published.Id));

        Assert.False(receipt.Accepted);
        Assert.Contains("静音", receipt.Reason);
        Assert.True(pair.ReceiverReminders.IsMuted(pair.Sender.GetSettings().Profile.DeviceId));
    }

    [Fact]
    public async Task Reminder_UnsupportedNode_ReportsClearError()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space, receiverSupportsReminders: false);
        var published = pair.Sender.AddResource(space.Write("资料.txt", "内容"), PublishMode.Reference);

        var hello = await pair.SenderClient.ProbeAsync(pair.ReceiverPeer);
        Assert.DoesNotContain(NodeDefaults.ReminderCapability, hello.Capabilities ?? []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, Request(pair, published.Id)));

        Assert.Contains("不支持定向提醒", error.Message);
    }

    [Fact]
    public async Task Reminder_InvalidFields_AreRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("资料.txt", "内容"), PublishMode.Reference);

        var oversized = Request(pair, published.Id) with { Note = new string('长', ReminderService.MaxNoteLength + 1) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, oversized));
        Assert.Contains("无效", error.Message);

        var wrongProtocol = Request(pair, published.Id) with { Protocol = "reminder-v2" };
        var protocolError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, wrongProtocol));
        Assert.Contains("协议", protocolError.Message);
    }

    [Fact]
    public async Task Reminder_NullNote_IsRejectedAsBadRequest()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("空备注.txt", "内容"), PublishMode.Reference);
        var deviceId = pair.Sender.GetSettings().Profile.DeviceId;
        var json = $$"""
            {"protocol":"{{NodeDefaults.ReminderCapability}}","messageId":"{{Guid.NewGuid():N}}","senderDeviceId":"{{deviceId}}","resourceId":"{{published.Id}}","resourceName":"{{published.Name}}","kind":"File","note":null,"sentUtc":"{{DateTimeOffset.UtcNow:O}}"}
            """;
        using var http = new HttpClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await http.PostAsync(
            $"http://127.0.0.1:{pair.ReceiverPeer.Port}/api/v1/reminders", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<ReminderReceipt>();
        Assert.NotNull(receipt);
        Assert.False(receipt.Accepted);
        Assert.Equal(400, receipt.StatusCode);
    }

    [Fact]
    public async Task Reminder_UnverifiableCatalog_IsRejected()
    {
        using var space = new TestSpace();
        await using var pair = await StartPairAsync(space);
        var published = pair.Sender.AddResource(space.Write("资料.txt", "内容"), PublishMode.Reference);
        var request = Request(pair, published.Id);
        await pair.SenderNode.StopAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SenderClient.SendReminderAsync(pair.ReceiverPeer, request));

        Assert.Contains("无法核对", error.Message);
    }

    private static ResourceReminderRequest Request(Pair pair, string resourceId, string? name = null, string? note = null)
    {
        var resource = pair.Sender.GetResource(resourceId)!;
        return new ResourceReminderRequest(NodeDefaults.ReminderCapability, Guid.NewGuid().ToString("N"),
            pair.Sender.GetSettings().Profile.DeviceId, resource.Id, name ?? resource.Name, resource.Kind,
            note ?? resource.Note, DateTimeOffset.UtcNow);
    }

    private static async Task<Pair> StartPairAsync(TestSpace space, bool receiverSupportsReminders = true, bool connect = true)
    {
        var sender = new NodeStore(space.PathFor("sender"));
        var receiver = new NodeStore(space.PathFor("receiver"));
        var senderPort = FreePort();
        var receiverPort = FreePort();
        sender.SaveSettings("发送方", null, senderPort, true);
        receiver.SaveSettings("接收方", null, receiverPort, true);
        var senderClient = new PeerClient(sender);
        var receiverClient = new PeerClient(receiver);
        var receiverReminders = new ReminderService(receiver, receiverClient);
        var senderNode = new PeerNode(sender);
        var receiverNode = new PeerNode(receiver, new LanDiscoveryService(receiver, FreeUdpPort()),
            new UpnpPortMappingManager(receiver, new UpnpGatewayClient()),
            receiverSupportsReminders ? receiverReminders : null);
        try
        {
            await senderNode.StartAsync(senderPort, "127.0.0.1");
            await receiverNode.StartAsync(receiverPort, "127.0.0.1");
            var receiverPeer = connect
                ? await senderClient.ConnectAsync("127.0.0.1", receiverPort)
                : new PeerInfo("receiver", "127.0.0.1", receiverPort, "接收方", null, null);
            return new Pair(sender, receiver, senderNode, receiverNode, senderClient, receiverClient,
                receiverReminders, receiverPeer);
        }
        catch
        {
            senderClient.Dispose();
            receiverClient.Dispose();
            await senderNode.DisposeAsync();
            await receiverNode.DisposeAsync();
            throw;
        }
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

    private sealed class Pair(NodeStore sender, NodeStore receiver, PeerNode senderNode, PeerNode receiverNode,
        PeerClient senderClient, PeerClient receiverClient, ReminderService receiverReminders, PeerInfo receiverPeer)
        : IAsyncDisposable
    {
        public NodeStore Sender { get; } = sender;
        public NodeStore Receiver { get; } = receiver;
        public PeerNode SenderNode { get; } = senderNode;
        public PeerNode ReceiverNode { get; } = receiverNode;
        public PeerClient SenderClient { get; } = senderClient;
        public PeerClient ReceiverClient { get; } = receiverClient;
        public ReminderService ReceiverReminders { get; } = receiverReminders;
        public PeerInfo ReceiverPeer { get; } = receiverPeer;

        public async ValueTask DisposeAsync()
        {
            SenderClient.Dispose();
            ReceiverClient.Dispose();
            await SenderNode.DisposeAsync();
            await ReceiverNode.DisposeAsync();
        }
    }

    private sealed class TestSpace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerTests", Guid.NewGuid().ToString("N"));

        public string PathFor(params string[] names) => Path.Combine([Root, .. names]);

        public string Write(string name, string text)
        {
            Directory.CreateDirectory(Root);
            var path = PathFor(name);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
