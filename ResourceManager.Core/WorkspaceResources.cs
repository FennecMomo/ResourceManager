using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed record WorkspacePublishedResource(RemoteResource Resource, GroupAccess Access, IReadOnlyList<string> Allowed, SharedUpdatePackage? Update);
public sealed record WorkspacePublishedCatalog(IReadOnlyList<ResourceGroup> Groups, IReadOnlyList<WorkspacePublishedResource> Resources, IReadOnlyList<GroupPermission>? GroupPermissions = null);
public sealed record WorkspaceOwnerCatalog(string Owner, bool Online, ResourceTreeCatalog Catalog, IReadOnlyList<SharedUpdatePackage> Updates);
public sealed record WorkspaceResourceRequest(string ServerId, string Owner, string Requester, string ResourceId, string Operation, string Path, long Offset, string? Etag, long Timestamp, string Nonce, string Signature);
public sealed record WorkspaceRelayTicket(string Id, WorkspaceResourceRequest Request, string PublicKey);
public sealed record WorkspaceResourceReply(int Status, byte[] Data);
public sealed record WorkspaceFileMetadata(long Size, string Etag, string Sha256);

public static class WorkspaceResourceRules
{
    public const int ChunkSize = 256 * 1024;
    public static byte[] SigningBytes(WorkspaceResourceRequest request) => JsonSerializer.SerializeToUtf8Bytes(request with { Signature = "" }, WorkspaceProtocol.Json);
    public static bool Allows(WorkspacePublishedResource resource, string viewer, string owner) => viewer == owner || resource.Access == GroupAccess.Public || resource.Access == GroupAccess.AllowList && resource.Allowed.Contains(viewer, StringComparer.Ordinal);
    public static bool Valid(WorkspaceResourceRequest request, string key) => request.Operation is "describe" or "tree" or "meta" or "chunk" &&
        request.ResourceId.Length is > 0 and <= 100 && request.Path.Length <= 4096 && request.Offset >= 0 && request.Nonce.Length == 32 &&
        Math.Abs((double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - request.Timestamp) <= 120 &&
        WorkspaceProtocol.Verify(key, request.Signature, SigningBytes(request));
    public static WorkspaceResourceReply Json<T>(T value) => new(200, JsonSerializer.SerializeToUtf8Bytes(value, WorkspaceProtocol.Json));
    public static T Read<T>(WorkspaceResourceReply reply)
    {
        Ensure(reply);
        return JsonSerializer.Deserialize<T>(reply.Data, WorkspaceProtocol.Json) ?? throw new InvalidDataException("服务器资源响应为空。");
    }
    public static void Ensure(WorkspaceResourceReply reply)
    {
        if (reply.Status is >= 200 and < 300) return;
        throw new HttpRequestException(reply.Status switch { 403 => "无权访问此资源。", 404 => "资源已撤销或不再发布到此服务器。", 409 => "文件已变化，请重新下载。", 503 => "发布者离线或中继未连接。", 429 => "传输繁忙，请稍后重试。", _ => $"资源请求失败（{reply.Status}）。" }, null, (HttpStatusCode)reply.Status);
    }
}

public sealed partial class WorkspaceClient
{
    private HttpRequestMessage Authorized(ServerBinding binding, WorkspaceSession session, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Route(binding.Address, path));
        request.Headers.Authorization = new("Bearer", session.Token); request.Headers.Add("X-RM-Device", store.GetSettings().Profile.DeviceId); return request;
    }
    private async Task<T> SendJsonAsync<T>(ServerBinding binding, WorkspaceSession session, HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = Authorized(binding, session, method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: WorkspaceProtocol.Json);
        using var response = await (path == "relay" ? resourceHttp : http).SendAsync(request, token).ConfigureAwait(false); await Check(response, token).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(WorkspaceProtocol.Json, token).ConfigureAwait(false) ?? throw new InvalidDataException("资源服务响应为空。");
    }
    public Task<bool> PublishAsync(ServerBinding binding, WorkspaceSession session, WorkspacePublishedCatalog catalog, CancellationToken token) => SendJsonAsync<bool>(binding, session, HttpMethod.Put, "catalog", catalog, token);
    public Task<WorkspaceOwnerCatalog[]> CatalogsAsync(ServerBinding binding, WorkspaceSession session, CancellationToken token) => SendJsonAsync<WorkspaceOwnerCatalog[]>(binding, session, HttpMethod.Get, "catalogs", null, token);
    public Task<WorkspaceRelayTicket[]> PollRelayAsync(ServerBinding binding, WorkspaceSession session, CancellationToken token) => SendJsonAsync<WorkspaceRelayTicket[]>(binding, session, HttpMethod.Get, "relay/poll", null, token);
    public Task<bool> ReplyRelayAsync(ServerBinding binding, WorkspaceSession session, string id, WorkspaceResourceReply reply, CancellationToken token) => SendJsonAsync<bool>(binding, session, HttpMethod.Post, "relay/" + Uri.EscapeDataString(id), reply, token);
    public WorkspaceResourceRequest SignResourceRequest(ServerBinding binding, string owner, string resource, string operation, string path = "", long offset = 0, string? etag = null)
    {
        var request = new WorkspaceResourceRequest(binding.ServerId, owner, store.GetSettings().Profile.DeviceId, resource, operation, path, offset, etag, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), "");
        return request with { Signature = WorkspaceProtocol.Sign(store.GetDeviceSigningKey(), WorkspaceResourceRules.SigningBytes(request)) };
    }
    public async Task<WorkspaceResourceReply> RelayAsync(ServerBinding binding, WorkspaceSession session, WorkspaceResourceRequest request, CancellationToken token)
    {
        try { return await SendJsonAsync<WorkspaceResourceReply>(binding, session, HttpMethod.Post, "relay", request, token).ConfigureAwait(false); }
        catch (WorkspaceException ex) when (ex.Code == HttpStatusCode.ServiceUnavailable)
        { throw new WorkspaceException("发布者离线或中继连接暂时不可用，请在对方上线后继续。", ex.Code); }
        catch (WorkspaceException ex) when (ex.Code == HttpStatusCode.Forbidden)
        { throw new WorkspaceException("无权访问此服务器资源，或设备权限已被撤销。", ex.Code); }
    }
}
