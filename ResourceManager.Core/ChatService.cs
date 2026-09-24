using System.Net;

namespace ResourceManager.Core;

public sealed class ChatService
{
    public const int MaxBodyBytes = 16 * 1024;
    public const int MaxTextLength = 2_000;
    private readonly NodeStore store;
    private readonly PeerClient client;
    private readonly ResourceCatalog catalog;
    private readonly Func<DateTimeOffset> clock;
    private readonly object rateGate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim pumpGate = new(1, 1);

    public event Action<ChatMessage>? MessageReceived;
    public event Action<ChatMessage>? MessageChanged;

    public ChatService(NodeStore store, PeerClient client, Func<DateTimeOffset>? clock = null)
    {
        this.store = store;
        this.client = client;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        catalog = new ResourceCatalog(store);
        store.RecoverSendingChatMessages();
    }

    public ChatMessage QueueText(string peerId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new ArgumentException("文字须为 1 至 2,000 字。");
        var message = store.QueueChatMessage(peerId, "Text", text.Trim(), null, null, clock());
        MessageChanged?.Invoke(message);
        return message;
    }

    public ChatMessage QueueResource(string peerId, string resourceId)
    {
        var resource = catalog.List().FirstOrDefault(item => item.Id == resourceId && item.Available)
            ?? throw new InvalidOperationException("资源已撤销或原文件不可用。");
        var message = store.QueueChatMessage(peerId, "Resource", null, resource.Id, resource.Name, clock());
        MessageChanged?.Invoke(message);
        return message;
    }

    public void Cancel(string peerId, string messageId)
    {
        var message = store.GetChatMessage(peerId, messageId, true);
        if (message?.State != "Queued") throw new InvalidOperationException("只有排队中的消息可以取消。");
        store.UpdateChatState(peerId, messageId, "Canceled", error: "已取消");
        MessageChanged?.Invoke(store.GetChatMessage(peerId, messageId, true)!);
    }

    public void Retry(string peerId, string messageId)
    {
        var message = store.GetChatMessage(peerId, messageId, true);
        if (message?.State != "Failed") throw new InvalidOperationException("只有失败消息可以重试。");
        if (store.GetPeer(peerId) is null) throw new InvalidOperationException("设备已移除。");
        if (!store.GetPeerCapabilities(peerId).Contains(NodeDefaults.ChatCapability, StringComparer.Ordinal))
            throw new InvalidOperationException("对方版本不支持聊天。");
        store.RetryChatMessage(peerId, messageId, clock());
        MessageChanged?.Invoke(store.GetChatMessage(peerId, messageId, true)!);
    }

    public async Task<ChatReceipt> ReceiveAsync(ChatMessageRequest request, string remoteIp,
        CancellationToken cancellationToken = default)
    {
        if (request.Protocol != NodeDefaults.ChatCapability || !ValidId(request.MessageId, 64) ||
            !ValidId(request.SenderDeviceId, 100) ||
            request.RecipientDeviceId != store.GetSettings().Profile.DeviceId || request.SentUtc == default ||
            request.SenderDeviceId == request.RecipientDeviceId ||
            !(request.Kind == "Text" && !string.IsNullOrWhiteSpace(request.Text) &&
              request.Text.Length <= MaxTextLength && request.ResourceId is null ||
              request.Kind == "Resource" && ValidId(request.ResourceId, 100) && request.Text is null))
            return new ChatReceipt(false, "聊天内容或目标设备无效。", 400);

        var sender = store.GetPeer(request.SenderDeviceId);
        if (sender is null || !store.GetPeerEndpoints(sender.DeviceId).Any(endpoint => endpoint.Ip == remoteIp &&
                endpoint.Source != "DeviceChanged"))
            return new ChatReceipt(false, "发送设备与已登记入口不匹配。", 403);

        var previous = store.GetChatMessage(sender.DeviceId, request.MessageId, false);
        if (previous is not null)
            return previous.Kind == request.Kind && previous.Text == request.Text &&
                   previous.ResourceId == request.ResourceId && previous.SentUtc == request.SentUtc
                ? new ChatReceipt(true, Duplicate: true, ReceivedUtc: previous.ReceivedUtc)
                : new ChatReceipt(false, "消息 ID 已对应不同内容。", 409);

        var now = clock();
        lock (rateGate)
        {
            if (!accepted.TryGetValue(sender.DeviceId, out var history))
                accepted[sender.DeviceId] = history = new Queue<DateTimeOffset>();
            while (history.Count > 0 && now - history.Peek() >= TimeSpan.FromMinutes(1)) history.Dequeue();
            if (history.Count + inFlight.GetValueOrDefault(sender.DeviceId) >= 60)
                return new ChatReceipt(false, "每分钟最多发送 60 条消息。", 429);
            inFlight[sender.DeviceId] = inFlight.GetValueOrDefault(sender.DeviceId) + 1;
        }
        try
        {
            string? resourceName = null;
            if (request.Kind == "Resource")
            {
                IReadOnlyList<RemoteResource> resources;
                try { resources = await client.GetResourcesAsync(sender, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { return new ChatReceipt(false, "无法核对发布方资源目录。", 503); }
                var resource = resources.FirstOrDefault(item => item.Id == request.ResourceId);
                if (resource is null || !resource.Available)
                    return new ChatReceipt(false, "资源已撤销或原文件不可用。", 409);
                resourceName = resource.Name;
            }

            var result = store.AcceptChatMessage(request, sender.Nickname, resourceName, now);
            if (result.Inserted)
            {
                lock (rateGate) accepted[sender.DeviceId].Enqueue(now);
                MessageReceived?.Invoke(store.GetChatMessage(sender.DeviceId, request.MessageId, false)!);
            }
            return new ChatReceipt(true, Duplicate: !result.Inserted, ReceivedUtc: result.ReceivedUtc);
        }
        finally
        {
            lock (rateGate) inFlight[sender.DeviceId]--;
        }
    }

    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        if (!await pumpGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            // One worker per device preserves that conversation's order without blocking other devices.
            var due = store.GetDueChatMessages(clock()).GroupBy(message => message.PeerId);
            await Task.WhenAll(due.Select(group => PumpPeerAsync(group.Key, cancellationToken))).ConfigureAwait(false);
        }
        finally { pumpGate.Release(); }
    }

    private async Task PumpPeerAsync(string peerId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = store.GetDueChatMessages(clock()).FirstOrDefault(item => item.PeerId == peerId);
            if (message is null) return;
            var now = clock();
            if (now >= store.GetChatAutoExpiry(peerId, message.MessageId))
            {
                SetState(message, "Failed", error: "7 天未送达，已停止自动发送；可手动重试。");
                continue;
            }
            var peer = store.GetPeer(peerId);
            if (peer is null) { SetState(message, "Failed", error: "设备已移除。"); continue; }
            if (!store.GetPeerCapabilities(peerId).Contains(NodeDefaults.ChatCapability, StringComparer.Ordinal))
            {
                SetState(message, "Failed", error: "对方版本不支持聊天。");
                continue;
            }
            if (message.Kind == "Resource" && !catalog.List().Any(item => item.Id == message.ResourceId && item.Available))
            {
                SetState(message, "Failed", error: "资源已撤销或原文件不可用。");
                continue;
            }
            store.UpdateChatState(peerId, message.MessageId, "Sending", incrementAttempt: true);
            MessageChanged?.Invoke(store.GetChatMessage(peerId, message.MessageId, true)!);
            var request = new ChatMessageRequest(NodeDefaults.ChatCapability, message.MessageId,
                store.GetSettings().Profile.DeviceId, peerId, message.SentUtc, message.Kind,
                message.Text, message.ResourceId);
            try
            {
                var result = await client.SendChatAsync(peer, request, cancellationToken).ConfigureAwait(false);
                if (result.Accepted)
                {
                    SetState(message, "Delivered", received: result.ReceivedUtc ?? clock());
                    continue;
                }
                if (result.StatusCode == 429 || result.StatusCode >= 500)
                {
                    Backoff(message, result.Reason);
                    return;
                }
                SetState(message, "Failed", error: result.Reason ?? "对方拒绝了消息。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                store.UpdateChatState(peerId, message.MessageId, "Queued", clock());
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                Backoff(message, "网络暂不可用，稍后自动重试。");
                return;
            }
            catch (Exception)
            {
                Backoff(message, "发送暂时失败，稍后自动重试。");
                return;
            }
        }
    }

    private void Backoff(ChatMessage message, string? reason)
    {
        var attempts = store.GetChatMessage(message.PeerId, message.MessageId, true)!.Attempts;
        var delay = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(attempts - 1, 5))));
        SetState(message, "Queued", clock() + delay, reason);
    }

    private void SetState(ChatMessage message, string state, DateTimeOffset? next = null,
        string? error = null, DateTimeOffset? received = null)
    {
        store.UpdateChatState(message.PeerId, message.MessageId, state, next, error, received);
        MessageChanged?.Invoke(store.GetChatMessage(message.PeerId, message.MessageId, true)!);
    }

    private static bool ValidId(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
}
