using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace ResourceManager.Core;

public interface IResourceClient
{
    Task<RemoteResource?> GetResourceAsync(PeerInfo peer, string id, CancellationToken token = default);
    Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string id, CancellationToken token = default);
    Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string id, string path, long offset, string? etag, CancellationToken token = default);
    Task<WorkspaceFileMetadata?> GetFileMetadataAsync(PeerInfo peer, string id, string path, CancellationToken token) => Task.FromResult<WorkspaceFileMetadata?>(null);
}

public sealed class WorkspaceResourceClient(NodeStore store, WorkspaceClient client, ServerBinding binding, Func<WorkspaceSession?> session, string owner,
    Func<CancellationToken, Task<IReadOnlyList<DiscoveredPeer>>>? discover = null, bool serverStored = false) : IResourceClient, IDisposable
{
    private readonly HttpClient direct = new(new PeerAuthenticationHandler(store, false, false)) { Timeout = TimeSpan.FromMinutes(6), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private readonly SemaphoreSlim routeGate = new(1, 1);
    private PeerInfo? directPeer;
    private bool selected;
    public string RouteName => serverStored ? "服务器存储" : !selected ? "待检测" : directPeer is null ? "服务器中继" : "直连";
    public async Task SelectRouteAsync(CancellationToken token)
    {
        await routeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (selected) return;
            if (serverStored) { selected = true; return; }
            var member = binding.Cached?.Members.FirstOrDefault(m => m.Profile.DeviceId == owner) ?? throw new IOException("服务器设备资料尚未同步。");
            if (member.PublicKey is null) throw new IOException("请将服务端升级到 0.2.0。");
            store.TrustDeviceKey(owner, member.PublicKey);
            // Manual, gateway and already established direct connections retain explicit direct routing.
            var endpoints = store.GetPeerEndpoints(owner).Where(e => e.Source != "DeviceChanged").ToArray();
            using var peers = new PeerClient(store);
            if (endpoints.Length > 0)
            {
                Exception? error = null;
                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        directPeer = await peers.ConnectAsync(endpoint.Ip, endpoint.Port, owner, token, endpoint.GatewayId, endpoint.Source).ConfigureAwait(false);
                        if (!store.GetPeerCapabilities(owner).Contains("workspace-resources-v1")) throw new IOException("直连发布者需要升级到 0.6.2。");
                        selected = true; return;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { error = ex; }
                }
                throw new IOException("已配置的直连连接不可用；未切换服务器中继。", error);
            }
            await using var discovery = new LanDiscoveryService(store);
            var found = (await (discover is null ? discovery.DiscoverAsync(TimeSpan.FromMilliseconds(700), token) : discover(token)).ConfigureAwait(false)).FirstOrDefault(p => p.DeviceId == owner);
            if (found is not null) directPeer = await peers.ConnectAsync(found.Ip, found.Port, owner, token, source: "WorkspaceLan").ConfigureAwait(false);
            if (directPeer is not null && !store.GetPeerCapabilities(owner).Contains("workspace-resources-v1")) throw new IOException("直连发布者需要升级到 0.6.2；未切换服务器中继。");
            selected = true;
        }
        finally { routeGate.Release(); }
    }
    private async Task<WorkspaceResourceReply> Request(string id, string operation, string path, long offset, string? etag, CancellationToken token)
    {
        await SelectRouteAsync(token).ConfigureAwait(false);
        var request = client.SignResourceRequest(binding, owner, id, operation, path, offset, etag);
        if (directPeer is not null)
        {
            using var response = await direct.PostAsJsonAsync(new Uri($"http://{directPeer.Ip}:{directPeer.Port}/api/v1/workspace-resource"), request, WorkspaceProtocol.Json, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<WorkspaceResourceReply>(WorkspaceProtocol.Json, token).ConfigureAwait(false) ?? throw new InvalidDataException("直连响应为空。");
        }
        if (serverStored) return await client.StoredResourceAsync(binding, session() ?? throw new IOException("服务器尚未连接。"), request, token).ConfigureAwait(false);
        return await client.RelayAsync(binding, session() ?? throw new IOException("服务器尚未连接。"), request, token).ConfigureAwait(false);
    }
    public async Task<RemoteResource?> GetResourceAsync(PeerInfo peer, string id, CancellationToken token = default)
    { var reply = await Request(id, "describe", "", 0, null, token).ConfigureAwait(false); return reply.Status == 404 ? null : WorkspaceResourceRules.Read<RemoteResource>(reply); }
    public async Task<IReadOnlyList<RemoteFile>> GetFilesAsync(PeerInfo peer, string id, CancellationToken token = default) => WorkspaceResourceRules.Read<RemoteFile[]>(await Request(id, "tree", "", 0, null, token).ConfigureAwait(false));
    public async Task<WorkspaceFileMetadata?> GetFileMetadataAsync(PeerInfo peer, string id, string path, CancellationToken token) => WorkspaceResourceRules.Read<WorkspaceFileMetadata>(await Request(id, "meta", path, 0, null, token).ConfigureAwait(false));
    public async Task<HttpResponseMessage> OpenFileAsync(PeerInfo peer, string id, string path, long offset, string? etag, CancellationToken token = default)
    {
        var metadata = (await GetFileMetadataAsync(peer, id, path, token).ConfigureAwait(false))!;
        if (offset > metadata.Size) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
        if (offset > 0 && metadata.Etag != etag) offset = 0;
        var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        response.Headers.ETag = EntityTagHeaderValue.Parse(metadata.Etag);
        response.Content = new StreamContent(new ChunkStream(metadata.Size, offset, async (position, cancellation) =>
        {
            var reply = await Request(id, "chunk", path, position, metadata.Etag, cancellation).ConfigureAwait(false); WorkspaceResourceRules.Ensure(reply); return reply.Data;
        }, token));
        response.Content.Headers.ContentLength = metadata.Size - offset;
        if (offset > 0) response.Content.Headers.ContentRange = new(offset, metadata.Size - 1, metadata.Size);
        return response;
    }
    public void Dispose() { direct.Dispose(); routeGate.Dispose(); }
    private sealed class ChunkStream(long length, long start, Func<long, CancellationToken, Task<byte[]>> fetch, CancellationToken lifetime) : Stream
    {
        private long position = start;
        private byte[] chunk = []; private int used;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0 || position >= length) return 0;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            if (used == chunk.Length) { chunk = await fetch(position, linked.Token).ConfigureAwait(false); used = 0; if (chunk.Length == 0 || chunk.Length > WorkspaceResourceRules.ChunkSize || chunk.Length > length - position) throw new IOException("中继分片长度无效。"); }
            var count = Math.Min(buffer.Length, chunk.Length - used); chunk.AsMemory(used, count).CopyTo(buffer); used += count; position += count; return count;
        }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
