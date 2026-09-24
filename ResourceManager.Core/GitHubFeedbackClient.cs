using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Core;

public sealed record GitHubDeviceCode(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);
public sealed record GitHubSession(string AccessToken, DateTimeOffset? ExpiresUtc, string? RefreshToken, DateTimeOffset? RefreshExpiresUtc);
public sealed record GitHubUser(string Login, long Id, string AvatarUrl);
public sealed record GitHubRepository(string Owner, string Name, string FullName, string HtmlUrl, bool HasIssues);
public sealed record GitHubIssue(int Number, string Title, string Body, string State, string? StateReason, string HtmlUrl,
    string Author, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, IReadOnlyList<string> Labels);

public sealed class GitHubAuthorizationPendingException : Exception;
public sealed class GitHubAuthorizationSlowDownException : Exception;

public sealed class GitHubFeedbackClient : IDisposable
{
    public const string OfficialOwner = "FennecMomo";
    public const string OfficialRepository = "ResourceManager";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public GitHubFeedbackClient(HttpClient? httpClient = null)
    {
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ResourceManager-Feedback/0.3.5");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken = default)
    {
        RequireClientId(clientId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId })
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<DeviceCodePayload>(Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub 设备登录响应无效。");
        return new GitHubDeviceCode(payload.DeviceCode, payload.UserCode, payload.VerificationUri, payload.ExpiresIn, Math.Max(payload.Interval, 5));
    }

    public async Task<GitHubSession?> PollDeviceFlowAsync(string clientId, string deviceCode, CancellationToken cancellationToken = default)
    {
        RequireClientId(clientId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            })
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TokenPayload>(Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub 登录响应无效。");
        if (payload.Error == "authorization_pending") return null;
        if (payload.Error == "slow_down") throw new GitHubAuthorizationSlowDownException();
        if (!string.IsNullOrEmpty(payload.Error)) throw new InvalidOperationException(payload.ErrorDescription ?? payload.Error);
        return ToSession(payload);
    }

    public async Task<GitHubSession> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TokenPayload>(Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub 刷新令牌响应无效。");
        if (!string.IsNullOrEmpty(payload.Error)) throw new InvalidOperationException(payload.ErrorDescription ?? payload.Error);
        return ToSession(payload);
    }

    public async Task<GitHubUser> GetUserAsync(string token, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "https://api.github.com/user", token, null, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<UserPayload>(Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub 用户响应无效。");
        return new GitHubUser(payload.Login, payload.Id, payload.AvatarUrl);
    }

    public async Task<GitHubRepository> GetRepositoryAsync(string token, string owner, string repository,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, Repo(owner, repository), token, null, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<RepositoryPayload>(Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub 仓库响应无效。");
        return new GitHubRepository(payload.Owner.Login, payload.Name, payload.FullName, payload.HtmlUrl, payload.HasIssues);
    }

    public async Task<GitHubIssue> CreateIssueAsync(string token, string owner, string repository,
        FeedbackSubmission submission, CancellationToken cancellationToken = default)
    {
        var payload = new { title = submission.Title.Trim(), body = FeedbackRules.BuildGithubBody(submission) };
        using var response = await SendAsync(HttpMethod.Post, Repo(owner, repository) + "/issues", token, JsonContent.Create(payload), cancellationToken);
        return await ReadIssueAsync(response, cancellationToken);
    }

    public async Task<GitHubIssue> GetIssueAsync(string token, string owner, string repository, int number,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, Repo(owner, repository) + $"/issues/{number}", token, null, cancellationToken);
        return await ReadIssueAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(string token, string owner, string repository,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, Repo(owner, repository) + "/issues?state=all&per_page=100", token, null, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<IssuePayload[]>(Json, cancellationToken) ?? [];
        return payload.Where(item => item.PullRequest is null).Select(ToIssue).ToArray();
    }

    public async Task<GitHubIssue> SetIssueStateAsync(string token, string owner, string repository, int number,
        string state, string? stateReason, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?> { ["state"] = state, ["state_reason"] = stateReason };
        using var response = await SendAsync(HttpMethod.Patch, Repo(owner, repository) + $"/issues/{number}", token,
            JsonContent.Create(payload), cancellationToken);
        return await ReadIssueAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, cancellationToken);
        try { await EnsureSuccessAsync(response, cancellationToken); }
        catch { response.Dispose(); throw; }
        return response;
    }

    private static async Task<GitHubIssue> ReadIssueAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadFromJsonAsync<IssuePayload>(Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub Issue 响应无效。");
        return ToIssue(payload);
    }

    private static GitHubIssue ToIssue(IssuePayload item) => new(item.Number, item.Title, item.Body ?? "", item.State,
        item.StateReason, item.HtmlUrl, item.User.Login, item.CreatedAt, item.UpdatedAt, item.Labels.Select(label => label.Name).ToArray());

    private static GitHubSession ToSession(TokenPayload payload)
    {
        if (string.IsNullOrEmpty(payload.AccessToken)) throw new InvalidDataException("GitHub 没有返回访问令牌。");
        var now = DateTimeOffset.UtcNow;
        return new GitHubSession(payload.AccessToken, payload.ExpiresIn is > 0 ? now.AddSeconds(payload.ExpiresIn.Value) : null,
            payload.RefreshToken, payload.RefreshTokenExpiresIn is > 0 ? now.AddSeconds(payload.RefreshTokenExpiresIn.Value) : null);
    }

    private static string Repo(string owner, string repository) =>
        $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";

    private static void RequireClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("尚未配置 GitHub App Client ID。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"GitHub 返回 {(int)response.StatusCode}：{content}");
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }

    private sealed record DeviceCodePayload(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record TokenPayload(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn,
        string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record UserPayload(string Login, long Id, [property: JsonPropertyName("avatar_url")] string AvatarUrl);
    private sealed record OwnerPayload(string Login);
    private sealed record RepositoryPayload(string Name, [property: JsonPropertyName("full_name")] string FullName,
        [property: JsonPropertyName("html_url")] string HtmlUrl, [property: JsonPropertyName("has_issues")] bool HasIssues, OwnerPayload Owner);
    private sealed record LabelPayload(string Name);
    private sealed record IssueUserPayload(string Login);
    private sealed record IssuePayload(int Number, string Title, string? Body, string State,
        [property: JsonPropertyName("state_reason")] string? StateReason,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        IssueUserPayload User,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        LabelPayload[] Labels,
        [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);
}
