using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

public sealed record AppUpdate(Version Version, long Size, string Sha256, Uri DownloadUrl, Uri ReleaseUrl,
    Uri? DeltaManifestUrl = null, long DeltaManifestSize = 0, string? DeltaManifestSha256 = null);

public sealed partial class UpdateClient : IDisposable
{
    public static readonly Uri LatestReleaseUrl = new("https://api.github.com/repos/FennecMomo/ResourceManager/releases/latest");
    public const long MaxPackageBytes = 1024L * 1024 * 1024;
    private const int MaxMetadataBytes = 256 * 1024;
    private const int MaxDeltaManifestBytes = 16 * 1024;
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
        var manifest = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == "ResourceManager-delta.json");
        Uri? manifestUrl = null;
        long manifestSize = 0;
        string? manifestHash = null;
        if (manifest.ValueKind != JsonValueKind.Undefined)
        {
            manifestSize = manifest.TryGetProperty("size", out var manifestSizeValue) &&
                manifestSizeValue.ValueKind == JsonValueKind.Number &&
                manifestSizeValue.TryGetInt64(out var parsedManifestSize) ? parsedManifestSize : 0;
            manifestHash = manifest.TryGetProperty("digest", out var manifestDigestValue) &&
                manifestDigestValue.ValueKind == JsonValueKind.String
                ? manifestDigestValue.GetString() : null;
            var expectedManifestUrl = new Uri($"https://github.com/FennecMomo/ResourceManager/releases/download/{tag}/ResourceManager-delta.json");
            if (manifestSize is > 0 and <= MaxDeltaManifestBytes &&
                manifestHash is not null && Regex.IsMatch(manifestHash, "^sha256:[0-9a-fA-F]{64}$") &&
                manifest.TryGetProperty("browser_download_url", out var manifestUrlValue) &&
                manifestUrlValue.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(manifestUrlValue.GetString(), UriKind.Absolute, out var actualManifestUrl) &&
                actualManifestUrl == expectedManifestUrl)
            {
                manifestUrl = expectedManifestUrl;
                manifestHash = manifestHash[7..].ToLowerInvariant();
            }
            else { manifestSize = 0; manifestHash = null; }
        }
        return new AppUpdate(version, size, digest[7..].ToLowerInvariant(), expectedUrl,
            new Uri($"https://github.com/FennecMomo/ResourceManager/releases/tag/{tag}"),
            manifestUrl, manifestSize, manifestHash);
    }

    public async Task<string> DownloadAsync(AppUpdate update, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        return await DownloadPackageAsync(update.Version, update.Size, update.Sha256,
            async (offset, token) =>
            {
                using var request = CreateRequest(update.DownloadUrl);
                if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            }, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadAsync(SharedUpdatePackage update, PeerInfo peer, IResourceClient peerClient,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.ResourceId) || update.ResourceId.Length > 100)
            throw new InvalidDataException("本地更新源的资源编号无效。");
        var version = ParseVersion(update.Version);
        return await DownloadPackageAsync(version, update.Size, update.Sha256,
            (offset, token) => peerClient.OpenFileAsync(peer, update.ResourceId, "", offset, null, token),
            progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadPackageAsync(Version version, long size, string sha256,
        Func<long, CancellationToken, Task<HttpResponseMessage>> openResponse,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ValidateBlobMetadata(size, sha256);
        var destination = Path.Combine(updateDirectory, $"ResourceManager-{version}-{sha256[..12]}.exe");
        return await DownloadVerifiedAsync(destination, size, sha256, openResponse, progress,
            requireExecutable: true, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateBlobMetadata(long size, string sha256)
    {
        if (size is <= 0 or > MaxPackageBytes) throw new InvalidDataException("更新包大小无效。");
        if (!Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("更新包校验值无效。");
    }

    private async Task<string> DownloadVerifiedAsync(string destination, long size, string sha256,
        Func<long, CancellationToken, Task<HttpResponseMessage>> openResponse,
        IProgress<long>? progress, bool requireExecutable, CancellationToken token)
    {
        ValidateBlobMetadata(size, sha256);
        sha256 = sha256.ToLowerInvariant();
        Directory.CreateDirectory(updateDirectory);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length == size && await HashFileAsync(destination, token) == sha256)
                return destination;
            File.Delete(destination);
        }
        var temporary = destination + ".download";
        var offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
        if (offset > size) { File.Delete(temporary); offset = 0; }
        if (offset == size)
        {
            if (await HashFileAsync(temporary, token) == sha256)
            {
                try { VerifyExecutableHeader(temporary, requireExecutable); }
                catch (InvalidDataException) { File.Delete(temporary); throw; }
                File.Move(temporary, destination, true);
                return destination;
            }
            File.Delete(temporary);
            offset = 0;
        }
        StorageLocation.EnsureSpace(updateDirectory, size - offset);
        using var response = await openResponse(offset, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK) offset = 0;
        else if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent ||
                 response.Content.Headers.ContentRange?.From != offset ||
                 response.Content.Headers.ContentRange?.Length != size))
        {
            File.Delete(temporary);
            throw new InvalidDataException("更新源返回的续传范围无效。");
        }
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != size - offset)
            throw new InvalidDataException("更新包大小与发布资料不符。");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using (var target = new FileStream(temporary, offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var chunk = new byte[1024 * 1024];
            var written = offset;
            progress?.Report(written);
            int read;
            while ((read = await source.ReadAsync(chunk, token)) != 0)
            {
                written += read;
                if (written > size) throw new InvalidDataException("更新包超过预期大小。");
                await target.WriteAsync(chunk.AsMemory(0, read), token);
                progress?.Report(written);
            }
            await target.FlushAsync(token);
            if (written != size) throw new InvalidDataException("更新包下载不完整，可在下次继续。");
        }
        if (await HashFileAsync(temporary, token) != sha256)
        {
            File.Delete(temporary);
            throw new InvalidDataException("更新包校验失败，未安装更新。");
        }
        try { VerifyExecutableHeader(temporary, requireExecutable); }
        catch (InvalidDataException) { File.Delete(temporary); throw; }
        File.Move(temporary, destination, true);
        return destination;
    }

    private static void VerifyExecutableHeader(string path, bool required)
    {
        if (!required) return;
        using var check = File.OpenRead(path);
        if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
            throw new InvalidDataException("下载内容不是 Windows 程序。");
    }

    public void CleanupOldDownloads()
    {
        if (!Directory.Exists(updateDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(updateDirectory, "ResourceManager-*.exe"))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var path in Directory.EnumerateFiles(updateDirectory, "ResourceManager-*.xdelta"))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var path in Directory.EnumerateFiles(updateDirectory, "*.reconstructed"))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var path in Directory.EnumerateFiles(updateDirectory, "*.download"))
            try { if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)) File.Delete(path); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ResourceManager/1.0");
        if (uri == LatestReleaseUrl)
        {
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }
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
