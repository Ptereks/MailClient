using System.Globalization;
using System.Net;
using System.Text;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient;

/// <summary>The quoted original in both forms the outgoing message carries.</summary>
/// <param name="Text">For the text/plain part; lines carry the usual "&gt; " prefix.</param>
/// <param name="Html">For the text/html part; the webmail "quoted" container, ready to append after the written body.</param>
internal sealed record QuoteParts(string Text, string Html);

/// <summary>
/// The quoted original under a reply, laid out the way the account's own webmail does it:
/// Yahoo Mail writes one "On …, … wrote:" line, AOL Mail writes an "-----Original Message-----"
/// block. The markup is the Yahoo Mail platform's (AOL Mail runs on it), with the AOL header.
/// </summary>
internal static class ReplyQuote
{
    private const int MaxQuotedChars = 4000;

    public static QuoteParts Build(string site, MessageEntity message, string originalText, string replyingAccountEmail)
    {
        var quoted = originalText.Replace("\r\n", "\n").Trim();
        if (quoted.Length > MaxQuotedChars)
            quoted = quoted[..MaxQuotedChars] + "\n[...]";

        var lines = quoted.Split('\n');
        var from = (message.From ?? "").Trim();
        var to = string.IsNullOrWhiteSpace(message.To) ? replyingAccountEmail : message.To.Trim();
        var subject = (message.Subject ?? "").Trim();

        string[] header = site == "aol"
            ? new[]
            {
                "-----Original Message-----",
                "From: " + from,
                "To: " + to,
                "Sent: " + message.DateUtc.ToString("ddd, MMM d, yyyy h:mm ", CultureInfo.InvariantCulture) +
                message.DateUtc.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant(),
                "Subject: " + subject
            }
            : new[]
            {
                "On " + message.DateUtc.ToString("dddd, MMMM d, yyyy 'at' hh:mm:ss tt", CultureInfo.InvariantCulture) +
                " GMT, " + from + " wrote:"
            };

        return new QuoteParts(BuildText(header, lines), BuildHtml(header, lines));
    }

    private static string BuildText(string[] header, string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in header)
            sb.AppendLine(line);
        sb.AppendLine();
        sb.Append(string.Join(Environment.NewLine, lines.Select(line => "> " + line)));
        return sb.ToString();
    }

    private static string BuildHtml(string[] header, string[] lines)
    {
        var sb = new StringBuilder();
        sb.Append("<div id=\"yahoo_quoted_").Append(HtmlIds.Digits(10)).Append("\" class=\"yahoo_quoted\">");
        sb.Append("<div style=\"font-family:'Helvetica Neue', Helvetica, Arial, sans-serif;font-size:13px;color:#26282a;\">");
        foreach (var line in header)
            sb.Append("<div>").Append(WebUtility.HtmlEncode(line)).Append("</div>");
        sb.Append("<div><br></div><div><br></div>");
        sb.Append("<div><div id=\"yiv").Append(HtmlIds.Digits(10)).Append("\">");
        foreach (var line in lines)
            sb.Append("<div>").Append(line.Length == 0 ? "<br>" : WebUtility.HtmlEncode(line)).Append("</div>");
        sb.Append("</div></div></div></div>");
        return sb.ToString();
    }
}

/// <summary>Random ids of the shape webmail generates for its containers.</summary>
internal static class HtmlIds
{
    public static string Digits(int count)
    {
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
            sb.Append((char)('0' + Random.Shared.Next(10)));
        return sb.ToString();
    }

    public static string Hex(int count)
    {
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
            sb.Append("0123456789abcdef"[Random.Shared.Next(16)]);
        return sb.ToString();
    }
}
