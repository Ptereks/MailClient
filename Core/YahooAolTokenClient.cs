using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace ZennoVibe.MailClient.Core;

internal sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresInSeconds = 3600);

internal sealed class TokenEndpointException : Exception
{
    public TokenEndpointException(string message, bool isTerminal) : base(message)
    {
        IsTerminal = isTerminal;
    }

    public bool IsTerminal { get; }
}

internal static class YahooAolTokenClient
{
    private const string YahooClientId = "dj0yJmk9NUtCTWFMNVpTaVJmJmQ9WVdrOVJ6UjVTa2xJTXpRbWNHbzlNQS0tJnM9Y29uc3VtZXJzZWNyZXQmeD0yYw--";
    private const string YahooClientSecret = "f2de6a30ae123cdbc258c15e0812799010d589cc";
    private const string AolClientId = "dj0yJmk9MGNoTTQ0SjhIN1dSJmQ9WVdrOVdIVnFkVVp2UkZNbWNHbzlNQT09JnM9Y29uc3VtZXJzZWNyZXQmc3Y9MCZ4PTNm";

    // One client (and therefore one connection pool) per proxy: a fresh HttpClient per
    // refresh exhausts sockets once dozens of accounts sync in parallel. Lazy keeps
    // GetOrAdd from building throwaway clients when threads race on the same proxy.
    private static readonly ConcurrentDictionary<string, Lazy<HttpClient>> Clients = new();

    public static async Task<TokenResponse> RefreshAsync(
        string email,
        string refreshToken,
        string site,
        ProxySpec? proxy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new TokenEndpointException(email + " refresh token is empty.", true);

        // Every caller passes the account proxy; this keeps the direct branch below unreachable.
        NetworkGuard.AssertProxy(proxy);

        if (proxy == null)
            return await PostRefreshAsync(email, refreshToken, site, null, "", cancellationToken);

        // An unlabeled proxy line may be SOCKS5. Trying HTTP only made every sync fail at the
        // token step, before IMAP was ever reached, on lists that work elsewhere.
        Exception? lastTransportError = null;

        foreach (var scheme in proxy.WebProxySchemes())
        {
            try
            {
                var response = await PostRefreshAsync(email, refreshToken, site, proxy, scheme, cancellationToken);
                proxy.RememberWorkingScheme(scheme);
                return response;
            }
            catch (TokenEndpointException)
            {
                // The endpoint answered, so this scheme carries traffic even if the token is
                // rejected: another scheme cannot help, and the scheme itself is confirmed.
                proxy.RememberWorkingScheme(scheme);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "token",
                    "Token refresh for " + email + " could not reach the endpoint over " +
                    scheme + " proxy " + proxy.Summary,
                    ex);
                lastTransportError = ex;
            }
        }

        throw lastTransportError ?? new TokenEndpointException(
            email + " token refresh failed through proxy " + proxy.Summary + ".",
            false);
    }

    private static async Task<TokenResponse> PostRefreshAsync(
        string email,
        string refreshToken,
        string site,
        ProxySpec? proxy,
        string proxyScheme,
        CancellationToken cancellationToken)
    {
        var clientId = site == "aol" ? AolClientId : YahooClientId;
        var useClientSecret = site == "yahoo";
        var tokenUrl = "https://api.login." + site + ".com/oauth2/get_token";

        var client = GetClient(proxy, proxyScheme);

        var values = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("client_id", clientId),
            new("refresh_token", refreshToken)
        };

        if (useClientSecret)
            values.Add(new KeyValuePair<string, string>("client_secret", YahooClientSecret));

        using var content = new FormUrlEncodedContent(values);
        using var response = await client.PostAsync(tokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new TokenEndpointException("HTTP " + (int)response.StatusCode + ": " + body, IsTerminalBody(body));

        using var json = System.Text.Json.JsonDocument.Parse(body);
        var root = json.RootElement;

        if (root.TryGetProperty("error", out var error))
            throw new TokenEndpointException(error.ToString() + ": " + body, IsTerminalBody(body));

        if (!root.TryGetProperty("access_token", out var accessTokenElement))
            throw new TokenEndpointException("access_token was not found. Response: " + body, IsTerminalBody(body));

        var accessToken = accessTokenElement.GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new TokenEndpointException("access_token is empty. Response: " + body, IsTerminalBody(body));

        var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : null;

        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) &&
                          expiresElement.TryGetInt32(out var seconds) &&
                          seconds > 0
            ? seconds
            : 3600;

        return new TokenResponse(accessToken, newRefreshToken, expiresIn);
    }

    private static HttpClient GetClient(ProxySpec? proxy, string scheme) =>
        Clients.GetOrAdd(
                (proxy?.Display ?? "") + "|" + scheme,
                _ => new Lazy<HttpClient>(
                    () => CreateClient(proxy, scheme),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;

    private static HttpClient CreateClient(ProxySpec? proxy, string scheme)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseProxy = proxy != null,
            MaxConnectionsPerServer = 64
        };

        if (proxy != null)
            handler.Proxy = proxy.CreateWebProxy(scheme);

        return new HttpClient(handler)
        {
            // A proxy that has not answered in ten seconds will not answer in twenty, and the
            // wrong scheme has to time out before the next one is tried.
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static bool IsTerminalBody(string body)
    {
        var lower = body.ToLowerInvariant();
        return lower.Contains("invalid_grant") ||
               lower.Contains("invalid_client") ||
               lower.Contains("unauthorized_client") ||
               lower.Contains("refresh token");
    }
}
