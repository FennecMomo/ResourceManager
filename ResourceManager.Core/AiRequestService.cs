namespace ResourceManager.Core;

public sealed class AiRequestService : IAsyncDisposable
{
    public const string Capability = "ai-file-request-v1";
    private readonly NodeStore store;
    private readonly ChatService chat;
    private readonly IAiFileFinder finder;
    private readonly object gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Task)> running = [];
    private bool disposed;
    public event Action? Changed;
    public AiRequestService(NodeStore store, ChatService chat, IAiFileFinder finder)
    {
        this.store = store; this.chat = chat; this.finder = finder;
        foreach (var session in store.GetAiSearchSessions().Where(s => s.State is "Running" or "Sending"))
            store.SaveAiSearchSession(session with { State = "Failed", Error = "客户端已重启；任务未自动重放，请重新请求。" });
    }
    private static void ValidateQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 4000) throw new ArgumentException("请求内容须为 1 至 4,000 字。");
    }
    private void Save(AiSearchSession session) { store.SaveAiSearchSession(session); Changed?.Invoke(); }
    private AiSearchSession Find(string id, string? device = null)
    {
        var session = store.GetAiSearchSessions().SingleOrDefault(s => s.Id == id && (device is null || s.DeviceId == device))
            ?? throw new KeyNotFoundException("请求不存在。");
        if (session.ExpiresUtc <= DateTimeOffset.UtcNow && session.State is not ("Expired" or "Rejected" or "Canceled"))
        {
            if (running.TryGetValue(id, out var run)) run.Cancellation.Cancel();
            session = session with { State = "Expired", Error = "授权会话已过期，请重新发起请求。" };
            Save(session);
        }
        return session;
    }
    public static AiSearchView View(AiSearchSession session) => new(session.Id, session.State, session.Question,
        session.ExpiresUtc, session.Answer, session.Files ?? [], session.Turns, session.Error);
    public AiSearchSession[] ListLocal()
    {
        lock (gate) return store.GetAiSearchSessions().Select(s => Find(s.Id)).ToArray();
    }
    public AiSearchView Get(string device, string id) { lock (gate) return View(Find(id, device)); }
    public AiSearchView Receive(string device, AiSearchRequest request)
    {
        ValidateQuestion(request.Question);
        if (!Guid.TryParseExact(request.Id, "N", out _) || request.ScopeId?.Length > 100) throw new ArgumentException("请求 ID 无效。");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (store.GetPeer(device) is null) throw new UnauthorizedAccessException("未登记设备。");
            if (store.GetAiSearchSessions().Any(s => s.Id == request.Id)) return View(Find(request.Id, device));
            var sessions = ListLocal();
            if (sessions.Count(s => s.ExpiresUtc > DateTimeOffset.UtcNow) >= 100 ||
                sessions.Count(s => s.State is "AwaitingApproval" or "Running" or "Sending") >= 20 ||
                sessions.Count(s => s.DeviceId == device && s.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(-10)) >= 5)
                throw new InvalidOperationException("AI 请求过于频繁，请稍后重试。");
            var session = new AiSearchSession(request.Id, device, request.Question.Trim(), "AwaitingApproval", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30));
            Save(session);
            var scopes = store.GetAiSearchScopes().Where(s => s.DeviceId == device && s.AutoApprove &&
                (request.ScopeId is null || request.ScopeId == s.Id)).ToArray();
            if (scopes.Length == 1)
            {
                try { Approve(session.Id, scopes[0].Id); }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
                { Save(session with { Error = "免授权启动失败，请在本机检查配置：" + ex.Message }); }
            }
            return View(Find(session.Id));
        }
    }
    public void Approve(string id, string scopeId)
    {
        lock (gate)
        {
            var session = Find(id);
            if (session.State != "AwaitingApproval") throw new InvalidOperationException("此请求已不在等待授权。");
            var scope = store.GetAiSearchScopes().SingleOrDefault(s => s.Id == scopeId && s.DeviceId == session.DeviceId)
                ?? throw new ArgumentException("请选择此请求设备对应的目录授权。");
            ScopedAiFiles.ValidateRoot(scope.Directory);
            if (!File.Exists(store.GetAiCodexExecutable())) throw new InvalidOperationException("请先配置本机 Codex CLI 路径并完成登录。");
            StartFind(session with { ScopeId = scope.Id, Directory = scope.Directory }, session.Question);
        }
    }
    private void CheckScope(AiSearchSession session)
    {
        if (store.GetPeer(session.DeviceId) is null || !store.GetAiSearchScopes().Any(s => s.Id == session.ScopeId &&
            s.DeviceId == session.DeviceId && s.Directory == session.Directory)) throw new UnauthorizedAccessException("目录授权已撤销。");
        if (session.ExpiresUtc <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("授权会话已过期。");
    }
    private void StartFind(AiSearchSession session, string question)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CheckScope(session);
        if (session.Turns >= 10) throw new InvalidOperationException("此会话已达到 10 轮，请重新申请授权。");
        if (running.Count >= 2 || running.ContainsKey(session.Id)) throw new InvalidOperationException("已有任务运行，请稍后重试。");
        var next = session with { State = "Running", Question = question, Turns = session.Turns + 1, Error = null, Files = [] };
        Save(next);
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var task = Task.Run(async () =>
        {
            try
            {
                var result = await finder.FindAsync(store.GetAiCodexExecutable(), session.Directory!, question, session.Answer, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var scoped = new ScopedAiFiles(session.Directory!);
                var files = result.RelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).Select(path =>
                {
                    using var stream = scoped.OpenRead(path);
                    return new AiFoundFile(Guid.NewGuid().ToString("N"), path, stream.Length);
                }).ToArray();
                lock (gate)
                {
                    var current = Find(session.Id);
                    if (current.State != "Running") return;
                    CheckScope(current);
                    Save(current with { State = "Ready", Answer = result.Answer[..Math.Min(result.Answer.Length, 16000)], Files = files });
                }
            }
            catch (Exception ex) { Fail(session.Id, ex, cancellation.IsCancellationRequested); }
            finally { lock (gate) { running.Remove(session.Id); cancellation.Dispose(); } }
        });
        running.Add(session.Id, (cancellation, task));
    }
    private void Fail(string id, Exception ex, bool canceled)
    {
        lock (gate)
        {
            var session = Find(id);
            if (session.State is "Running" or "Sending")
                Save(session with { State = canceled ? "Canceled" : "Failed", Error = canceled ? "任务已取消或超时。" : ex.Message });
        }
    }
    public AiSearchView Act(string device, string id, AiSearchAction action)
    {
        lock (gate)
        {
            var session = Find(id, device);
            switch (action.Action)
            {
                case "cancel": Cancel(id); break;
                case "continue":
                    ValidateQuestion(action.Text ?? "");
                    if (session.State != "Ready") throw new InvalidOperationException("等待本轮结束后才能追问。");
                    StartFind(session, action.Text!); break;
                case "send_file":
                    if (session.State != "Ready") throw new InvalidOperationException("请求尚未得到可发送的文件。");
                    CheckScope(session);
                    var file = session.Files?.SingleOrDefault(f => f.Id == action.FileId) ?? throw new ArgumentException("只能发送当前结果中明确列出的文件。");
                    if (file.MessageId is null) StartSend(session, file);
                    break;
                default: throw new ArgumentException("未知请求操作。");
            }
            return View(Find(id, device));
        }
    }
    private void StartSend(AiSearchSession session, AiFoundFile file)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running.ContainsKey(session.Id) || running.Count >= 2) throw new InvalidOperationException("已有任务运行，请稍后重试。");
        Save(session with { State = "Sending", Error = null });
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var task = Task.Run(() =>
        {
            ChatMessage? message = null;
            try
            {
                var scoped = new ScopedAiFiles(session.Directory!);
                // The open handle pins this file against replacement while the managed copy is prepared.
                using var pinned = scoped.OpenRead(file.RelativePath);
                message = chat.QueuePrivateResource(session.DeviceId, scoped.Resolve(file.RelativePath), PublishMode.Copy, cancellation.Token);
                lock (gate)
                {
                    var current = Find(session.Id);
                    CheckScope(current);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (current.State != "Sending") throw new OperationCanceledException();
                    Save(current with { State = "Ready", Files = current.Files!.Select(f => f.Id == file.Id
                        ? f with { MessageId = message.MessageId, ResourceId = message.ResourceId } : f).ToArray() });
                }
            }
            catch (Exception ex)
            {
                if (message is not null)
                {
                    try { chat.Cancel(session.DeviceId, message.MessageId); }
                    catch (Exception cleanup) when (cleanup is IOException or InvalidOperationException) { }
                }
                Fail(session.Id, ex, cancellation.IsCancellationRequested);
            }
            finally { lock (gate) { running.Remove(session.Id); cancellation.Dispose(); } }
        });
        running.Add(session.Id, (cancellation, task));
    }
    public void Cancel(string id, bool reject = false)
    {
        lock (gate)
        {
            var session = Find(id);
            if (running.TryGetValue(id, out var run)) run.Cancellation.Cancel();
            Save(session with { State = reject ? "Rejected" : "Canceled", Error = "本次 AI 请求已停止。" });
        }
    }
    public void RemoveScope(string id)
    {
        lock (gate)
        {
            foreach (var session in ListLocal().Where(s => s.ScopeId == id && s.State is "Running" or "Sending" or "Ready")) Cancel(session.Id);
            store.RemoveAiSearchScope(id); Changed?.Invoke();
        }
    }
    public void RevokeDevice(string deviceId)
    {
        lock (gate)
        {
            foreach (var session in ListLocal().Where(s => s.DeviceId == deviceId && s.State is "AwaitingApproval" or "Running" or "Sending" or "Ready"))
                Cancel(session.Id);
            foreach (var scope in store.GetAiSearchScopes().Where(s => s.DeviceId == deviceId)) store.RemoveAiSearchScope(scope.Id);
            Changed?.Invoke();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (gate)
        {
            disposed = true;
            foreach (var run in running.Values) run.Cancellation.Cancel();
            tasks = running.Values.Select(r => r.Task).ToArray();
        }
        await Task.WhenAll(tasks);
    }
}
