using System.Net.Http.Json;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed partial class PeerClient
{
    private async Task CheckAiPeerAsync(PeerInfo peer, CancellationToken token)
    {
        var hello = await ProbeAsync(peer, token);
        if (hello.DeviceId != peer.DeviceId || !(hello.Capabilities ?? []).Contains(AiRequestService.Capability))
            throw new InvalidOperationException("目标设备身份不匹配或版本不支持 AI 请求，请升级双方 Windows 客户端。");
    }
    public async Task<AiSearchView> CreateAiRequestAsync(PeerInfo peer, AiSearchRequest request, CancellationToken token = default)
    {
        await CheckAiPeerAsync(peer, token);
        using var response = await http.PostAsJsonAsync(Route(peer, "ai/requests"), request, Json, token);
        return await ReadAiResponse(response, token);
    }
    public async Task<AiSearchView> GetAiRequestAsync(PeerInfo peer, string id, CancellationToken token = default)
    {
        await CheckAiPeerAsync(peer, token);
        using var response = await http.GetAsync(Route(peer, "ai/requests/" + Uri.EscapeDataString(id)), token);
        return await ReadAiResponse(response, token);
    }
    public async Task<AiSearchView> ActAiRequestAsync(PeerInfo peer, string id, AiSearchAction action, CancellationToken token = default)
    {
        await CheckAiPeerAsync(peer, token);
        using var response = await http.PostAsJsonAsync(Route(peer, "ai/requests/" + Uri.EscapeDataString(id) + "/actions"), action, Json, token);
        return await ReadAiResponse(response, token);
    }
    private static async Task<AiSearchView> ReadAiResponse(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = $"远端 AI 请求失败（{(int)response.StatusCode}）；请检查设备连接、请求状态及授权。";
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, token);
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var value) && value.GetString() is { } message)
                    error = message[..Math.Min(message.Length, 400)];
            }
            catch (JsonException) { }
            throw new HttpRequestException(error, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<AiSearchView>(Json, token) ?? throw new IOException("对方未返回请求状态。");
    }
}
