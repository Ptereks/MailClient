using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using ZennoVibe.MailClient.Core;

namespace ZennoVibe.MailClient.Services;

/// <summary>
/// Downloads remote message media through the same proxy used for IMAP. WebView2 has no
/// proxy of its own, so this is the only way an image can load without exposing the real IP.
/// </summary>
internal static class ProxiedMediaFetcher
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxCachedItems = 80;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<string, Lazy<HttpClient>> Clients = new();
    private static readonly ConcurrentDictionary<string, (string Type, byte[] Bytes)> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<FetchedMedia?>> Inflight = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProbeGates = new();
    private static readonly ConcurrentDictionary<string, byte> ProbedProxies = new();

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".avif", ".tif", ".tiff",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp4", ".webm", ".ogv", ".mp3", ".wav", ".ogg",
        ".css"
    };

    public static bool IsAllowed(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext context, Uri uri)
    {
        switch (context)
        {
            case Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image:
            case Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Media:
            case Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Font:
            case Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Stylesheet:
                return true;
            case Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Other:
                return LooksLikeMedia(uri);
            default:
                return false;
        }
    }

    public static async Task<FetchedMedia?> GetAsync(
        Uri uri,
        ProxySpec proxy,
        CancellationToken cancellationToken)
    {
        NetworkGuard.AssertProxy(proxy);

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var key = uri.AbsoluteUri;
        if (Cache.TryGetValue(key, out var cached))
            return Cached(cached);

        // One mail can ask for dozens of images at once. An unlabelled proxy is probed as
        // HTTP and then SOCKS5, and a wrong scheme costs the full timeout. Without a gate
        // every image pays that timeout together and the page sits there until the last one.
        if (proxy.Scheme.Length == 0 && !ProbedProxies.ContainsKey(proxy.Display))
        {
            var gate = ProbeGates.GetOrAdd(proxy.Display, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!ProbedProxies.ContainsKey(proxy.Display))
                {
                    var probed = await FetchOnceAsync(uri, proxy, cancellationToken);
                    ProbedProxies[proxy.Display] = 1;
                    return probed;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        var pending = Inflight.GetOrAdd(key, _ => FetchOnceAsync(uri, proxy, cancellationToken));
        try
        {
            return await pending;
        }
        finally
        {
            Inflight.TryRemove(key, out _);
        }
    }

    private static async Task<FetchedMedia?> FetchOnceAsync(
        Uri uri,
        ProxySpec proxy,
        CancellationToken cancellationToken)
    {
        var key = uri.AbsoluteUri;
        if (Cache.TryGetValue(key, out var cached))
            return Cached(cached);

        Exception? lastError = null;
        foreach (var scheme in proxy.WebProxySchemes())
        {
            try
            {
                var result = await GetWithSchemeAsync(uri, proxy, scheme, cancellationToken);
                proxy.RememberWorkingScheme(scheme);
                Remember(key, result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError != null)
            AppLog.Warning("media", "Could not fetch " + uri.Host + " via " + proxy.Summary, lastError);

        return null;
    }

    private static void Remember(string key, FetchedMedia result)
    {
        if (result.StatusCode is < 200 or >= 300)
            return;

        var bytes = result.Body.ToArray();
        result.Body.Position = 0;
        if (Cache.Count >= MaxCachedItems)
        {
            var oldest = Cache.Keys.FirstOrDefault();
            if (oldest != null)
                Cache.TryRemove(oldest, out _);
        }

        Cache[key] = (result.ContentType, bytes);
    }

    private static FetchedMedia Cached((string Type, byte[] Bytes) item) =>
        new(200, item.Type, new MemoryStream(item.Bytes, writable: false));

    private static async Task<FetchedMedia> GetWithSchemeAsync(
        Uri uri,
        ProxySpec proxy,
        string scheme,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "image/*,video/*,audio/*,font/*,text/css,*/*;q=0.1");

        using var response = await GetClient(proxy, scheme)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? GuessContentType(uri);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        var copy = new byte[81920];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(copy.AsMemory(0, copy.Length), cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxBytes)
                throw new IOException("Remote media exceeded " + MaxBytes + " bytes.");

            buffer.Write(copy, 0, read);
        }

        buffer.Position = 0;
        return new FetchedMedia((int)response.StatusCode, contentType, buffer);
    }

    private static bool LooksLikeMedia(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        return ext.Length > 0 && MediaExtensions.Contains(ext);
    }

    private static string GuessContentType(Uri uri)
    {
        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".css" => "text/css",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }

    private static HttpClient GetClient(ProxySpec proxy, string scheme) =>
        Clients.GetOrAdd(
                proxy.Display + "|" + scheme,
                _ => new Lazy<HttpClient>(
                    () => CreateClient(proxy, scheme),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;

    private static HttpClient CreateClient(ProxySpec proxy, string scheme)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseProxy = true,
            Proxy = proxy.CreateWebProxy(scheme),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            MaxConnectionsPerServer = 8
        };

        return new HttpClient(handler) { Timeout = RequestTimeout };
    }
}

internal sealed record FetchedMedia(int StatusCode, string ContentType, MemoryStream Body);
