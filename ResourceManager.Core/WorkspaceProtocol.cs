using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed record WorkspaceCapabilities(string Protocol, string ServerId, string Name, string Version, string PublicKey, string[]? Features = null);
public sealed record WorkspaceProfile(string DeviceId, string Nickname, byte[]? Avatar, string Version);
public sealed record WorkspaceRegistration(WorkspaceProfile Profile, string PublicKey, long Timestamp, string Nonce, string Signature);
public sealed record WorkspaceSession(string ServerId, string Token, DateTimeOffset ExpiresUtc, string Nonce, string Signature);
public sealed record WorkspaceMember(WorkspaceProfile Profile, bool Online, DateTimeOffset LastSeenUtc, string? PublicKey = null);
public sealed record WorkspaceSnapshot(string Cursor, IReadOnlyList<WorkspaceMember> Members);
public sealed record ServerBinding(string Id, string Name, string Address, string ServerId, string PublicKey, string Status, string? Error, WorkspaceSnapshot? Cached);

public static class WorkspaceProtocol
{
    public const string Protocol = "workspace-v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string NormalizeAddress(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("请输入服务器的 HTTPS 根地址，例如 https://files.example.com；本机测试可用 http://127.0.0.1:端口。");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static byte[] RegistrationBytes(string serverId, WorkspaceRegistration request) =>
        PeerProof.Payload(Protocol, serverId, JsonSerializer.Serialize(request.Profile, Json), request.PublicKey, request.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Nonce);
    public static byte[] SessionBytes(WorkspaceSession session) => PeerProof.Payload(Protocol, session.ServerId, session.Token, session.ExpiresUtc.ToString("O"), session.Nonce);
    public static byte[] CreateKey() { using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); return key.ExportPkcs8PrivateKey(); }
    public static string PublicKey(byte[] privateKey) { using var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(privateKey, out _); return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()); }
    public static string Sign(byte[] privateKey, byte[] bytes) { using var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(privateKey, out _); return Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256)); }
    public static bool Verify(string publicKey, string signature, byte[] bytes) => PeerProof.Verify(publicKey, signature, bytes);
}

public sealed class WorkspaceException(string message, HttpStatusCode code) : Exception(message)
{
    public HttpStatusCode Code { get; } = code;
    public bool StopRetry => Code is HttpStatusCode.Forbidden or HttpStatusCode.Conflict;
}

public sealed partial class WorkspaceClient(NodeStore store, string version) : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
    private readonly HttpClient resourceHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(6), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private static Uri Route(string address, string route) => new(WorkspaceProtocol.NormalizeAddress(address) + "/api/v1/workspace/" + route);

    public async Task<WorkspaceCapabilities> InspectAsync(string address, CancellationToken token = default)
    {
        using var response = await http.GetAsync(Route(address, "capabilities"), token).ConfigureAwait(false);
        await Check(response, token).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<WorkspaceCapabilities>(WorkspaceProtocol.Json, token).ConfigureAwait(false);
        if (result is null || result.Protocol != WorkspaceProtocol.Protocol || !Guid.TryParse(result.ServerId, out _) ||
            string.IsNullOrWhiteSpace(result.Name) || result.PublicKey is null || result.PublicKey.Length > 256)
            throw new InvalidDataException("目标不是兼容的 ResourceManager 服务器，请核对地址或升级服务端。");
        return result;
    }

    public async Task<WorkspaceSession> JoinAsync(ServerBinding binding, CancellationToken token = default)
    {
        var info = await InspectAsync(binding.Address, token).ConfigureAwait(false);
        if (info.ServerId != binding.ServerId || info.PublicKey != binding.PublicKey) throw new WorkspaceException("服务器身份已变化，请核对地址；不会自动替换已信任服务器。", HttpStatusCode.Conflict);
        var profile = store.GetSettings().Profile;
        var key = store.GetDeviceSigningKey();
        var request = new WorkspaceRegistration(new(profile.DeviceId, profile.Nickname, profile.Avatar, version), WorkspaceProtocol.PublicKey(key),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), "");
        request = request with { Signature = WorkspaceProtocol.Sign(key, WorkspaceProtocol.RegistrationBytes(info.ServerId, request)) };
        using var response = await http.PostAsJsonAsync(Route(binding.Address, "join"), request, WorkspaceProtocol.Json, token).ConfigureAwait(false);
        await Check(response, token).ConfigureAwait(false);
        var session = await response.Content.ReadFromJsonAsync<WorkspaceSession>(WorkspaceProtocol.Json, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器未返回连接凭据。");
        if (session.ServerId != info.ServerId || session.Nonce != request.Nonce || session.Token?.Length != 64 || session.Signature is null || session.ExpiresUtc <= DateTimeOffset.UtcNow ||
            !WorkspaceProtocol.Verify(info.PublicKey, session.Signature, WorkspaceProtocol.SessionBytes(session))) throw new InvalidDataException("服务器身份确认无效。");
        return session;
    }

    public async Task<WorkspaceSnapshot> WatchAsync(ServerBinding binding, WorkspaceSession session, string cursor, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(binding.Address, "members?cursor=" + Uri.EscapeDataString(cursor)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("X-RM-Device", store.GetSettings().Profile.DeviceId);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false); await Check(response, token).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<WorkspaceSnapshot>(WorkspaceProtocol.Json, token).ConfigureAwait(false) ?? throw new InvalidDataException("在线名单为空。");
    }

    public async Task LeaveAsync(ServerBinding binding, WorkspaceSession session, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(binding.Address, "leave")); request.Headers.Authorization = new("Bearer", session.Token); request.Headers.Add("X-RM-Device", store.GetSettings().Profile.DeviceId);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
    }
    private static async Task Check(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "连接凭据已过期，正在重新登记。",
            HttpStatusCode.Forbidden => "服务器已撤销此设备的加入权限。",
            HttpStatusCode.Conflict => "此设备连接已被替换，或设备身份密钥不匹配；请核对其他运行实例。",
            HttpStatusCode.TooManyRequests => "服务器暂时繁忙或登记过于频繁，稍后重试。",
            _ => $"服务器返回 HTTP {(int)response.StatusCode}，请核对地址和服务端协议。"
        };
        await Task.CompletedTask;
        throw new WorkspaceException(message, response.StatusCode);
    }
    public void Dispose() { http.Dispose(); resourceHttp.Dispose(); }
}
