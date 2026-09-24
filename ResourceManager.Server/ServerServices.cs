using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ResourceManager.Core;

namespace ResourceManager.Server;

public sealed class FeedbackDiscoveryService(ServerStore store, ServerRuntimeOptions options, ILogger<FeedbackDiscoveryService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, options.DiscoveryPort));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(stoppingToken);
                using var document = JsonDocument.Parse(packet.Buffer);
                var root = document.RootElement;
                if (!root.TryGetProperty("protocol", out var protocol) || protocol.GetString() != FeedbackRules.DiscoveryProtocol ||
                    !root.TryGetProperty("type", out var type) || type.GetString() != "query") continue;
                var advertisement = new FeedbackServerAdvertisement(FeedbackRules.DiscoveryProtocol, "response", store.ServerId,
                    store.ServerName, options.ApiPort, options.Version);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(advertisement, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await udp.SendAsync(bytes, packet.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "处理服务发现请求失败"); }
        }
    }
}

public sealed class SubmissionLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> clientSubmissions = [];
    private readonly Queue<DateTimeOffset> globalSubmissions = [];

    public bool TryAcquire(string clientId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (clientSubmissions.TryGetValue(clientId, out var previous) && now - previous < TimeSpan.FromMinutes(1)) return false;
            while (globalSubmissions.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromHours(1)) globalSubmissions.Dequeue();
            if (globalSubmissions.Count >= 100) return false;
            clientSubmissions[clientId] = now;
            globalSubmissions.Enqueue(now);
            return true;
        }
    }
}

public sealed record PendingGitHubFlow(GitHubDeviceCode Code, DateTimeOffset ExpiresUtc);

public sealed class GitHubSessionManager
{
    private readonly ServerStore store;
    private readonly GitHubFeedbackClient github;
    private readonly IDataProtector protector;
    private readonly ILogger<GitHubSessionManager> logger;
    private readonly ConcurrentDictionary<string, PendingGitHubFlow> flows = new();
    public string ClientId { get; }

    public GitHubSessionManager(ServerStore store, GitHubFeedbackClient github, IDataProtectionProvider protection,
        IConfiguration configuration, ILogger<GitHubSessionManager> logger)
    {
        this.store = store;
        this.github = github;
        this.logger = logger;
        protector = protection.CreateProtector("ResourceManager.Server.GitHubSession.v1");
        ClientId = Environment.GetEnvironmentVariable("ResourceManager_GitHubClientId") ?? configuration["GitHub:ClientId"] ?? "";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
    public string? RepositoryOwner => store.GetSetting("github_owner");
    public string? RepositoryName => store.GetSetting("github_repo");
    public string? Account => store.GetSetting("github_account");

    public async Task<(string FlowId, GitHubDeviceCode Code)> StartAsync(CancellationToken cancellationToken)
    {
        var code = await github.StartDeviceFlowAsync(ClientId, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        flows[id] = new PendingGitHubFlow(code, DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn));
        return (id, code);
    }

    public async Task<(bool Complete, string? Account)> PollAsync(string flowId, CancellationToken cancellationToken)
    {
        if (!flows.TryGetValue(flowId, out var flow) || flow.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("GitHub 登录请求已过期。");
        GitHubSession? session;
        try { session = await github.PollDeviceFlowAsync(ClientId, flow.Code.DeviceCode, cancellationToken); }
        catch (GitHubAuthorizationSlowDownException) { return (false, null); }
        if (session is null) return (false, null);
        var user = await github.GetUserAsync(session.AccessToken, cancellationToken);
        SaveSession(session);
        store.SetSetting("github_account", user.Login);
        flows.TryRemove(flowId, out _);
        return (true, user.Login);
    }

    public async Task<GitHubSession> GetValidSessionAsync(CancellationToken cancellationToken)
    {
        var protectedValue = store.GetSetting("github_session") ?? throw new InvalidOperationException("尚未登录 GitHub。");
        var session = JsonSerializer.Deserialize<GitHubSession>(protector.Unprotect(protectedValue))
            ?? throw new InvalidDataException("GitHub 登录资料无效。");
        if (session.ExpiresUtc is null || session.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return session;
        if (string.IsNullOrEmpty(session.RefreshToken)) throw new InvalidOperationException("GitHub 登录已过期，请重新登录。");
        session = await github.RefreshAsync(ClientId, session.RefreshToken, cancellationToken);
        SaveSession(session);
        return session;
    }

    public async Task<GitHubRepository> BindRepositoryAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository) || owner.Length > 100 || repository.Length > 100)
            throw new ArgumentException("GitHub 仓库名称无效。");
        var session = await GetValidSessionAsync(cancellationToken);
        var result = await github.GetRepositoryAsync(session.AccessToken, owner.Trim(), repository.Trim(), cancellationToken);
        if (!result.HasIssues) throw new InvalidOperationException("此仓库没有启用 Issues。");
        store.SetSetting("github_owner", result.Owner);
        store.SetSetting("github_repo", result.Name);
        await SyncAsync(cancellationToken);
        return result;
    }

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (RepositoryOwner is not { } owner || RepositoryName is not { } repository) return;
        var session = await GetValidSessionAsync(cancellationToken);
        var issues = await github.ListIssuesAsync(session.AccessToken, owner, repository, cancellationToken);
        store.SaveGitHubIssues(issues);
        store.SetSetting("github_last_sync", DateTimeOffset.UtcNow.ToString("O"));
    }

    public async Task<GitHubIssue> SetStateAsync(int number, string state, string? reason, CancellationToken cancellationToken)
    {
        if (number < 1 || state is not ("open" or "closed") || reason is not (null or "completed" or "not_planned" or "reopened"))
            throw new ArgumentException("GitHub Issue 状态无效。");
        if (RepositoryOwner is not { } owner || RepositoryName is not { } repository)
            throw new InvalidOperationException("尚未绑定 GitHub 仓库。");
        var session = await GetValidSessionAsync(cancellationToken);
        var issue = await github.SetIssueStateAsync(session.AccessToken, owner, repository, number, state, reason, cancellationToken);
        await SyncAsync(cancellationToken);
        return issue;
    }

    public void Logout()
    {
        store.SetSetting("github_session", null);
        store.SetSetting("github_account", null);
        store.SetSetting("github_owner", null);
        store.SetSetting("github_repo", null);
        store.SaveGitHubIssues([]);
    }

    private void SaveSession(GitHubSession session) =>
        store.SetSetting("github_session", protector.Protect(JsonSerializer.Serialize(session)));
}

public sealed class GitHubSyncService(GitHubSessionManager manager, ILogger<GitHubSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await manager.SyncAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "同步 GitHub Issue 失败"); }
        }
    }
}

public sealed record ServerRuntimeOptions(string DataDirectory, string ServerName, int ApiPort, int DiscoveryPort, int AdminPort, string Version);
