using System.Net;
using System.Net.Http.Json;
using ResourceManager.Core;
namespace ResourceManager.Tests;
public sealed partial class ChatTests
{
    [Fact]
    public async Task ProgressReceipt_TracksReadAndRealDownload_RequiresIdentity_AndSurvivesRestart()
    {
        using var room = new Room();
        var a = room.Store("a"); var b = room.Store("b");
        var portA = FreePort(); var portB = FreePort();
        a.SaveSettings("A", null, portA, true); b.SaveSettings("B", null, portB, true);
        using var clientA = new PeerClient(a, supportsChat: true);
        using var clientB = new PeerClient(b, supportsChat: true);
        var now = DateTimeOffset.UtcNow;
        var chatA = new ChatService(a, clientA, () => now);
        var chatB = new ChatService(b, clientB);
        await using var nodeA = ChatNode(a, chatA); await using var nodeB = ChatNode(b, chatB);
        await nodeA.StartAsync(portA, "127.0.0.1"); await nodeB.StartAsync(portB, "127.0.0.1");
        var peerB = await clientA.ConnectAsync("127.0.0.1", portB);
        var peerA = b.GetPeer(a.GetSettings().Profile.DeviceId)!;
        var source = room.Write("receipt-file.txt", "downloaded contents");
        var sent = chatA.QueuePrivateResource(peerB.DeviceId, source, PublishMode.Reference);
        await chatA.PumpAsync();
        Assert.Equal("Delivered", a.GetChatMessage(peerB.DeviceId, sent.MessageId, true)!.State);
        var initial = Assert.Single(await clientA.GetChatProgressAsync(peerB, [sent.MessageId]));
        Assert.False(initial.Read); Assert.Equal("未开始", initial.DownloadState);
        using var unsigned = new HttpClient();
        using var denied = await unsigned.PostAsJsonAsync($"http://127.0.0.1:{portB}/api/v1/chat/progress", new ChatProgressQuery([sent.MessageId]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(b.GetLocalChatProgress("someone-else", sent.MessageId));
        var c = room.Store("c");
        c.SaveSettings("C", null, FreePort(), true);
        using var clientC = new PeerClient(c, supportsChat: true);
        var peerForC = await clientC.ConnectAsync("127.0.0.1", portB);
        Assert.Empty(await clientC.GetChatProgressAsync(peerForC, [sent.MessageId]));
        b.MarkChatRead(peerA.DeviceId);
        var resource = await clientB.GetResourceAsync(peerA, sent.ResourceId!);
        var downloads = new DownloadManager(b, clientB);
        var destination = Path.Combine(b.DataDirectory, "downloads");
        var job = downloads.CreateJob(peerA, resource!, destination);
        b.SaveDownload(job with { Status = "下载中", DownloadedBytes = 3 });
        var partial = Assert.Single(await clientA.GetChatProgressAsync(peerB, [sent.MessageId]));
        Assert.True(partial.Read); Assert.Equal("下载中", partial.DownloadState); Assert.Equal(3, partial.DownloadedBytes);
        var completed = await downloads.RunAsync(job.Id);
        Assert.Equal("downloaded contents", File.ReadAllText(completed.TargetPath));
        now = now.AddSeconds(11);
        await chatA.RefreshProgressAsync();
        var cached = a.GetCachedChatProgress(peerB.DeviceId, sent.MessageId)!;
        Assert.True(cached.Read); Assert.Equal("已完成", cached.DownloadState);
        Assert.Equal(cached.TotalBytes, cached.DownloadedBytes);
        var reopened = new NodeStore(a.DataDirectory);
        Assert.Equal(cached, reopened.GetCachedChatProgress(peerB.DeviceId, sent.MessageId));
        b.RemoveDownload(job.Id);
        Assert.Equal("已完成", b.GetLocalChatProgress(peerA.DeviceId, sent.MessageId)!.DownloadState);
        a.ClearChatConversation(peerB.DeviceId);
        Assert.Null(a.GetCachedChatProgress(peerB.DeviceId, sent.MessageId));
    }
}
