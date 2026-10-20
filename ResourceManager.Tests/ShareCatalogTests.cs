using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class ShareCatalogTests
{
    private const string CatalogJson = """
        {
          "schemaVersion": 1,
          "revision": 32,
          "generatedAt": "2026-10-19T13:47:41.043Z",
          "serverTime": "2026-10-19T14:43:22.847Z",
          "tags": ["工具", "测试项目"],
          "projects": [{
            "id": "paper-notes",
            "slug": "paper-notes",
            "title": "便签纸",
            "summary": "内容保存在本地浏览器的网页便签本。",
            "description": "添加、勾选、删除。\n纯静态页面。",
            "categories": ["web"],
            "tags": ["测试项目", "工具"],
            "cover": {"url": "/media/paper-notes/r/release/cover.svg", "alt": "便签纸封面"},
            "screenshots": [],
            "createdAt": "2026-10-18T13:02:16.583Z",
            "updatedAt": "2026-10-18T16:23:07.847Z",
            "publishedAt": "2026-10-18T13:02:16.583Z",
            "displayVersion": "1.0.0",
            "sortOrder": 30,
            "published": true,
            "demo": true,
            "web": {"releaseId": "release", "entry": "index.html", "publicUrl": "/play/paper-notes/", "spaFallback": false},
            "downloads": [{
              "id": "dl-b6309658d681",
              "fileName": "paper-notes-web-1.0.0.zip",
              "platform": "html",
              "version": "1.0.0",
              "size": 3563,
              "sha256": "13e690d020fa0ef38fe3de355c8c46f0e754c3947021aa473a84a145c04011d8",
              "contentType": "application/zip",
              "publicUrl": "/download/paper-notes/paper-notes-web-1.0.0.zip",
              "releaseId": "release",
              "isDefault": true
            }],
            "stats": {"playClicks": 0, "shareClicks": 0, "downloads": 1}
          }]
        }
        """;

    [Fact]
    public async Task ReadsCurrentPublishedCatalogSchemaAndResolvesPublicUrls()
    {
        using var http = CreateHttp((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://share.fennecmomo.top/api/projects", request.RequestUri!.AbsoluteUri);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/json");
            Assert.True(request.Headers.CacheControl!.NoCache);
            return Task.FromResult(JsonResponse(CatalogJson));
        });
        using var client = new ShareCatalogClient(http);

        var catalog = await client.GetProjectsAsync();

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(32, catalog.Revision);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T13:47:41.043Z"), catalog.GeneratedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T14:43:22.847Z"), catalog.ServerTime);
        Assert.Equal(["工具", "测试项目"], catalog.Tags);
        var project = Assert.Single(catalog.Projects);
        Assert.Equal("paper-notes", project.Id);
        Assert.Equal("paper-notes", project.Slug);
        Assert.Equal("便签纸", project.Title);
        Assert.Contains("本地浏览器", project.Summary);
        Assert.Contains("\n", project.Description);
        Assert.Equal(["web"], project.Categories);
        Assert.Equal(["测试项目", "工具"], project.Tags);
        Assert.Equal("1.0.0", project.DisplayVersion);
        Assert.True(project.Demo);
        Assert.True(project.IsTestProject);
        Assert.Equal("https://share.fennecmomo.top/?p=paper-notes", project.DetailsUrl.AbsoluteUri);
        Assert.Equal("https://share.fennecmomo.top/media/paper-notes/r/release/cover.svg", project.CoverUrl!.AbsoluteUri);
        Assert.Equal("便签纸封面", project.CoverAlt);
        Assert.Empty(project.Screenshots);
        Assert.Equal("https://share.fennecmomo.top/play/paper-notes/", project.WebUrl!.AbsoluteUri);
        var download = Assert.Single(project.Downloads);
        Assert.Equal("paper-notes-web-1.0.0.zip", download.FileName);
        Assert.Equal("html", download.Platform);
        Assert.Equal("1.0.0", download.Version);
        Assert.Equal(3563, download.Size);
        Assert.Equal("13e690d020fa0ef38fe3de355c8c46f0e754c3947021aa473a84a145c04011d8", download.Sha256);
        Assert.True(download.IsDefault);
        Assert.Equal("https://share.fennecmomo.top/download/paper-notes/paper-notes-web-1.0.0.zip", download.DownloadUrl.AbsoluteUri);
    }

    [Fact]
    public async Task EmptyPublishedCatalogIsValid()
    {
        var catalog = await ReadAsync("""
            {"schemaVersion":1,"revision":0,"generatedAt":null,"serverTime":null,"tags":[],"projects":[]}
            """);

        Assert.Empty(catalog.Projects);
        Assert.Empty(catalog.Tags);
        Assert.Null(catalog.GeneratedAt);
    }

    [Fact]
    public async Task NativeOnlyProjectCanHaveNoCoverOrWebRelease()
    {
        var json = Mutate(root =>
        {
            var project = root["projects"]![0]!;
            project["cover"] = null;
            project["web"] = null;
            project["displayVersion"] = null;
            project["categories"] = new JsonArray("desktop");
        });

        var project = Assert.Single((await ReadAsync(json)).Projects);

        Assert.Null(project.CoverUrl);
        Assert.Null(project.CoverAlt);
        Assert.Null(project.WebUrl);
        Assert.Null(project.DisplayVersion);
        Assert.Equal(["desktop"], project.Categories);
        Assert.Single(project.Downloads);
    }

    [Theory]
    [InlineData("https://example.com/payload.exe")]
    [InlineData("http://share.fennecmomo.top/payload.exe")]
    [InlineData("//example.com/payload.exe")]
    [InlineData("https://share.fennecmomo.top.example.com/payload.exe")]
    [InlineData("https://user@share.fennecmomo.top/payload.exe")]
    [InlineData("https://share.fennecmomo.top:8443/payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/payload.exe")]
    public async Task UnsafePublicLinksAreNotExposedToUi(string url)
    {
        var json = Mutate(root =>
        {
            var project = root["projects"]![0]!;
            project["cover"]!["url"] = url;
            project["screenshots"] = new JsonArray(new JsonObject { ["url"] = url, ["alt"] = "预览" });
            project["web"]!["publicUrl"] = url;
            project["downloads"]![0]!["publicUrl"] = url;
        });

        var project = Assert.Single((await ReadAsync(json)).Projects);

        Assert.Null(project.CoverUrl);
        Assert.Empty(project.Screenshots);
        Assert.Null(project.WebUrl);
        Assert.Empty(project.Downloads);
        Assert.Equal("https://share.fennecmomo.top/?p=paper-notes", project.DetailsUrl.AbsoluteUri);
    }

    [Fact]
    public async Task AbsoluteSameOriginLinksRemainUsable()
    {
        var json = Mutate(root => root["projects"]![0]!["web"]!["publicUrl"] =
            "https://share.fennecmomo.top/play/paper-notes/");

        var project = Assert.Single((await ReadAsync(json)).Projects);

        Assert.Equal("https://share.fennecmomo.top/play/paper-notes/", project.WebUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task ReadsScreenshotsInDisplayOrderAndAllowsMissingAlternativeText()
    {
        var json = Mutate(root => root["projects"]![0]!["screenshots"] = new JsonArray(
            new JsonObject { ["url"] = "/media/paper-notes/first.png", ["alt"] = "初始界面" },
            new JsonObject { ["url"] = "https://share.fennecmomo.top/media/paper-notes/second.svg" }));

        var project = Assert.Single((await ReadAsync(json)).Projects);

        Assert.Collection(project.Screenshots,
            first =>
            {
                Assert.Equal("https://share.fennecmomo.top/media/paper-notes/first.png", first.Url.AbsoluteUri);
                Assert.Equal("初始界面", first.Alt);
            },
            second =>
            {
                Assert.Equal("https://share.fennecmomo.top/media/paper-notes/second.svg", second.Url.AbsoluteUri);
                Assert.Null(second.Alt);
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogWithoutOptionalPreviewMetadataRemainsUsable(bool omitScreenshots)
    {
        var json = Mutate(root =>
        {
            var project = root["projects"]![0]!.AsObject();
            project["cover"]!.AsObject().Remove("alt");
            if (omitScreenshots) project.Remove("screenshots");
            else project["screenshots"] = null;
        });

        var project = Assert.Single((await ReadAsync(json)).Projects);

        Assert.NotNull(project.CoverUrl);
        Assert.Null(project.CoverAlt);
        Assert.Empty(project.Screenshots);
        Assert.Single(project.Downloads);
    }

    [Fact]
    public async Task SkipsUnavailableScreenshotEntriesWithoutLosingValidPreviews()
    {
        var json = Mutate(root => root["projects"]![0]!["screenshots"] = new JsonArray(
            null, "unavailable", new JsonObject(),
            new JsonObject { ["url"] = "" },
            new JsonObject { ["url"] = "https://example.com/external.png" },
            new JsonObject { ["url"] = "/media/paper-notes/valid.png", ["alt"] = "有效预览" }));

        var project = Assert.Single((await ReadAsync(json)).Projects);

        var preview = Assert.Single(project.Screenshots);
        Assert.Equal("https://share.fennecmomo.top/media/paper-notes/valid.png", preview.Url.AbsoluteUri);
        Assert.Equal("有效预览", preview.Alt);
    }

    [Theory]
    [InlineData(true, "工具", true)]
    [InlineData(false, "测试项目", true)]
    [InlineData(true, "测试项目", true)]
    [InlineData(false, "工具", false)]
    [InlineData(false, "测试项目备份", false)]
    [InlineData(false, "测试", false)]
    public async Task RecognizesTestProjectsFromDemoFlagOrExactTag(bool demo, string tag, bool expected)
    {
        var json = Mutate(root =>
        {
            root["projects"]![0]!["demo"] = demo;
            root["projects"]![0]!["tags"] = new JsonArray(tag);
        });

        var project = Assert.Single((await ReadAsync(json)).Projects);

        Assert.Equal(expected, project.IsTestProject);
    }

    [Fact]
    public async Task UnpublishedEntriesAreExcluded()
    {
        var catalog = await ReadAsync(Mutate(root => root["projects"]![0]!["published"] = false));

        Assert.Empty(catalog.Projects);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("demo&other=1")]
    [InlineData("demo#hash")]
    [InlineData("")]
    public async Task InvalidSlugsCannotBecomeDetailLinks(string slug)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(
            Mutate(root => root["projects"]![0]!["slug"] = slug)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"projects\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"projects\":null}")]
    public async Task RejectsMalformedOrUnsupportedCatalogs(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(json));
    }

    [Fact]
    public async Task RejectsMissingTitlesAndNegativeDownloadSizes()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(
            Mutate(root => root["projects"]![0]!["title"] = " ")));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(
            Mutate(root => root["projects"]![0]!["downloads"]![0]!["size"] = -1)));
    }

    [Fact]
    public async Task ReportsFailedRequests()
    {
        using var http = CreateHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new ShareCatalogClient(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProjectsAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task RejectsResponseRedirectedOutsideShareSite()
    {
        using var http = CreateHttp((_, _) =>
        {
            var response = JsonResponse(CatalogJson);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api/projects");
            return Task.FromResult(response);
        });
        using var client = new ShareCatalogClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetProjectsAsync());
    }

    [Fact]
    public async Task HonorsCallerCancellationDuringRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = CreateHttp(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(CatalogJson);
        });
        using var client = new ShareCatalogClient(http);
        using var cancellation = new CancellationTokenSource();

        var request = client.GetProjectsAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task HonorsCallerCancellationWhileReadingResponseBody()
    {
        using var stream = new BlockingReadStream();
        using var http = CreateHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) }));
        using var client = new ShareCatalogClient(http);
        using var cancellation = new CancellationTokenSource();

        var request = client.GetProjectsAsync(cancellation.Token);
        await stream.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOversizedMetadataWithOrWithoutContentLength(bool knownLength)
    {
        var bytes = new byte[2 * 1024 * 1024 + 1];
        using var http = CreateHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekableStream(bytes))
        }));
        using var client = new ShareCatalogClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetProjectsAsync());
    }

    [Fact]
    public async Task DoesNotDisposeCallerOwnedHttpClient()
    {
        using var http = CreateHttp((_, _) => Task.FromResult(JsonResponse(CatalogJson)));
        var client = new ShareCatalogClient(http);
        client.Dispose();

        using var response = await http.GetAsync(ShareCatalogClient.SiteUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Mutate(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(CatalogJson)!;
        change(root);
        return root.ToJsonString();
    }

    private static async Task<ShareCatalog> ReadAsync(string json)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(JsonResponse(json)));
        using var client = new ShareCatalogClient(http);
        return await client.GetProjectsAsync();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient CreateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class BlockingReadStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
