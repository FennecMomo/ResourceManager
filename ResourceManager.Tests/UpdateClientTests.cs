using System.Net;
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
