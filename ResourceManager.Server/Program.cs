using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using ResourceManager.Core;
using ResourceManager.Server;

var version = Assembly.GetExecutingAssembly().GetName().Version is { } assemblyVersion
    ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}" : "0.0.1";
var options = ParseOptions(args, version);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.ConfigureKestrel(server =>
{
    server.ListenAnyIP(options.ApiPort);
    server.ListenLocalhost(options.AdminPort);
    server.Limits.MaxRequestBodySize = 55L * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = 55L * 1024 * 1024);
var protection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "secrets")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new ServerStore(options.DataDirectory, options.ServerName));
builder.Services.AddSingleton<SubmissionLimiter>();
builder.Services.AddSingleton<GitHubFeedbackClient>();
builder.Services.AddSingleton<GitHubSessionManager>();
builder.Services.AddHostedService<FeedbackDiscoveryService>();
builder.Services.AddHostedService<GitHubSyncService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin") &&
        (context.Connection.LocalPort != options.AdminPort || context.Connection.RemoteIpAddress is null ||
         !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("管理页只允许从服务端本机访问。");
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/admin", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/admin/");
        return;
    }
    await next();
});

app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/admin" });
app.UseStaticFiles();

app.MapGet("/admin/", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "admin", "index.html"), "text/html; charset=utf-8"));

app.MapGet("/", (HttpContext context) => context.Connection.LocalPort == options.AdminPort
    ? Results.Redirect("/admin/")
    : Results.Json(new { name = "ResourceManager.Server", version = options.Version, protocol = FeedbackRules.Protocol }));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (ServerStore store) => Results.Ok(new { status = "ready", store.ServerId }));
app.MapGet("/api/v1/capabilities", (ServerStore store) => Results.Ok(new FeedbackServerCapabilities(store.ServerId,
    store.ServerName, options.Version, FeedbackRules.Protocol, FeedbackRules.MaxAttachments,
    FeedbackRules.MaxOtherBytes, FeedbackRules.MaxTotalBytes)));

app.MapPost("/api/v1/issues", async (HttpContext context, ServerStore store, SubmissionLimiter limiter, CancellationToken token) =>
{
    var clientId = ClientId(context);
    if (clientId is null) return Results.BadRequest("缺少有效的客户端标识。");
    if (!context.Request.HasFormContentType) return Results.BadRequest("反馈请求必须使用 multipart/form-data。");
    var form = await context.Request.ReadFormAsync(token);
    if (!form.TryGetValue("metadata", out var metadata) || metadata.Count != 1) return Results.BadRequest("缺少反馈资料。");
    FeedbackSubmission? submission;
    try { submission = JsonSerializer.Deserialize<FeedbackSubmission>(metadata[0]!, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    catch (JsonException) { return Results.BadRequest("反馈资料格式无效。"); }
    if (submission is null) return Results.BadRequest("反馈资料格式无效。");
    var idempotency = context.Request.Headers["Idempotency-Key"].ToString();
    if (!string.Equals(idempotency, submission.ClientSubmissionId, StringComparison.Ordinal)) return Results.BadRequest("提交编号不一致。");
    try
    {
        FeedbackRules.ValidateSubmission(submission);
        FeedbackRules.ValidateAttachments(form.Files.Select(file => (Path.GetFileName(file.FileName), file.Length)));
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    var existing = store.GetIssueBySubmission(clientId, submission.ClientSubmissionId);
    if (existing is not null) return Results.Ok(new FeedbackReceipt(existing.SubmissionId, existing.Id, existing.Status, existing.CreatedUtc, existing.UpdatedUtc));
    var limiterKey = $"{context.Connection.RemoteIpAddress}|{clientId}";
    if (!limiter.TryAcquire(limiterKey, DateTimeOffset.UtcNow)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    var client = new FeedbackSubmitter(clientId, submission.UserName);
    var staged = new List<StagedServerAttachment>();
    try
    {
        foreach (var file in form.Files)
        {
            var fileName = Path.GetFileName(file.FileName);
            var id = Guid.NewGuid().ToString("N");
            var temp = Path.Combine(store.TemporaryDirectory, id + ".upload");
            await using (var source = file.OpenReadStream())
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(destination, token);
            await using var verify = File.OpenRead(temp);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, token)).ToLowerInvariant();
            staged.Add(new StagedServerAttachment(id, fileName, temp, file.Length, hash, FeedbackRules.IsImage(fileName)));
        }
        return Results.Ok(store.SaveIssue(client, submission, staged));
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        foreach (var file in staged) try { File.Delete(file.TempPath); } catch { }
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/v1/issues/{submissionId}", (string submissionId, HttpContext context, ServerStore store) =>
{
    var clientId = ClientId(context);
    if (clientId is null) return Results.BadRequest("缺少有效的客户端标识。");
    var issue = store.GetIssueBySubmission(clientId, submissionId);
    return issue is null ? Results.NotFound() : Results.Ok(new FeedbackStatusSnapshot(issue.SubmissionId, issue.Id, issue.Status, issue.UpdatedUtc));
});

app.MapPost("/api/v1/issues/{submissionId}/close", (string submissionId, HttpContext context, ServerStore store) =>
{
    var clientId = ClientId(context);
    if (clientId is null) return Results.BadRequest("缺少有效的客户端标识。");
    var client = new FeedbackSubmitter(clientId, "局域网用户");
    return Try(() => Results.Ok(store.CloseBySubmitter(client, submissionId)));
});

app.MapGet("/admin/api/state", (ServerStore store, GitHubSessionManager github) => Results.Ok(new
{
    server = new { store.ServerId, store.ServerName, options.Version, options.ApiPort, options.DiscoveryPort },
    github = new { configured = github.IsConfigured, account = github.Account, owner = github.RepositoryOwner,
        repository = github.RepositoryName, lastSync = store.GetSetting("github_last_sync") }
}));

app.MapGet("/admin/api/issues", (ServerStore store) => Results.Ok(new
{
    local = store.GetIssues().Select(issue => new
    {
        source = "local", issue.Id, issue.SubmissionId, issue.Nickname, category = issue.Category.ToString(),
        issue.Title, issue.Body, issue.UserName, issue.AppVersion, issue.OsDescription, issue.OsArchitecture,
        issue.ProcessArchitecture, issue.RejectionReason, status = issue.Status.ToString(), issue.CreatedUtc, issue.UpdatedUtc,
        attachments = issue.Attachments.Select(file => new { file.Id, file.FileName, file.Size, file.IsImage })
    }),
    github = store.GetGitHubIssues().Select(issue => new
    {
        source = "github", id = issue.Number.ToString(), number = issue.Number, issue.Title, issue.Body,
        issue.Author, category = issue.Category?.ToString(), issue.State, issue.StateReason, issue.HtmlUrl,
        issue.CreatedUtc, issue.UpdatedUtc, issue.Labels
    })
}));

app.MapPatch("/admin/api/issues/{id}/status", async (string id, HttpRequest request, ServerStore store) =>
{
    var payload = await request.ReadFromJsonAsync<StatusChange>();
    return payload is null || !Enum.TryParse<LocalIssueStatus>(payload.Status, true, out var status) || !Enum.IsDefined(status)
        ? Results.BadRequest("反馈状态无效。") : Try(() => Results.Ok(store.SetStatus(id, status, payload.Reason)));
});

app.MapGet("/admin/api/attachments/{id}", (string id, ServerStore store) =>
{
    var file = store.GetAttachment(id);
    if (file is null || !File.Exists(file.StoredPath)) return Results.NotFound();
    var contentType = file.IsImage ? ImageContentType(file.FileName) : "application/octet-stream";
    return Results.File(file.StoredPath, contentType, file.FileName, enableRangeProcessing: true);
});

app.MapPost("/admin/api/github/device/start", async (GitHubSessionManager github, CancellationToken token) =>
{
    if (!github.IsConfigured) return Results.BadRequest("尚未配置 GitHub App Client ID。");
    return await TryAsync(async () =>
    {
        var result = await github.StartAsync(token);
        return Results.Ok(new { flowId = result.FlowId, userCode = result.Code.UserCode,
            verificationUri = result.Code.VerificationUri, interval = result.Code.Interval, expiresIn = result.Code.ExpiresIn });
    });
});

app.MapPost("/admin/api/github/device/poll/{flowId}", async (string flowId, GitHubSessionManager github, CancellationToken token) =>
    await TryAsync(async () =>
    {
        var result = await github.PollAsync(flowId, token);
        return Results.Ok(new { complete = result.Complete, account = result.Account });
    }));

app.MapPost("/admin/api/github/bind", async (GitHubBinding payload, GitHubSessionManager github, CancellationToken token) =>
    await TryAsync(async () => Results.Ok(await github.BindRepositoryAsync(payload.Owner, payload.Repository, token))));

app.MapPost("/admin/api/github/sync", async (GitHubSessionManager github, CancellationToken token) =>
    await TryAsync(async () => { await github.SyncAsync(token); return Results.Ok(); }));

app.MapPost("/admin/api/github/logout", (GitHubSessionManager github) => { github.Logout(); return Results.Ok(); });

app.MapPatch("/admin/api/github/issues/{number:int}/state", async (int number, GitHubStateChange payload,
    GitHubSessionManager github, CancellationToken token) =>
    await TryAsync(async () => Results.Ok(await github.SetStateAsync(number, payload.State, payload.StateReason, token))));

app.Run();

static string? ClientId(HttpContext context)
{
    var value = context.Request.Headers[FeedbackRules.ClientIdHeader].ToString();
    return Guid.TryParse(value, out var id) ? id.ToString("N") : null;
}

static IResult Try(Func<IResult> action)
{
    try { return action(); }
    catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(ex.Message); }
}

static async Task<IResult> TryAsync(Func<Task<IResult>> action)
{
    try { return await action(); }
    catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException) { return Results.BadRequest(ex.Message); }
}

static string ImageContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
{
    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
    ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "application/octet-stream"
};

static ServerRuntimeOptions ParseOptions(string[] values, string version)
{
    string? Value(string name)
    {
        var index = Array.FindIndex(values, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
    }
    int Port(string name, int fallback) => int.TryParse(Value(name), out var port) && port is > 0 and <= 65535 ? port : fallback;
    if (values.Contains("--version", StringComparer.OrdinalIgnoreCase)) { Console.WriteLine(version); Environment.Exit(0); }
    if (values.Contains("--help", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("ResourceManager.Server [--data-dir PATH] [--server-name NAME] [--api-port PORT] [--admin-port PORT] [--discovery-port PORT]");
        Environment.Exit(0);
    }
    var data = Value("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceManager.Server");
    return new ServerRuntimeOptions(data, Value("--server-name") ?? Environment.MachineName,
        Port("--api-port", FeedbackRules.DefaultApiPort), Port("--discovery-port", FeedbackRules.DefaultDiscoveryPort),
        Port("--admin-port", FeedbackRules.DefaultAdminPort), version);
}

sealed record StatusChange(string Status, string? Reason);
sealed record GitHubBinding(string Owner, string Repository);
sealed record GitHubStateChange(string State, string? StateReason);

public partial class Program;
