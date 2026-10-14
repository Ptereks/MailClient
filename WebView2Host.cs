using System.IO;
using Microsoft.Web.WebView2.Core;
using ZennoVibe.MailClient.Core;

namespace ZennoVibe.MailClient;

internal static class WebView2Host
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim DocumentationGate = new(1, 1);
    private static CoreWebView2Environment? _environment;
    private static CoreWebView2Environment? _documentationEnvironment;

    public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment != null)
            return _environment;

        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_environment != null)
                return _environment;

            Directory.CreateDirectory(AppPaths.WebView2UserDataDirectory);

            // Mail view only. Chromium must never reach the network on its own. Requests the
            // WebResourceRequested filter does not answer are pointed at a closed loopback
            // port and fail there instead of leaving with the real IP. Media is fetched by
            // .NET through the account proxy. Loopback stays on the default bypass list so
            // the connection to that closed port itself is not sent back through the proxy.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--proxy-server=http://127.0.0.1:1 " +
                    "--disable-background-networking " +
                    "--dns-prefetch-disable " +
                    "--disable-features=NetworkPrediction"
            };

            _environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppPaths.WebView2UserDataDirectory,
                    options)
                .ConfigureAwait(true);

            return _environment;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static readonly SemaphoreSlim LinkGate = new(1, 1);
    private static readonly Dictionary<string, CoreWebView2Environment> LinkEnvironments =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Browser profile for links clicked in a message. Chromium itself is pointed at the
    /// account proxy, so the page, its scripts and every sub-request leave through that proxy
    /// and nothing else: there is no DIRECT fallback in a fixed <c>--proxy-server</c> list, so
    /// a dead proxy means an error page, not the real IP. One profile per proxy line, kept
    /// under <c>data\webview2\links\</c>; the folder name is a hash, never the proxy itself.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetLinkEnvironmentAsync(ProxySpec proxy)
    {
        NetworkGuard.AssertProxy(proxy);

        await LinkGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (LinkEnvironments.TryGetValue(proxy.Display, out var existing))
                return existing;

            var folder = Path.Combine(AppPaths.WebView2LinksDirectory, ProfileName(proxy.Display));
            Directory.CreateDirectory(folder);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--proxy-server=" + ChromiumProxyServer(proxy) + " " +
                    // Loopback is normally exempt from the proxy; here nothing is.
                    "--proxy-bypass-list=<-loopback> " +
                    "--disable-background-networking " +
                    "--dns-prefetch-disable " +
                    "--disable-quic " +
                    // WebRTC would otherwise open UDP sockets straight from this machine.
                    "--force-webrtc-ip-handling-policy=disable_non_proxied_udp " +
                    "--disable-features=NetworkPrediction " +
                    "--no-first-run"
            };

            var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: folder,
                    options)
                .ConfigureAwait(true);

            LinkEnvironments[proxy.Display] = environment;
            return environment;
        }
        finally
        {
            LinkGate.Release();
        }
    }

    /// <summary>Chromium proxy syntax for the scheme this line most likely is.</summary>
    public static string ChromiumProxyServer(ProxySpec proxy)
    {
        var scheme = proxy.WebProxySchemes().First() switch
        {
            "socks5" => "socks5",
            "socks4" or "socks4a" => "socks4",
            _ => "http"
        };

        return scheme + "://" + proxy.Host + ":" + proxy.Port;
    }

    private static string ProfileName(string display)
    {
        var hash = 2166136261u;
        foreach (var ch in display)
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;

        return hash.ToString("x8");
    }

    /// <summary>
    /// Documentation profile. Stored inside the mail browser folder so data stays in one place.
    /// It does not use the mail proxy.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetDocumentationEnvironmentAsync()
    {
        if (_documentationEnvironment != null)
            return _documentationEnvironment;

        await DocumentationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_documentationEnvironment != null)
                return _documentationEnvironment;

            Directory.CreateDirectory(AppPaths.WebView2DocumentationDirectory);
            _documentationEnvironment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppPaths.WebView2DocumentationDirectory,
                    options: null)
                .ConfigureAwait(true);

            return _documentationEnvironment;
        }
        finally
        {
            DocumentationGate.Release();
        }
    }
}
