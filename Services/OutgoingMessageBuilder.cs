using System.IO;
using System.Net;
using System.Text;
using MimeKit;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

/// <summary>
/// Builds the MIME body of an outgoing message the way the account's webmail would:
/// multipart/alternative with a text and an HTML part, wrapped in multipart/mixed when files
/// are attached. The HTML markup follows the Yahoo Mail platform, which AOL Mail also runs on.
/// </summary>
internal static class OutgoingMessageBuilder
{
    /// <summary>Yahoo and AOL reject messages above 25 MB; the limit applies to the attachments together.</summary>
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    public static MimeEntity Build(AccountEntity account, OutgoingMail mail)
    {
        var builder = new BodyBuilder
        {
            TextBody = TextBody(mail),
            HtmlBody = HtmlBody(mail)
        };

        foreach (var path in mail.AttachmentPaths)
            builder.Attachments.Add(path);

        return builder.ToMessageBody();
    }

    public static string TextBody(OutgoingMail mail)
    {
        var written = mail.Body.Replace("\r\n", "\n").TrimEnd();
        if (mail.Quote == null)
            return written.Replace("\n", "\r\n");

        return (written + "\n\n" + mail.Quote.Text.Replace("\r\n", "\n")).Replace("\n", "\r\n");
    }

    public static string HtmlBody(OutgoingMail mail)
    {
        var sb = new StringBuilder();
        sb.Append("<html><head></head><body>");
        sb.Append("<div class=\"ydp").Append(HtmlIds.Hex(8)).Append("yahoo-style-wrap\" ");
        sb.Append("style=\"font-family:Helvetica Neue, Helvetica, Arial, sans-serif;font-size:16px;\">");
        sb.Append("<div></div>");

        foreach (var line in mail.Body.Replace("\r\n", "\n").TrimEnd().Split('\n'))
        {
            sb.Append("<div dir=\"ltr\" data-setdir=\"false\">")
              .Append(line.Length == 0 ? "<br>" : WebUtility.HtmlEncode(line))
              .Append("</div>");
        }

        sb.Append("<div dir=\"ltr\" data-setdir=\"false\"><br></div>");
        sb.Append("</div>");

        if (mail.Quote != null)
            sb.Append(mail.Quote.Html);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Sum of the files' sizes; a file that vanished counts as zero and fails later at send.</summary>
    public static long TotalSize(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch
            {
                // Reported when the message is built.
            }
        }

        return total;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB"
            : bytes >= 1024
                ? (bytes / 1024.0).ToString("0") + " KB"
                : bytes + " B";
}
