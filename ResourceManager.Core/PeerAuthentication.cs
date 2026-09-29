using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ResourceManager.Core;

internal sealed record SignedHello(PeerHello Hello, string Nonce, string PublicKey, string Signature);

internal static class PeerProof
{
    internal const string Capability = "signed-device-v1";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static byte[] Payload(params string[] fields) => JsonSerializer.SerializeToUtf8Bytes(fields);
    internal static ECDsa Key(NodeStore store)
    {
        var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(store.GetDeviceSigningKey(), out _); return key;
    }
    internal static string PublicKey(NodeStore store) { using var key = Key(store); return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()); }
    internal static string Sign(NodeStore store, byte[] bytes) { using var key = Key(store); return Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256)); }
    internal static bool Verify(string publicKey, string signature, byte[] bytes)
    {
        try
        {
            if (publicKey.Length > 256 || signature.Length > 256) return false;
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.KeySize == 256 && key.VerifyData(bytes, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    internal static byte[] RequestPayload(string sender, string target, string method, string route, string timestamp, string nonce, byte[] body) =>
        Payload(Capability, sender, target, method, route, timestamp, nonce, Convert.ToHexString(SHA256.HashData(body)));
    internal static byte[] HelloPayload(PeerHello hello, string nonce, string recipient) =>
        Payload("signed-device-hello-v1", recipient, nonce, JsonSerializer.Serialize(hello, Json));

    internal static void SignRequest(NodeStore store, HttpRequestMessage request, string target, byte[] body)
    {
        var sender = store.GetSettings().Profile.DeviceId;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        request.Headers.Add("X-RM-Device", sender); request.Headers.Add("X-RM-Time", timestamp);
        request.Headers.Add("X-RM-Nonce", nonce); request.Headers.Add("X-RM-Key", PublicKey(store));
        request.Headers.Add("X-RM-Signature", Sign(store, RequestPayload(sender, target, request.Method.Method,
            request.RequestUri!.PathAndQuery + "\n" + request.Headers.Range + "\n" + request.Headers.IfRange, timestamp, nonce, body)));
    }
}

internal sealed class PeerAuthenticationServer(NodeStore store)
{
    internal async Task<bool> VerifyAsync(HttpContext context, bool firstContact)
    {
        var headers = context.Request.Headers;
        var device = headers["X-RM-Device"].ToString(); var key = headers["X-RM-Key"].ToString();
        var timestamp = headers["X-RM-Time"].ToString(); var nonce = headers["X-RM-Nonce"].ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (device.Length is < 1 or > 100 || device == store.GetSettings().Profile.DeviceId || nonce.Length != 48 ||
            !long.TryParse(timestamp, out var time) || time < now - 300 || time > now + 300) return false;
        var trusted = store.GetTrustedDeviceKey(device);
        if (trusted is not null && trusted != key || !firstContact && (trusted is null || store.GetPeer(device) is null)) return false;
        // Bound signed bodies before reading; downloaded response streams are unaffected.
        if (context.Request.ContentLength > 2 * 1024 * 1024) return false;
        context.Request.EnableBuffering(32 * 1024, 2 * 1024 * 1024);
        using var buffer = new MemoryStream();
        try { await context.Request.Body.CopyToAsync(buffer, context.RequestAborted); }
        catch (IOException) { return false; }
        context.Request.Body.Position = 0;
        var payload = PeerProof.RequestPayload(device, store.GetSettings().Profile.DeviceId, context.Request.Method,
            context.Request.Path + context.Request.QueryString + "\n" + headers.Range + "\n" + headers.IfRange, timestamp, nonce, buffer.ToArray());
        if (!PeerProof.Verify(key, headers["X-RM-Signature"].ToString(), payload)) return false;
        if (!store.AcceptDeviceNonce(device, nonce, time, now)) return false;
        context.Items["VerifiedDevice"] = device;
        return true;
    }
}

// Every request is signed independently. A shared IP never grants restricted resource access.
internal sealed class PeerAuthenticationHandler(NodeStore store, bool supportsReminders, bool supportsChat) : DelegatingHandler(new HttpClientHandler())
{
    private readonly HttpClient handshake = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, (PeerHello Hello, DateTimeOffset Until)> peers = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> connecting = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var authority = uri.GetLeftPart(UriPartial.Authority);
        var remote = await EnsurePeerAsync(authority, cancellationToken).ConfigureAwait(false);
        if (remote is not null)
        {
            if (uri.AbsolutePath.EndsWith("/hello", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(remote, options: PeerProof.Json), RequestMessage = request };
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            PeerProof.SignRequest(store, request, remote.DeviceId, body);
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (remote is null || response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        peers.TryRemove(authority, out _);
        remote = await EnsurePeerAsync(authority, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("设备身份校验不可用，拒绝降级连接。");
        // Authentication rejects before dispatching the operation, so a single fresh proof retry is safe.
        using var retry = new HttpRequestMessage(request.Method, uri);
        foreach (var header in request.Headers.Where(h => !h.Key.StartsWith("X-RM-", StringComparison.OrdinalIgnoreCase))) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (request.Content is not null)
        {
            retry.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers) retry.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        PeerProof.SignRequest(store, retry, remote.DeviceId, bytes);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PeerHello?> EnsurePeerAsync(string authority, CancellationToken token)
    {
        if (peers.TryGetValue(authority, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Hello;
        var gate = connecting.GetOrAdd(authority, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (peers.TryGetValue(authority, out cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Hello;
            var hello = await handshake.GetFromJsonAsync<PeerHello>(authority + "/api/v1/health", PeerProof.Json, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("设备资料为空。");
            var address = new Uri(authority);
            var known = store.GetPeers().FirstOrDefault(p => p.Ip == address.Host && p.Port == address.Port);
            if (known is not null && known.DeviceId != hello.DeviceId) throw new InvalidOperationException("此端点现在属于另一台设备。");
            if (!(hello.Capabilities ?? []).Contains(PeerProof.Capability))
            {
                if (store.GetTrustedDeviceKey(hello.DeviceId) is not null) throw new InvalidOperationException("设备身份校验不可用，拒绝降级连接。");
                return null;
            }
            var settings = store.GetSettings();
            var capabilities = new List<string> { NodeDefaults.RouterDiscoveryCapability, NodeDefaults.UpnpMappingCapability, PeerProof.Capability };
            if (supportsReminders) capabilities.Add(NodeDefaults.ReminderCapability);
            if (supportsChat) capabilities.AddRange([NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
            var local = new PeerHello(settings.Profile.DeviceId, settings.Profile.Nickname, settings.ListenPort, settings.Profile.Avatar,
                capabilities.ToArray());
            using var request = new HttpRequestMessage(HttpMethod.Post, authority + "/api/v1/auth/hello") { Content = JsonContent.Create(local, options: PeerProof.Json) };
            PeerProof.SignRequest(store, request, hello.DeviceId, await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            var nonce = request.Headers.GetValues("X-RM-Nonce").Single();
            using var response = await handshake.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("设备身份校验失败，请核对设备身份与系统时间。");
            var signed = await response.Content.ReadFromJsonAsync<SignedHello>(PeerProof.Json, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("身份确认响应为空。");
            if (signed.Hello.DeviceId != hello.DeviceId || signed.Nonce != nonce ||
                !PeerProof.Verify(signed.PublicKey, signed.Signature, PeerProof.HelloPayload(signed.Hello, nonce, local.DeviceId)))
                throw new InvalidOperationException("设备身份响应签名无效。");
            store.TrustDeviceKey(signed.Hello.DeviceId, signed.PublicKey);
            peers[authority] = (signed.Hello, DateTimeOffset.UtcNow.AddMinutes(2));
            return signed.Hello;
        }
        finally { gate.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            handshake.Dispose();
            foreach (var gate in connecting.Values) gate.Dispose();
        }
        base.Dispose(disposing);
    }
}
