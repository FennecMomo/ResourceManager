using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed class FeedbackServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;

    public FeedbackServerClient(TimeSpan? timeout = null)
    {
        http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    public async Task<FeedbackServerCapabilities> GetCapabilitiesAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<FeedbackServerCapabilities>(new Uri(new Uri(Normalize(baseUrl)), "api/v1/capabilities"), Json, cancellationToken)
        ?? throw new InvalidDataException("服务端没有返回能力信息。");

    public async Task<FeedbackReceipt> SubmitAsync(FeedbackServerBinding binding, FeedbackSubmission submission,
        IReadOnlyList<FeedbackAttachmentDraft> attachments, CancellationToken cancellationToken = default)
    {
        FeedbackRules.ValidateSubmission(submission);
        FeedbackRules.ValidateAttachments(attachments.Select(item => (item.FileName, item.Size)));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(submission, Json), Encoding.UTF8, "application/json"), "metadata");
        foreach (var item in attachments)
        {
            var stream = File.OpenRead(item.StagedPath);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "attachments", item.FileName);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(Normalize(binding.BaseUrl)), "api/v1/issues")) { Content = form };
        request.Headers.TryAddWithoutValidation(FeedbackRules.ClientIdHeader, binding.ClientId);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", submission.ClientSubmissionId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FeedbackReceipt>(Json, cancellationToken)
            ?? throw new InvalidDataException("服务端反馈回执无效。");
    }

    public async Task<FeedbackStatusSnapshot> GetStatusAsync(FeedbackServerBinding binding, string submissionId,
        CancellationToken cancellationToken = default)
    {
        using var request = ForClient(binding, HttpMethod.Get, $"api/v1/issues/{Uri.EscapeDataString(submissionId)}");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FeedbackStatusSnapshot>(Json, cancellationToken)
            ?? throw new InvalidDataException("服务端状态响应无效。");
    }

    public async Task<FeedbackStatusSnapshot> CloseAsync(FeedbackServerBinding binding, string submissionId,
        CancellationToken cancellationToken = default)
    {
        using var request = ForClient(binding, HttpMethod.Post, $"api/v1/issues/{Uri.EscapeDataString(submissionId)}/close");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FeedbackStatusSnapshot>(Json, cancellationToken)
            ?? throw new InvalidDataException("服务端关闭反馈响应无效。");
    }

    private static HttpRequestMessage ForClient(FeedbackServerBinding binding, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(Normalize(binding.BaseUrl)), path));
        request.Headers.TryAddWithoutValidation(FeedbackRules.ClientIdHeader, binding.ClientId);
        return request;
    }

    private static string Normalize(string value) => value.EndsWith('/') ? value : value + "/";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"服务端返回 {(int)response.StatusCode}。" : message.Trim('"', ' ', '\r', '\n'));
    }

    public void Dispose() => http.Dispose();
}

public static class FeedbackServerDiscovery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<FeedbackServerAdvertisement>> DiscoverAsync(
        int port = FeedbackRules.DefaultDiscoveryPort, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var query = JsonSerializer.SerializeToUtf8Bytes(new { protocol = FeedbackRules.DiscoveryProtocol, type = "query" }, Json);
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, port), cancellationToken);
        var deadline = DateTime.UtcNow + (duration ?? TimeSpan.FromSeconds(1.5));
        var found = new Dictionary<string, FeedbackServerAdvertisement>();
        while (DateTime.UtcNow < deadline)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(deadline - DateTime.UtcNow);
            try
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                var item = JsonSerializer.Deserialize<FeedbackServerAdvertisement>(packet.Buffer, Json);
                if (item is null || item.Protocol != FeedbackRules.DiscoveryProtocol || item.Type != "response" || item.ApiPort is < 1 or > 65535) continue;
                found[item.ServerId] = item with { Address = packet.RemoteEndPoint.Address.ToString() };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            catch (JsonException) { }
        }
        return found.Values.OrderBy(item => item.Name).ToArray();
    }
}
