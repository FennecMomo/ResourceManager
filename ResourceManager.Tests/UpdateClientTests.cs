using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class UpdateClientTests
{
    [Fact]
    public void ParsesReleaseAssetAndVersion()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var json = $$"""
            {
              "tag_name": "v0.2.0",
              "assets": [{
                "name": "ResourceManager.exe",
                "size": 203248367,
                "digest": "sha256:{{digest}}",
                "browser_download_url": "https://github.com/FennecMomo/ResourceManager/releases/download/v0.2.0/ResourceManager.exe"
              }]
            }
            """;

        var update = UpdateClient.ParseRelease(Encoding.UTF8.GetBytes(json));

        Assert.Equal(new Version(0, 2, 0), update.Version);
        Assert.Equal(203248367, update.Size);
        Assert.Equal(digest, update.Sha256);
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("v0.2.0")]
    [InlineData("0.2.0.1")]
    [InlineData("latest")]
    public void RejectsInvalidVersionFormat(string value) =>
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseVersion(value));

    [Fact]
    public void RejectsUnexpectedDownloadAddress()
    {
        const string json = """
            {
              "tag_name": "v0.2.0",
              "assets": [{
                "name": "ResourceManager.exe",
                "size": 12,
                "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "browser_download_url": "https://example.com/ResourceManager.exe"
              }]
            }
            """;

        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void OptionalDeltaManifestCannotBreakFullRelease()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var json = $$"""
            {"tag_name":"v0.6.9","assets":[
              {"name":"ResourceManager.exe","size":1000,"digest":"sha256:{{digest}}","browser_download_url":"https://github.com/FennecMomo/ResourceManager/releases/download/v0.6.9/ResourceManager.exe"},
              {"name":"ResourceManager-delta.json","size":400,"digest":"sha256:{{digest}}","browser_download_url":"https://github.com/FennecMomo/ResourceManager/releases/download/v0.6.9/ResourceManager-delta.json"}
            ]}
            """;
        var parsed = UpdateClient.ParseRelease(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(parsed.DeltaManifestUrl);
        Assert.Equal(digest, parsed.DeltaManifestSha256);
        var invalid = json.Replace($"\"size\":400,\"digest\":\"sha256:{digest}\"",
            "\"size\":400,\"digest\":\"invalid\"");
        Assert.Null(UpdateClient.ParseRelease(Encoding.UTF8.GetBytes(invalid)).DeltaManifestUrl);
    }

    [Fact]
    public async Task DownloadsAndVerifiesExecutablePackage()
    {
        var payload = CreatePayload(8192);
        using var directory = new TempDirectory();
        using var http = new HttpClient(new StubHandler(_ => PayloadResponse(payload)));
        using var client = new UpdateClient(directory.Root, http);

        var path = await client.DownloadAsync(CreateUpdate(payload));

        Assert.StartsWith(directory.Root, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReusesPackageThatAlreadyPassedVerification()
    {
        var payload = CreatePayload(4096);
        using var directory = new TempDirectory();
        var handler = new StubHandler(_ => PayloadResponse(payload));
        using var http = new HttpClient(handler);
        using var client = new UpdateClient(directory.Root, http);
        var update = CreateUpdate(payload);

        var first = await client.DownloadAsync(update);
        var second = await client.DownloadAsync(update);

        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsPackageWhoseSizeDoesNotMatchRelease()
    {
        var payload = CreatePayload(4096);
        using var directory = new TempDirectory();
        using var http = new HttpClient(new StubHandler(_ => PayloadResponse(payload)));
        using var client = new UpdateClient(directory.Root, http);
        var update = CreateUpdate(payload) with { Size = payload.Length + 1 };

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update));
    }

    [Fact]
    public async Task RejectsDownloadedContentWithoutExecutableHeader()
    {
        var payload = CreatePayload(4096);
        payload[0] = (byte)'N';
        using var directory = new TempDirectory();
        using var http = new HttpClient(new StubHandler(_ => PayloadResponse(payload)));
        using var client = new UpdateClient(directory.Root, http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(CreateUpdate(payload)));
        Assert.Empty(Directory.GetFiles(directory.Root, "ResourceManager-*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ResumesFullDownloadFromVerifiedRange()
    {
        var payload = CreatePayload(8192);
        var update = CreateUpdate(payload);
        using var directory = new TempDirectory();
        var updates = Path.Combine(directory.Root, "updates");
        Directory.CreateDirectory(updates);
        var partial = Path.Combine(updates, $"ResourceManager-{update.Version}-{update.Sha256[..12]}.exe.download");
        await File.WriteAllBytesAsync(partial, payload[..1024]);
        var requestedOffset = -1L;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestedOffset = request.Headers.Range?.Ranges.Single().From ?? -1;
            var reply = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(payload[1024..]) };
            reply.Content.Headers.ContentRange = new(1024, payload.Length - 1, payload.Length);
            return reply;
        }));
        using var client = new UpdateClient(directory.Root, http);
        var path = await client.DownloadAsync(update);
        Assert.Equal(1024L, requestedOffset);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task OfficialDeltaReconstructsExecutableAndInvalidPatchFallsBackToFull()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.Root);
        var basePath = Path.Combine(directory.Root, "old.exe");
        var targetPath = Path.Combine(directory.Root, "new.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), basePath);
        await using (var append = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write))
        {
            await append.WriteAsync(await File.ReadAllBytesAsync(basePath));
            await append.WriteAsync(Encoding.UTF8.GetBytes("ResourceManager delta verification fixture"));
        }
        var versionInfo = FileVersionInfo.GetVersionInfo(targetPath);
        var targetVersion = new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart, versionInfo.FileBuildPart);
        var baseVersion = new Version(targetVersion.Major - 1, targetVersion.Minor, targetVersion.Build);
        var targetBytes = await File.ReadAllBytesAsync(targetPath);
        var baseHash = (await HashAsync(basePath)).ToLowerInvariant();
        var targetHash = (await HashAsync(targetPath)).ToLowerInvariant();
        var patchPath = Path.Combine(directory.Root, "test.xdelta");
        await BinaryDelta.CreateAsync(basePath, targetPath, patchPath);
        var patchBytes = await File.ReadAllBytesAsync(patchPath);
        var patchHash = Convert.ToHexString(SHA256.HashData(patchBytes)).ToLowerInvariant();
        var tag = $"v{targetVersion}";
        var patchUrl = $"https://github.com/FennecMomo/ResourceManager/releases/download/{tag}/ResourceManager-v{baseVersion}-to-{tag}.xdelta";
        var manifest = Encoding.UTF8.GetBytes($$"""
            {"schema":1,"algorithm":"xdelta3-3.2.1-lzma","fromVersion":"{{baseVersion}}","toVersion":"{{targetVersion}}","fromSha256":"{{baseHash}}","toSha256":"{{targetHash}}","toSize":{{targetBytes.Length}},"patchSha256":"{{patchHash}}","patchSize":{{patchBytes.Length}},"patchUrl":"{{patchUrl}}"}
            """);
        var manifestUrl = new Uri($"https://github.com/FennecMomo/ResourceManager/releases/download/{tag}/ResourceManager-delta.json");
        var update = new AppUpdate(targetVersion, targetBytes.Length, targetHash,
            new Uri($"https://github.com/FennecMomo/ResourceManager/releases/download/{tag}/ResourceManager.exe"),
            new Uri($"https://github.com/FennecMomo/ResourceManager/releases/tag/{tag}"),
            manifestUrl, manifest.Length, Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant());
        Assert.Equal(baseVersion, UpdateClient.ParseDeltaManifest(manifest, update).FromVersion);

        var fullRequested = false;
        using (var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == manifestUrl) return PayloadResponse(manifest);
            if (request.RequestUri == update.DownloadUrl) { fullRequested = true; return PayloadResponse(targetBytes); }
            return PayloadResponse(patchBytes);
        })))
        using (var client = new UpdateClient(Path.Combine(directory.Root, "success"), http))
        {
            var result = await client.DownloadPreferredAsync(update, basePath, baseVersion);
            Assert.Equal("差分更新", result.Method);
            Assert.False(fullRequested);
            Assert.Equal(targetHash, (await HashAsync(result.Path)).ToLowerInvariant());
        }

        var resumeRoot = Path.Combine(directory.Root, "resume-delta");
        var resumeUpdates = Path.Combine(resumeRoot, "updates");
        Directory.CreateDirectory(resumeUpdates);
        var partialPatch = Path.Combine(resumeUpdates,
            $"ResourceManager-{baseVersion}-to-{targetVersion}-{patchHash[..12]}.xdelta.download");
        await File.WriteAllBytesAsync(partialPatch, patchBytes[..16]);
        var patchOffset = -1L;
        using (var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == manifestUrl) return PayloadResponse(manifest);
            if (request.RequestUri == update.DownloadUrl) throw new Exception("Full package was not expected.");
            patchOffset = request.Headers.Range?.Ranges.Single().From ?? -1;
            var reply = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(patchBytes[16..]) };
            reply.Content.Headers.ContentRange = new(16, patchBytes.Length - 1, patchBytes.Length);
            return reply;
        })))
        using (var client = new UpdateClient(resumeRoot, http))
        {
            var result = await client.DownloadPreferredAsync(update, basePath, baseVersion);
            Assert.Equal("差分更新", result.Method);
            Assert.Equal(16L, patchOffset);
            Assert.Equal(targetHash, (await HashAsync(result.Path)).ToLowerInvariant());
        }

        var corrupt = (byte[])patchBytes.Clone();
        corrupt[^1] ^= 0x5a;
        fullRequested = false;
        using (var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == manifestUrl) return PayloadResponse(manifest);
            if (request.RequestUri == update.DownloadUrl) { fullRequested = true; return PayloadResponse(targetBytes); }
            return PayloadResponse(corrupt);
        })))
        using (var client = new UpdateClient(Path.Combine(directory.Root, "fallback"), http))
        {
            var result = await client.DownloadPreferredAsync(update, basePath, baseVersion);
            Assert.Equal("完整更新", result.Method);
            Assert.True(fullRequested);
            Assert.NotNull(result.FallbackReason);
            Assert.Equal(targetHash, (await HashAsync(result.Path)).ToLowerInvariant());
        }

        await File.AppendAllTextAsync(basePath, "modified local build");
        var patchRequested = false;
        using (var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == manifestUrl) return PayloadResponse(manifest);
            if (request.RequestUri == update.DownloadUrl) return PayloadResponse(targetBytes);
            patchRequested = true;
            return PayloadResponse(patchBytes);
        })))
        using (var client = new UpdateClient(Path.Combine(directory.Root, "wrong-base"), http))
        {
            var result = await client.DownloadPreferredAsync(update, basePath, baseVersion);
            Assert.Equal("完整更新", result.Method);
            Assert.False(patchRequested);
            Assert.Equal(targetHash, (await HashAsync(result.Path)).ToLowerInvariant());
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        Random.Shared.NextBytes(payload);
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        return payload;
    }

    private static HttpResponseMessage PayloadResponse(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload)
    };

    private static AppUpdate CreateUpdate(byte[] payload) => new(
        new Version(0, 3, 2),
        payload.Length,
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        new Uri("https://github.com/FennecMomo/ResourceManager/releases/download/v0.3.2/ResourceManager.exe"),
        new Uri("https://github.com/FennecMomo/ResourceManager/releases/tag/v0.3.2"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ResourceManagerTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
