using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

public sealed record ShareCatalog(int SchemaVersion, long Revision, DateTimeOffset? GeneratedAt,
    DateTimeOffset? ServerTime, IReadOnlyList<string> Tags, IReadOnlyList<ShareProject> Projects);

public sealed record ShareProject(string Id, string Slug, string Title, string Summary, string Description,
    IReadOnlyList<string> Categories, IReadOnlyList<string> Tags, string? DisplayVersion,
    DateTimeOffset? PublishedAt, DateTimeOffset? UpdatedAt, bool Demo, Uri DetailsUrl, Uri? CoverUrl,
    Uri? WebUrl, IReadOnlyList<ShareDownload> Downloads);

public sealed record ShareDownload(string FileName, string Platform, string? Version, long Size,
    string? Sha256, bool IsDefault, Uri DownloadUrl);

public sealed class ShareCatalogClient : IDisposable
{
    public static readonly Uri SiteUrl = new("https://share.fennecmomo.top/");
    private static readonly Uri CatalogUrl = new(SiteUrl, "api/projects");
    private const int MaxCatalogBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public ShareCatalogClient(HttpClient? httpClient = null)
    {
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<ShareCatalog> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUrl && !IsSiteUrl(finalUrl))
            throw new InvalidDataException("项目分享站返回了站外地址。");
        if (response.Content.Headers.ContentLength > MaxCatalogBytes)
            throw new InvalidDataException("项目分享站的项目列表过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaxCatalogBytes)
                throw new InvalidDataException("项目分享站的项目列表过大。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }
        timeout.Token.ThrowIfCancellationRequested();
        try { return ParseCatalog(buffer.ToArray()); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException)
        { throw new InvalidDataException("项目分享站返回的项目列表无效。", error); }
    }

    private static ShareCatalog ParseCatalog(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != 1) throw new InvalidDataException("项目分享站的项目列表版本不受支持。");
        var projects = new List<ShareProject>();
        foreach (var item in root.GetProperty("projects").EnumerateArray())
        {
            if (!item.GetProperty("published").GetBoolean()) continue;
            var slug = RequiredString(item, "slug");
            if (!Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("项目分享站返回的项目标识无效。");
            var downloads = new List<ShareDownload>();
            foreach (var download in item.GetProperty("downloads").EnumerateArray())
            {
                var url = ReadSiteUrl(download, "publicUrl");
                if (url is null) continue;
                var size = download.GetProperty("size").GetInt64();
                if (size < 0) throw new InvalidDataException("项目分享站返回的下载大小无效。");
                downloads.Add(new ShareDownload(RequiredString(download, "fileName"),
                    RequiredString(download, "platform"), OptionalString(download, "version"), size,
                    OptionalString(download, "sha256"), download.GetProperty("isDefault").GetBoolean(), url));
            }
            var cover = item.GetProperty("cover");
            var web = item.GetProperty("web");
            projects.Add(new ShareProject(RequiredString(item, "id"), slug, RequiredString(item, "title"),
                item.GetProperty("summary").GetString() ?? "", item.GetProperty("description").GetString() ?? "",
                ReadStrings(item, "categories"), ReadStrings(item, "tags"), OptionalString(item, "displayVersion"),
                ReadDate(item, "publishedAt"), ReadDate(item, "updatedAt"), item.GetProperty("demo").GetBoolean(),
                new Uri(SiteUrl, "?p=" + Uri.EscapeDataString(slug)),
                cover.ValueKind == JsonValueKind.Null ? null : ReadSiteUrl(cover, "url"),
                web.ValueKind == JsonValueKind.Null ? null : ReadSiteUrl(web, "publicUrl"), downloads.AsReadOnly()));
        }
        return new ShareCatalog(schemaVersion, root.GetProperty("revision").GetInt64(),
            ReadDate(root, "generatedAt"), ReadDate(root, "serverTime"), ReadStrings(root, "tags"), projects.AsReadOnly());
    }

    private static string RequiredString(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("项目分享站返回的项目资料不完整。");
        return value;
    }

    private static string? OptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement item, string name) =>
        item.GetProperty(name).EnumerateArray().Select(value => value.GetString()
            ?? throw new InvalidDataException("项目分享站返回的分类或标签无效。")).ToArray();

    private static DateTimeOffset? ReadDate(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetDateTimeOffset() : null;

    private static Uri? ReadSiteUrl(JsonElement item, string name)
    {
        var value = OptionalString(item, name);
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(SiteUrl, value, out var url) && IsSiteUrl(url)
            ? url : null;
    }

    private static bool IsSiteUrl(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        string.Equals(url.IdnHost, SiteUrl.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        url.Port == SiteUrl.Port && string.IsNullOrEmpty(url.UserInfo);

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }
}
