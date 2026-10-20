using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResourceManager.App;
using ResourceManager.Core;

internal static class PreviewLoaderChecks
{
    internal static async Task RunAsync(Action<bool, string> require)
    {
        var uri = new Uri(ShareCatalogClient.SiteUrl, "preview/check.png");
        require(SharePreviewLoader.IsPreviewUri(uri) &&
            !SharePreviewLoader.IsPreviewUri(new Uri("https://outside.invalid/a.png")) &&
            !SharePreviewLoader.IsPreviewUri(new Uri("http://share.fennecmomo.top/a.png")) &&
            !SharePreviewLoader.IsPreviewUri(new Uri("https://share.fennecmomo.top:444/a.png")) &&
            !SharePreviewLoader.IsPreviewUri(new Uri("https://name@share.fennecmomo.top/a.png")),
            "preview URLs require the station HTTPS origin without user information");

        var png = Png(2400, 100);
        var decoded = (BitmapSource)SharePreviewLoader.Decode(png, "image/png");
        require(decoded.IsFrozen && decoded.PixelWidth == 1200 && decoded.PixelHeight == 50,
            "raster previews decode to frozen bounded images without retaining a stream");
        var portrait = (BitmapSource)SharePreviewLoader.Decode(Png(10, 1800));
        require(portrait.PixelHeight <= 900 && portrait.PixelWidth <= 1200,
            "portrait preview downsampling bounds height as well as width");
        require(Throws(() => SharePreviewLoader.Decode([1, 2, 3])) &&
            Throws(() => SharePreviewLoader.Decode(new byte[SharePreviewLoader.MaxImageBytes + 1])),
            "invalid and oversized image payloads are rejected");

        var svg = Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" width="320" height="180" viewBox="0 0 320 180">
              <defs><linearGradient id="cover"><stop offset="0" stop-color="#367ac7"/><stop offset="1" stop-color="#9cd2fa"/></linearGradient></defs>
              <rect width="320" height="180" rx="10" fill="url(#cover)"/>
              <text x="18" y="70" font-family="Segoe UI" font-size="28" fill="#fff">App preview</text>
            </svg>
            """);
        var svgImage = SharePreviewLoader.Decode(svg, "image/svg+xml");
        require(svgImage is DrawingImage && svgImage.IsFrozen && svgImage.Width > 300 && svgImage.Height > 170,
            "safe SVG gradients and text render through the WPF SVG decoder");
        var shadowSvg = Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" width="320" height="180">
              <defs><filter id="shadow"><feDropShadow dx="0" dy="10" stdDeviation="16"/></filter></defs>
              <rect width="320" height="180" fill="#367ac7" filter="url(#shadow)"/>
            </svg>
            """);
        require(SharePreviewLoader.Decode(shadowSvg) is DrawingImage { IsFrozen: true },
            "SVG content remains visible when decorative shadow filters are omitted");
        foreach (var content in new[]
        {
            "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><svg xmlns='http://www.w3.org/2000/svg'>&x;</svg>",
            "<?xml-stylesheet href='https://outside.invalid/a.css'?><svg xmlns='http://www.w3.org/2000/svg'/>",
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='https://outside.invalid/a.png'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><use href='#loop'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><style>rect { fill:url(https://outside.invalid/a.svg); }</style></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><style>@import 'a.css';</style></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><text font-family='C:/private/font.ttf#Font'>a</text></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><style>text { font-family: '/private/font.ttf#Font'; }</style></svg>"
        })
            require(Throws(() => SharePreviewLoader.ValidateSvg(Encoding.UTF8.GetBytes(content))),
                "SVG blocks active content, external resources, fonts, and recursive references");
        var deep = "<svg xmlns='http://www.w3.org/2000/svg'>" + string.Concat(Enumerable.Repeat("<g>", 35)) +
            string.Concat(Enumerable.Repeat("</g>", 35)) + "</svg>";
        require(Throws(() => SharePreviewLoader.ValidateSvg(Encoding.UTF8.GetBytes(deep))),
            "SVG nesting depth is bounded before entering its renderer");

        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(png) }));
        using var http = new HttpClient(handler);
        var loader = new SharePreviewLoader(http);
        var loaded = await loader.LoadAsync(uri, CancellationToken.None);
        var cached = await loader.LoadAsync(uri, CancellationToken.None);
        require(loaded.IsFrozen && ReferenceEquals(loaded, cached) && handler.Calls == 1,
            "preview loader caches decoded frozen images and avoids repeat downloads");
        require(await ThrowsAsync(() => loader.LoadAsync(new Uri("https://outside.invalid/a.png"), CancellationToken.None)) && handler.Calls == 1,
            "invalid preview origins are rejected before an HTTP request");

        var redirectHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            { Headers = { Location = new Uri("https://outside.invalid/a.png") } }));
        using var redirectHttp = new HttpClient(redirectHandler);
        require(await ThrowsAsync(() => new SharePreviewLoader(redirectHttp).LoadAsync(uri, CancellationToken.None)) && redirectHandler.Calls == 1,
            "redirect responses fail without issuing an external request");

        var largeHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[SharePreviewLoader.MaxImageBytes + 1]) }));
        using var largeHttp = new HttpClient(largeHandler);
        require(await ThrowsAsync(() => new SharePreviewLoader(largeHttp).LoadAsync(uri, CancellationToken.None)),
            "advertised oversized responses fail before decoding");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelHandler = new FakeHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        using var cancelHttp = new HttpClient(cancelHandler);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new SharePreviewLoader(cancelHttp).LoadAsync(uri, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        require(await ThrowsAsync(async () => await cancelled), "preview network loads honor cancellation");
    }

    private static byte[] Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 140; pixels[i + 2] = 50; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch { return true; }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return respond(request, token);
        }
    }
}
