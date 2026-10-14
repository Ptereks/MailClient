using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient;

internal static partial class MessageBodyDocument
{
    /// <summary>WebView2 <c>NavigateToString</c> rejects very large documents.</summary>
    private const int MaxNavigateChars = 1_800_000;

    /// <summary>
    /// Defence in depth behind the WebView2 request filter: nothing in the document may
    /// execute, submit or navigate. Inline images and styles remain allowed. Remote media
    /// is allowed only when the setting is on; the viewer still serves those bytes itself
    /// through the account proxy.
    /// </summary>
    private const string SealedContentSecurityPolicy =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; " +
        "img-src data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; " +
        "frame-src 'none'; base-uri 'none'\">";

    private const string MediaContentSecurityPolicy =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; " +
        "img-src data: http: https:; media-src data: http: https:; font-src data: http: https:; " +
        "style-src 'unsafe-inline' http: https:; form-action 'none'; frame-src 'none'; " +
        "connect-src 'none'; script-src 'none'; base-uri 'none'\">";

    public static string Build(MessageEntity message, MessageBody body, bool loadMedia = false)
    {
        var policy = loadMedia ? MediaContentSecurityPolicy : SealedContentSecurityPolicy;

        if (!string.IsNullOrWhiteSpace(body.Html))
        {
            var html = SanitizeHtml(Sanitize(body.Html)).Trim();
            if (html.Contains("<html", StringComparison.OrdinalIgnoreCase))
                return LimitSize(InjectPolicy(html, policy));

            return LimitSize(WrapFragment(html, policy));
        }

        var text = ResolvePlainText(message, body);
        var encoded = WebUtility.HtmlEncode(text);
        return LimitSize(WrapFragment("<pre>" + encoded + "</pre>", policy));
    }

    /// <summary>Plain text for the fallback viewer when WebView2 cannot render the message.</summary>
    public static string ResolvePlainText(MessageEntity message, MessageBody body)
    {
        if (!string.IsNullOrWhiteSpace(body.Text))
            return Sanitize(body.Text);

        if (!string.IsNullOrWhiteSpace(body.Html))
            return Sanitize(StripHtml(body.Html));

        if (!string.IsNullOrWhiteSpace(message.Preview))
            return Sanitize(message.Preview);

        return "(This message has no readable body. Try syncing the account again.)";
    }

    /// <summary>
    /// Also used by the sync to build a preview and search text from an HTML-only message,
    /// so the plain-text part never has to be downloaded as well.
    /// </summary>
    public static string StripHtml(string html)
    {
        // Style and script bodies are not the letter. Leaving them made the preview a wall of CSS.
        html = StyleScriptBlockPattern().Replace(html, " ");

        // Good enough for a plain-text fallback; the WebView path keeps real HTML.
        var text = html
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</div>", "\n", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder(text.Length);
        var insideTag = false;

        foreach (var ch in text)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
                sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // WebView and HTML encoding both choke on NUL bytes; some servers send them in plain text.
        return value.Replace('\0', ' ');
    }

    /// <summary>
    /// Removes the elements and attributes that can execute, embed, submit or redirect.
    /// Regex-based on purpose: mail HTML is rarely well-formed enough for a strict parser,
    /// and the hard guarantee is the request filter in the viewer, not this pass.
    /// </summary>
    internal static string SanitizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";

        html = ActiveContainerPattern().Replace(html, "");
        html = ForbiddenTagPattern().Replace(html, "");
        html = EventHandlerPattern().Replace(html, "");
        html = ScriptUrlPattern().Replace(html, "$1=\"#\"");
        return html;
    }

    private static string InjectPolicy(string html, string policy)
    {
        var head = HeadOpenPattern().Match(html);
        if (head.Success)
            return html.Insert(head.Index + head.Length, policy);

        var root = HtmlOpenPattern().Match(html);
        return root.Success
            ? html.Insert(root.Index + root.Length, "<head>" + policy + "</head>")
            : policy + html;
    }

    [GeneratedRegex(
        @"<\s*(style|script)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleScriptBlockPattern();

    // Elements whose content must go with them.
    [GeneratedRegex(
        @"<\s*(script|iframe|frame|object|embed|applet|noscript|template|svg)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ActiveContainerPattern();

    // Self-closing or unclosed variants of the above plus tags that navigate or submit.
    // <link> is included: rel=preconnect / dns-prefetch / prefetch open connections that
    // never pass through the WebResourceRequested filter.
    [GeneratedRegex(
        @"<\s*/?\s*(script|iframe|frame|frameset|object|embed|applet|form|input|button|select|textarea|meta|base|link|noscript|template|svg|math)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTagPattern();

    [GeneratedRegex(
        @"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerPattern();

    [GeneratedRegex(
        @"(href|src|action|formaction|xlink:href|poster|background)\s*=\s*[""']?\s*(javascript|vbscript|data:text/html)[^""'\s>]*[""']?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrlPattern();

    [GeneratedRegex(@"<\s*head\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpenPattern();

    [GeneratedRegex(@"<\s*html\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpenPattern();

    private static string LimitSize(string html)
    {
        if (html.Length <= MaxNavigateChars)
            return html;

        AppLog.Warning(
            "message.view",
            "Message HTML was truncated for WebView2 (" + html.Length + " chars).");

        return html[..MaxNavigateChars] +
               "<hr><p><em>[Message truncated — open the log file if this keeps happening.]</em></p></body></html>";
    }

    private static string WrapFragment(string bodyHtml, string policy) =>
        """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        """ + policy + """

        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          html, body { margin: 0; padding: 0; background: #ffffff; color: #1a1a1a; }
          body { font-family: Segoe UI, Arial, sans-serif; font-size: 14px; line-height: 1.5; padding: 16px 20px; }
          img { max-width: 100%; height: auto; }
          table { max-width: 100%; }
          pre { white-space: pre-wrap; word-wrap: break-word; font-family: Consolas, Segoe UI, monospace; }
          a { color: #1a73e8; }
        </style>
        </head>
        <body>
        """ + bodyHtml + """
        </body>
        </html>
        """;
}
