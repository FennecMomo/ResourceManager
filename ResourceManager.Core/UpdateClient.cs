using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

public sealed record AppUpdate(Version Version, long Size, string Sha256, Uri DownloadUrl, Uri ReleaseUrl);

public sealed class UpdateClient : IDisposable
{
    public static readonly Uri LatestReleaseUrl = new("https://api.github.com/repos/FennecMomo/ResourceManager/releases/latest");
    public const long MaxPackageBytes = 1024L * 1024 * 1024;
    private const int MaxMetadataBytes = 256 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string updateDirectory;

    public UpdateClient(string dataDirectory, HttpClient? httpClient = null)
    {
        updateDirectory = Path.Combine(dataDirectory, "updates");
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+$") || !Version.TryParse(value, out var version))
            throw new InvalidDataException("版本号格式无效，应为 系统.模块.修改。");
        return version;
    }

    public async Task<AppUpdate?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = CreateRequest(LatestReleaseUrl);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxMetadataBytes)
            throw new InvalidDataException("GitHub 发布资料过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + read > MaxMetadataBytes) throw new InvalidDataException("GitHub 发布资料过大。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
        }
        try { return ParseRelease(buffer.ToArray()); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("GitHub 发布资料无效。", error); }
    }

    public static AppUpdate ParseRelease(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new InvalidDataException("发布标签不是版本号。");
        var version = ParseVersion(tag[1..]);
        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == "ResourceManager.exe");
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("此版本缺少 Windows 安装包。");
        var size = asset.GetProperty("size").GetInt64();
        if (size is <= 0 or > MaxPackageBytes) throw new InvalidDataException("更新包大小无效。");
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("更新包缺少 SHA-256 校验值。");
        var expectedUrl = new Uri($"https://github.com/FennecMomo/ResourceManager/releases/download/{tag}/ResourceManager.exe");
        var actualUrl = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute);
        if (actualUrl != expectedUrl) throw new InvalidDataException("更新包下载地址无效。");
        return new AppUpdate(version, size, digest[7..].ToLowerInvariant(), expectedUrl,
            new Uri($"https://github.com/FennecMomo/ResourceManager/releases/tag/{tag}"));
    }

    public async Task<string> DownloadAsync(AppUpdate update, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        return await DownloadPackageAsync(update.Version, update.Size, update.Sha256,
            async token =>
            {
                using var request = CreateRequest(update.DownloadUrl);
                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            }, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadAsync(SharedUpdatePackage update, PeerInfo peer, PeerClient peerClient,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.ResourceId) || update.ResourceId.Length > 100)
            throw new InvalidDataException("本地更新源的资源编号无效。");
        var version = ParseVersion(update.Version);
        return await DownloadPackageAsync(version, update.Size, update.Sha256,
            token => peerClient.OpenFileAsync(peer, update.ResourceId, "", 0, null, token),
            progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadPackageAsync(Version version, long size, string sha256,
        Func<CancellationToken, Task<HttpResponseMessage>> openResponse,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (size is <= 0 or > MaxPackageBytes) throw new InvalidDataException("更新包大小无效。");
        if (!Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("更新包校验值无效。");
        sha256 = sha256.ToLowerInvariant();
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, $"ResourceManager-{version}-{sha256[..12]}.exe");
        if (File.Exists(destination) && new FileInfo(destination).Length == size &&
            await HashFileAsync(destination, cancellationToken) == sha256)
            return destination;

        var temporary = Path.Combine(updateDirectory, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using var response = await openResponse(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != size)
                throw new InvalidDataException("更新包大小与发布资料不符。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = new byte[1024 * 1024];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) != 0)
            {
                written += read;
                if (written > size) throw new InvalidDataException("更新包超过预期大小。");
                hash.AppendData(chunk, 0, read);
                await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                progress?.Report(written);
            }
            await target.FlushAsync(cancellationToken);
            if (written != size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != sha256)
                throw new InvalidDataException("更新包校验失败，未安装更新。");
            await target.DisposeAsync();
            await using (var check = File.OpenRead(temporary))
            {
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                    throw new InvalidDataException("下载内容不是 Windows 程序。");
            }
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void CleanupOldDownloads()
    {
        if (!Directory.Exists(updateDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(updateDirectory, "ResourceManager-*.exe"))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var path in Directory.EnumerateFiles(updateDirectory, "*.download"))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ResourceManager/1.0");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        if (uri == LatestReleaseUrl) request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
