using System.Collections.Concurrent;
using System.IO;
using System.Net;
using MailKit.Net.Proxy;

namespace ZennoVibe.MailClient.Core;

internal sealed class ProxySpec
{
    private static readonly char[] Separators = [':', ';', '|', ',', ' ', '\t'];

    /// <summary>
    /// Scheme that last carried traffic for a proxy line, keyed by the line itself. Probing a
    /// SOCKS5 proxy as HTTP costs a full connection timeout, and without this every account
    /// pays it again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> ConfirmedSchemes = new();

    private ProxySpec(string scheme, string host, int port, string? username, string? password, string display)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        Username = username;
        Password = password;
        Display = display;
    }

    public string Scheme { get; }
    public string Host { get; }
    public int Port { get; }
    public string? Username { get; }
    public string? Password { get; }
    public string Display { get; }

    /// <summary>Credentials must never reach the log file, so only the endpoint is shown.</summary>
    public string Summary =>
        (Scheme.Length == 0 ? "auto" : Scheme) + "://" +
        (string.IsNullOrEmpty(Username) ? "" : "***@") +
        Host + ":" + Port;

    /// <summary>
    /// Schemes to try for plain HTTP requests. A line without a scheme can be either kind of
    /// proxy and only a real request tells them apart, so both are offered in turn — the same
    /// way <see cref="CreateMailKitProxyCandidates"/> does it for IMAP.
    /// </summary>
    public IEnumerable<string> WebProxySchemes() => CandidateSchemes();

    /// <summary>
    /// Records the scheme that just worked, so every later account starts with it instead of
    /// waiting out a timeout on the wrong one.
    /// </summary>
    public void RememberWorkingScheme(string scheme)
    {
        if (Scheme.Length > 0 || scheme.Length == 0)
            return;

        ConfirmedSchemes[Display] = NormalizeScheme(scheme);
    }

    public void RememberWorkingScheme(IProxyClient client) =>
        RememberWorkingScheme(client switch
        {
            Socks5Client => "socks5",
            Socks4aClient => "socks4a",
            Socks4Client => "socks4",
            _ => "http"
        });

    /// <summary>
    /// A labelled line has exactly one candidate. An unlabelled one leads with the scheme that
    /// already worked for it, then still falls back to the rest.
    /// </summary>
    private IEnumerable<string> CandidateSchemes()
    {
        if (Scheme.Length > 0)
        {
            yield return Scheme;
            yield break;
        }

        if (ConfirmedSchemes.TryGetValue(Display, out var confirmed))
        {
            yield return confirmed;

            if (confirmed != "http")
                yield return "http";

            if (confirmed != "socks5")
                yield return "socks5";

            yield break;
        }

        yield return "http";
        yield return "socks5";
    }

    public IWebProxy CreateWebProxy() =>
        CreateWebProxy(EffectiveScheme);

    public IWebProxy CreateWebProxy(string scheme)
    {
        var normalized = NormalizeScheme(scheme);
        var proxyUri = new Uri($"{normalized}://{Host}:{Port}");
        var webProxy = new WebProxy(proxyUri);

        if (!string.IsNullOrEmpty(Username))
            webProxy.Credentials = new NetworkCredential(Username, Password);

        return webProxy;
    }

    /// <summary>
    /// A line without a scheme could be either kind of proxy, so both are tried in turn
    /// instead of asking the user to label the list.
    /// </summary>
    public IEnumerable<IProxyClient> CreateMailKitProxyCandidates()
    {
        foreach (var scheme in CandidateSchemes())
            yield return CreateMailKitProxy(scheme);
    }

    public IProxyClient CreateMailKitProxy(string scheme)
    {
        var normalized = NormalizeScheme(scheme);
        NetworkCredential? credentials = null;
        if (!string.IsNullOrEmpty(Username))
            credentials = new NetworkCredential(Username, Password);

        return normalized switch
        {
            "socks5" => credentials != null
                ? new Socks5Client(Host, Port, credentials)
                : new Socks5Client(Host, Port),
            "socks4a" => credentials != null
                ? new Socks4aClient(Host, Port, credentials)
                : new Socks4aClient(Host, Port),
            "socks4" => credentials != null
                ? new Socks4Client(Host, Port, credentials)
                : new Socks4Client(Host, Port),
            _ => credentials != null
                ? new HttpProxyClient(Host, Port, credentials)
                : new HttpProxyClient(Host, Port)
        };
    }

    /// <summary>
    /// Lines without an explicit scheme are treated as HTTP proxies.
    /// </summary>
    public IProxyClient CreateMailKitProxy() => CreateMailKitProxy(EffectiveScheme);

    private string EffectiveScheme => string.IsNullOrEmpty(Scheme) ? "http" : Scheme;

    private static string NormalizeScheme(string scheme) =>
        scheme.Trim().ToLowerInvariant() switch
        {
            "" or "http" or "https" or "http-proxy" => "http",
            "socks" or "socks5" or "socks5h" => "socks5",
            "socks4a" => "socks4a",
            "socks4" => "socks4",
            var other => other
        };

    /// <summary>
    /// Accepts the proxy layouts that appear in the wild and normalises them, so a list can
    /// be pasted as-is: with or without a scheme, credentials before or after the endpoint,
    /// and separated by any of colon, semicolon, pipe, comma, space or tab.
    /// </summary>
    public static ProxySpec Parse(string input)
    {
        var display = input.Trim();
        var line = StripTrailingComment(display);

        if (line.Length == 0)
            throw new FormatException("Proxy line is empty.");

        var scheme = "";
        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            scheme = NormalizeScheme(line[..schemeEnd]);
            line = line[(schemeEnd + 3)..];
        }

        line = line.Trim().TrimEnd('/');

        var at = line.LastIndexOf('@');
        if (at >= 0)
            return ParseWithCredentialMarker(scheme, line, at, display, input);

        var tokens = Tokenize(line);
        if (tokens.Count < 2)
            throw new FormatException("Proxy port is missing: " + input);

        var hostFirst = IsPort(tokens[1]);
        var credentialsFirst = tokens.Count >= 4 && IsPort(tokens[^1]);

        // "host:port:user:pass" and "user:pass:host:port" are both common and look alike,
        // so the side holding something host-shaped decides which one this is.
        if (hostFirst && LooksLikeHost(tokens[0]))
            return FromHostFirst(scheme, tokens, display, input);

        if (credentialsFirst && LooksLikeHost(tokens[^2]))
            return FromCredentialsFirst(scheme, tokens, display, input);

        if (hostFirst)
            return FromHostFirst(scheme, tokens, display, input);

        if (credentialsFirst)
            return FromCredentialsFirst(scheme, tokens, display, input);

        throw new FormatException("Unsupported proxy format: " + input);
    }

    private static ProxySpec ParseWithCredentialMarker(
        string scheme,
        string line,
        int at,
        string display,
        string input)
    {
        var left = line[..at];
        var right = line[(at + 1)..];

        // Both "user:pass@host:port" and "host:port@user:pass" are used.
        var endpoint = LooksLikeEndpoint(right) || !LooksLikeEndpoint(left) ? right : left;
        var credentials = ReferenceEquals(endpoint, right) ? left : right;

        var endpointTokens = Tokenize(endpoint);
        if (endpointTokens.Count < 2 || !IsPort(endpointTokens[1]))
            throw new FormatException("Proxy port is missing: " + input);

        var credentialTokens = Tokenize(credentials);
        var username = credentialTokens.Count > 0 ? credentialTokens[0] : null;
        var password = credentialTokens.Count > 1
            ? string.Join(':', credentialTokens.Skip(1))
            : null;

        return new ProxySpec(
            scheme,
            endpointTokens[0],
            ParsePort(endpointTokens[1], input),
            username,
            password,
            display);
    }

    private static ProxySpec FromHostFirst(string scheme, List<string> tokens, string display, string input) =>
        new(
            scheme,
            tokens[0],
            ParsePort(tokens[1], input),
            tokens.Count > 2 ? tokens[2] : null,
            tokens.Count > 3 ? string.Join(':', tokens.Skip(3)) : null,
            display);

    private static ProxySpec FromCredentialsFirst(string scheme, List<string> tokens, string display, string input) =>
        new(
            scheme,
            tokens[^2],
            ParsePort(tokens[^1], input),
            tokens[0],
            string.Join(':', tokens.Skip(1).Take(tokens.Count - 3)),
            display);

    private static List<string> Tokenize(string value) =>
        value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static bool LooksLikeEndpoint(string value)
    {
        var tokens = Tokenize(value);
        return tokens.Count >= 2 && IsPort(tokens[1]) && LooksLikeHost(tokens[0]);
    }

    /// <summary>
    /// Proxy hosts are IP addresses or domain names, so a dot (or a bare "localhost") is
    /// what separates them from a username sitting in the same position.
    /// </summary>
    private static bool LooksLikeHost(string value) =>
        value.Contains('.') || value.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPort(string value) =>
        int.TryParse(value.Trim(), out var port) && port is > 0 and <= 65535;

    private static string StripTrailingComment(string value)
    {
        // Only whitespace-delimited '#' starts a comment: passwords may contain it.
        var index = value.IndexOf(" #", StringComparison.Ordinal);
        if (index < 0)
            index = value.IndexOf("\t#", StringComparison.Ordinal);

        return index < 0 ? value : value[..index].TrimEnd();
    }

    private static int ParsePort(string value, string source)
    {
        if (int.TryParse(value.Trim(), out var port) && port is > 0 and <= 65535)
            return port;

        throw new FormatException("Invalid proxy port in: " + source);
    }

    /// <summary>
    /// Unreadable lines are skipped rather than aborting the whole run; only a file with no
    /// usable proxy at all is reported back to the user.
    /// </summary>
    public static List<ProxySpec> LoadFromFile(string path)
    {
        var result = new List<ProxySpec>();
        var rejected = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            try
            {
                result.Add(Parse(trimmed));
            }
            catch (FormatException)
            {
                if (rejected.Count < 5)
                    rejected.Add(trimmed);
            }
        }

        foreach (var line in rejected)
            AppLog.Warning("proxy", "Skipped unreadable proxy line: " + line);

        if (result.Count == 0)
        {
            var reason = rejected.Count > 0
                ? "No proxy could be read from " + path + ". Rejected lines:" +
                  Environment.NewLine + string.Join(Environment.NewLine, rejected)
                : "No proxy found in " + path + ". The file has no lines outside comments.";

            AppLog.Error("proxy", reason);
            throw new FormatException(reason);
        }

        return result;
    }
}
