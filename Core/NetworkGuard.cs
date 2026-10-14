using System.Text.RegularExpressions;
using MailKit.Net.Proxy;

namespace ZennoVibe.MailClient.Core;

/// <summary>
/// Refuse a mail or HTTP hop that has no proxy, so traffic cannot fall through
/// to the real NIC. This is a per-call check, not a global latch.
/// </summary>
internal static class NetworkGuard
{
    private static readonly Regex Ipv4 = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\b",
        RegexOptions.Compiled);

    public static void AssertProxy(ProxySpec? proxy)
    {
        if (proxy == null)
            throw new InvalidOperationException("Refused to connect without a proxy.");
    }

    public static void AssertProxies(IReadOnlyList<ProxySpec> proxies)
    {
        if (proxies.Count == 0)
            throw new InvalidOperationException("Refused to start: proxy list is empty.");
    }

    public static void AssertMailKitProxy(IProxyClient? mailKitProxy)
    {
        if (mailKitProxy == null)
            throw new InvalidOperationException("Refused a mail connection without a proxy client.");
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return Ipv4.Replace(text, "x.x.x.x");
    }
}
