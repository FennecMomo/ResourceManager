namespace ResourceManager.Core;

public sealed class ReminderService(NodeStore store, PeerClient client)
{
    public const int MaxNoteLength = 200;
    public const int MaxNameLength = 260;
    public const int MaxBodyBytes = 16 * 1024;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HourlyWindow = TimeSpan.FromHours(1);
    private const int MaxPerHour = 10;

    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> muted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastAccepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastSameResource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> acceptedHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> inFlightResources = new(StringComparer.Ordinal);

    public event Action<ReminderDelivery>? ReminderReceived;

    public async Task<ReminderReceipt> ReceiveAsync(ResourceReminderRequest request, string remoteIp,
        CancellationToken cancellationToken = default)
    {
        if (request.Protocol != NodeDefaults.ReminderCapability)
            return new ReminderReceipt(false, "提醒协议版本不受支持。", 400);
        if (!Valid(request.MessageId, 64) || !Valid(request.SenderDeviceId, 100) || !Valid(request.ResourceId, 100) ||
            !Valid(request.ResourceName, MaxNameLength) || request.Note is null ||
            request.Note.Length > MaxNoteLength || request.SentUtc == default)
            return new ReminderReceipt(false, "提醒内容无效。", 400);
        if (request.SenderDeviceId == store.GetSettings().Profile.DeviceId)
            return new ReminderReceipt(false, "不能给自己发送提醒。", 400);

        var sender = store.GetPeer(request.SenderDeviceId);
        if (sender is null || !store.GetPeerEndpoints(sender.DeviceId).Any(endpoint => endpoint.Ip == remoteIp))
            return new ReminderReceipt(false, "发送设备未完成握手登记。", 403);

        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (muted.TryGetValue(sender.DeviceId, out var until) && until > now)
                return new ReminderReceipt(false, "对方已暂时静音提醒。");
            if (muted.TryGetValue(sender.DeviceId, out until) && until <= now) muted.Remove(sender.DeviceId);
            var dedupeKey = sender.DeviceId + "\0" + request.ResourceId;
            if (lastSameResource.TryGetValue(dedupeKey, out var same) && now - same < DuplicateWindow)
                return new ReminderReceipt(false, "相同提醒刚刚已经发送过。");
            if (lastAccepted.TryGetValue(sender.DeviceId, out var last) && now - last < MinimumInterval)
                return new ReminderReceipt(false, "提醒发送过于频繁，请稍后再试。", 429);
            if (acceptedHistory.TryGetValue(sender.DeviceId, out var history) &&
                history.Count(item => now - item < HourlyWindow) >= MaxPerHour)
                return new ReminderReceipt(false, "提醒发送过于频繁，请稍后再试。", 429);
            if (inFlightResources.TryGetValue(sender.DeviceId, out var inFlightResource))
                return new ReminderReceipt(false,
                    inFlightResource == request.ResourceId ? "相同提醒正在处理中。" : "提醒发送过于频繁，请稍后再试。",
                    inFlightResource == request.ResourceId ? 200 : 429);
            inFlightResources[sender.DeviceId] = request.ResourceId;
        }

        try
        {
            IReadOnlyList<RemoteResource> catalog;
            try
            {
                catalog = await client.GetResourcesAsync(sender, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return new ReminderReceipt(false, "无法核对发布方资源目录，请稍后再试。", 409);
            }
            var resource = catalog.FirstOrDefault(item => item.Id == request.ResourceId);
            if (resource is null) return new ReminderReceipt(false, "资源已撤销。", 409);
            if (!resource.Available) return new ReminderReceipt(false, "资源原文件当前不可用。", 409);

            var acceptedAt = DateTimeOffset.UtcNow;
            lock (gate)
            {
                var dedupeKey = sender.DeviceId + "\0" + resource.Id;
                lastSameResource[dedupeKey] = acceptedAt;
                lastAccepted[sender.DeviceId] = acceptedAt;
                if (!acceptedHistory.TryGetValue(sender.DeviceId, out var history))
                {
                    history = new Queue<DateTimeOffset>();
                    acceptedHistory[sender.DeviceId] = history;
                }
                history.Enqueue(acceptedAt);
                while (history.Count > 0 && acceptedAt - history.Peek() >= HourlyWindow) history.Dequeue();
            }

            ReminderReceived?.Invoke(new ReminderDelivery(sender, resource, request.SentUtc, acceptedAt));
            return new ReminderReceipt(true);
        }
        finally
        {
            lock (gate)
            {
                if (inFlightResources.GetValueOrDefault(sender.DeviceId) == request.ResourceId)
                    inFlightResources.Remove(sender.DeviceId);
            }
        }
    }

    public void Mute(string deviceId, TimeSpan duration)
    {
        lock (gate) muted[deviceId] = DateTimeOffset.UtcNow.Add(duration);
    }

    public bool IsMuted(string deviceId)
    {
        lock (gate) return muted.TryGetValue(deviceId, out var until) && until > DateTimeOffset.UtcNow;
    }

    private static bool Valid(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);
}
