using ResourceManager.Core;
namespace ResourceManager.Tests;
public sealed partial class ChatTests
{
    [Theory]
    [InlineData(PublishMode.Reference)]
    [InlineData(PublishMode.Copy)]
    public void DeliveredPrivateResource_CanRevokeWithoutDeletingOriginal(PublishMode mode)
    {
        using var room = new Room();
        var store = room.Store("a");
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 9, "B", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        using var client = new PeerClient(store, supportsChat: true);
        var chat = new ChatService(store, client);
        var source = room.Write("original.txt", "keep");
        var message = chat.QueuePrivateResource("b", source, mode);
        var resource = store.GetPrivateResource(message.ResourceId!, "b")!;
        Assert.Equal(mode, resource.Mode);
        Assert.Equal(mode == PublishMode.Reference, resource.SourcePath == source);
        store.UpdateChatState("b", message.MessageId, "Delivered");
        chat.Cancel("b", message.MessageId);
        // A late delivery acknowledgement must not resurrect a revoked attachment.
        store.UpdateChatState("b", message.MessageId, "Delivered");
        Assert.Equal("Canceled", store.GetChatMessage("b", message.MessageId, true)!.State);
        Assert.Null(store.GetPrivateResource(resource.Id, "b"));
        Assert.Equal("keep", File.ReadAllText(source));
        if (mode == PublishMode.Copy) Assert.False(File.Exists(resource.SourcePath));
    }

    private sealed class CancelCopy(CancellationTokenSource cancellation) : IProgress<long>
    {
        public void Report(long bytes) => cancellation.Cancel();
    }

    [Fact]
    public void CancelDuringCopy_RemovesPartialRoot_AndOrphanCleanupPreservesRegisteredCopies()
    {
        using var room = new Room();
        var store = room.Store("a");
        store.UpsertPeer(new PeerInfo("b", "127.0.0.1", 9, "B", null, null));
        store.SavePeerCapabilities("b", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        using var client = new PeerClient(store, supportsChat: true);
        var chat = new ChatService(store, client);
        var source = room.Write("large.bin", new string('x', 3 * 1024 * 1024));
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => chat.QueuePrivateResource("b", source, PublishMode.Copy, cancel.Token, new CancelCopy(cancel)));
        Assert.Empty(store.GetResources("b"));
        Assert.Empty(Directory.GetDirectories(store.PrivateResourceDirectory));
        var keep = chat.QueuePrivateResource("b", source);
        var orphan = Path.Combine(store.PrivateResourceDirectory, "private-" + new string('a', 64));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "partial"), "aborted");
        var unknown = Path.Combine(store.PrivateResourceDirectory, "user-folder");
        Directory.CreateDirectory(unknown);
        Assert.Equal(1, store.CleanOrphanPrivateCopies());
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(unknown));
        Assert.True(File.Exists(store.GetPrivateResource(keep.ResourceId!, "b")!.SourcePath));
    }
}
