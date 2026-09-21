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
}
