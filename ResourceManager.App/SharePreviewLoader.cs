using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using ResourceManager.Core;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace ResourceManager.App;

/// <summary>Fetches bounded, same-site image data without letting image formats fetch other resources.</summary>
internal sealed class SharePreviewLoader
{
    internal const int MaxImageBytes = 4 * 1024 * 1024;
    internal const int MaxSvgBytes = 1024 * 1024;
    private const long MaxSourcePixels = 16_000_000;
    private const long MaxCacheBytes = 16 * 1024 * 1024;
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    internal static readonly SharePreviewLoader Shared = new(SharedHttp);
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(3);
    private readonly object cacheLock = new();
    private readonly Dictionary<Uri, (ImageSource Image, long Bytes)> cache = [];
    private readonly Queue<Uri> cacheOrder = [];
    private long cachedBytes;

    internal SharePreviewLoader(HttpClient httpClient) => http = httpClient;

    internal async Task<ImageSource> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsPreviewUri(uri)) throw new InvalidDataException("预览图必须来自应用站。");
        cancellationToken.ThrowIfCancellationRequested();
        lock (cacheLock)
            if (cache.TryGetValue(uri, out var cached)) return cached.Image;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await slots.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            lock (cacheLock)
                if (cache.TryGetValue(uri, out var cached)) return cached.Image;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsPreviewUri(finalUri))
                throw new InvalidDataException("预览图返回了站外地址。");
            if (response.Content.Headers.ContentLength > MaxImageBytes)
                throw new InvalidDataException("预览图文件过大。");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxImageBytes) throw new InvalidDataException("预览图文件过大。");
                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            var data = buffer.ToArray();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var image = await DecodeOnWorkerAsync(data, mediaType, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            AddToCache(uri, image);
            return image;
        }
        finally { slots.Release(); }
    }

    internal static bool IsPreviewUri(Uri? uri) => uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == ShareCatalogClient.SiteUrl.Port &&
        string.Equals(uri.IdnHost, ShareCatalogClient.SiteUrl.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static Task<ImageSource> DecodeOnWorkerAsync(byte[] data, string? mediaType, CancellationToken token)
    {
        // A dedicated STA keeps WPF/SVG work off the UI dispatcher; the semaphore bounds worker count.
        var completion = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var image = Decode(data, mediaType);
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(image);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "ResourceManager application preview" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    internal static ImageSource Decode(byte[] data, string? mediaType = null)
    {
        if (data.Length == 0 || data.Length > MaxImageBytes) throw new InvalidDataException("预览图文件大小无效。");
        var offset = data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        while (offset < data.Length && char.IsWhiteSpace((char)data[offset])) offset++;
        var isSvg = string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
            (offset < data.Length && data[offset] == '<');
        if (isSvg) return DecodeSvg(data);

        using var source = new MemoryStream(data, false);
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaxSourcePixels)
            throw new InvalidDataException("预览图尺寸过大或无效。");
        var scale = Math.Max(1d, Math.Max(frame.PixelWidth / 1200d, frame.PixelHeight / 900d));
        source.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth / scale));
        image.StreamSource = source;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageSource DecodeSvg(byte[] data)
    {
        var svg = ValidateSvg(data);
        var document = XDocument.Parse(svg);
        // Decorative SVG shadows are omitted: WPF can display the vector content without allocating
        // attacker-sized filter surfaces. Active/external filter primitives are still rejected above.
        foreach (var filter in document.Descendants().Where(element => element.Name.LocalName == "filter").ToArray()) filter.Remove();
        foreach (var filter in document.Descendants().Attributes().Where(attribute => attribute.Name.LocalName == "filter").ToArray()) filter.Remove();
        using var reader = new StringReader(document.ToString(SaveOptions.DisableFormatting));
        var settings = new WpfDrawingSettings { IncludeRuntime = false };
        using var converter = new FileSvgReader(settings);
        var drawing = converter.Read(reader) ?? throw new InvalidDataException("SVG 预览图没有可显示的内容。");
        if (drawing.Bounds.IsEmpty || !double.IsFinite(drawing.Bounds.Width) || !double.IsFinite(drawing.Bounds.Height) ||
            drawing.Bounds.Width > 100_000 || drawing.Bounds.Height > 100_000 || !drawing.CanFreeze)
            throw new InvalidDataException("SVG 预览图的尺寸或内容无效。");
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    internal static string ValidateSvg(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxSvgBytes) throw new InvalidDataException("SVG 预览图文件过大。");
        using var input = new MemoryStream(data, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSvgBytes
        });
        var document = XDocument.Load(reader);
        XNamespace svgNamespace = "http://www.w3.org/2000/svg";
        if (document.Root?.Name != svgNamespace + "svg" || document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new InvalidDataException("SVG 预览图格式不受支持。");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "svg", "g", "defs", "rect", "circle", "ellipse", "line", "polyline", "polygon", "path",
            "text", "tspan", "title", "desc", "linearGradient", "radialGradient", "stop", "clipPath",
            "mask", "pattern", "style", "filter", "feDropShadow"
        };
        var elements = document.Root.DescendantsAndSelf().ToArray();
        if (elements.Length > 2000) throw new InvalidDataException("SVG 预览图内容过多。");
        foreach (var element in elements)
        {
            if (element.Name.Namespace != svgNamespace || !allowed.Contains(element.Name.LocalName) ||
                element.Ancestors().Count() > 32)
                throw new InvalidDataException("SVG 预览图包含不支持的元素。");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Name.LocalName is "href" or "base" || attribute.Value.Length > 65536 ||
                    (attribute.Name.LocalName == "font-family" && attribute.Value.IndexOfAny(['/', ':']) >= 0) ||
                    !HasOnlyInternalReferences(attribute.Value))
                    throw new InvalidDataException("SVG 预览图包含外部引用或过于复杂的内容。");
            }
            if (element.Name.LocalName == "style" && !HasOnlyInternalReferences(element.Value))
                throw new InvalidDataException("SVG 预览图包含外部样式引用。");
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static bool HasOnlyInternalReferences(string text)
    {
        // External images, fonts, stylesheet imports, and escaped CSS URLs never reach the SVG renderer.
        if (text.Contains('@') || text.Contains('\\') || text.Contains("//", StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"(?:file|pack|data)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(text, @"font-family\s*:[^;}]*[/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        foreach (Match match in Regex.Matches(text, @"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var reference = match.Groups[1].Value.Trim().Trim('\'', '"');
            if (!Regex.IsMatch(reference, @"^#[a-zA-Z_][a-zA-Z0-9_.:-]*$", RegexOptions.CultureInvariant)) return false;
        }
        return true;
    }

    private void AddToCache(Uri uri, ImageSource image)
    {
        var cost = image is BitmapSource bitmap ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 : MaxSvgBytes;
        lock (cacheLock)
        {
            if (cache.ContainsKey(uri)) return;
            while (cacheOrder.Count > 0 && (cachedBytes + cost > MaxCacheBytes || cache.Count >= 24))
            {
                var key = cacheOrder.Dequeue();
                if (cache.Remove(key, out var old)) cachedBytes -= old.Bytes;
            }
            cache[uri] = (image, cost);
            cacheOrder.Enqueue(uri);
            cachedBytes += cost;
        }
    }
}
