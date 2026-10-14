using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

/// <summary>What the compose pane or the reply window hands to the sender.</summary>
/// <param name="Body">What the user wrote, plain text.</param>
/// <param name="Quote">The quoted original for a reply; null for a new mail.</param>
/// <param name="InReplyTo">Message-Id of the message being answered, without angle brackets; null for a new mail.</param>
/// <param name="References">The thread so far: the original's References plus its own Message-Id, oldest first.</param>
/// <param name="AttachmentPaths">Files to attach; checked against the 25 MB limit by the UI.</param>
internal sealed record OutgoingMail(
    IReadOnlyList<MailboxAddress> Recipients,
    string Subject,
    string Body,
    QuoteParts? Quote,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<string> AttachmentPaths)
{
    public static OutgoingMail NewMail(IReadOnlyList<MailboxAddress> recipients, string subject, string body, IReadOnlyList<string> attachments) =>
        new(recipients, subject, body, null, null, Array.Empty<string>(), attachments);
}

internal sealed class SmtpSendService
{
    private readonly TokenService _tokenService = new();

    /// <summary>
    /// Accepts "a@x.com, b@y.com; Name &lt;c@z.com&gt;" and similar; the separators comma and
    /// semicolon are both allowed because people paste both.
    /// </summary>
    public static bool TryParseRecipients(
        string input,
        out IReadOnlyList<MailboxAddress> recipients,
        out string error)
    {
        recipients = Array.Empty<MailboxAddress>();
        error = "";

        var normalized = (input ?? "").Replace(';', ',').Trim();
        if (normalized.Length == 0)
        {
            error = "Enter at least one recipient address.";
            return false;
        }

        if (!InternetAddressList.TryParse(normalized, out var list))
        {
            error = "Recipient list could not be parsed. Separate addresses with commas.";
            return false;
        }

        var mailboxes = list.Mailboxes.ToList();
        if (mailboxes.Count == 0 || mailboxes.Any(m => string.IsNullOrEmpty(m.Address) || !m.Address.Contains('@')))
        {
            error = "Every recipient must be an email address.";
            return false;
        }

        recipients = mailboxes;
        return true;
    }

    /// <summary>Pulls the bare address out of a From header such as "Name &lt;user@host&gt;".</summary>
    public static string ExtractAddress(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return "";

        if (MailboxAddress.TryParse(from, out var mailbox) && !string.IsNullOrEmpty(mailbox.Address))
            return mailbox.Address;

        var open = from.LastIndexOf('<');
        var close = from.LastIndexOf('>');
        if (open >= 0 && close > open)
            return from[(open + 1)..close].Trim();

        return from.Contains('@') ? from.Trim() : "";
    }

    public async Task SendAsync(
        MailDatabase db,
        AccountEntity account,
        IReadOnlyList<ProxySpec> proxies,
        int proxyStartIndex,
        OutgoingMail mail,
        CancellationToken cancellationToken)
    {
        NetworkGuard.AssertProxies(proxies);

        if (proxies.Count == 0)
            throw new InvalidOperationException("Proxy list is empty.");
        if (mail.Recipients.Count == 0)
            throw new InvalidOperationException("No recipients.");

        var endpoints = MailSite.GetEndpoints(account.Site);
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(account.Email));
        message.To.AddRange(mail.Recipients);
        message.Subject = mail.Subject;
        // text + html alternative, attachments in multipart/mixed — the shape webmail sends.
        message.Body = OutgoingMessageBuilder.Build(account, mail);
        // MimeKit's default Message-ID carries the Windows computer name and the Date carries
        // this machine's time zone; both would be the same for every account.
        message.MessageId = MimeUtils.GenerateMessageId(MailIdentity.MessageIdDomain(account.Email));
        message.Date = DateTimeOffset.UtcNow;
        ApplyThreading(message, mail.InReplyTo, mail.References);

        if (AuthRouter.IsAppPassword(account))
        {
            await SendWithAppPasswordAsync(db, account, proxies, proxyStartIndex, message, cancellationToken);
            return;
        }

        Exception? lastError = null;
        var proxyCount = proxies.Count;
        var authRetried = false;

        // One extra pass is allowed so a token refreshed after an auth failure still gets
        // used, even when the proxy file holds a single entry.
        for (var attempt = 0; attempt <= proxyCount; attempt++)
        {
            if (attempt == proxyCount && !authRetried)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            var proxy = proxies[(proxyStartIndex + attempt) % proxyCount];

            // The token refresh goes through the same proxy as the SMTP connection: if this
            // proxy is dead the refresh fails here and the loop moves on instead of aborting
            // the whole send on the very first line of the proxy file.
            try
            {
                await RefreshAccountTokenAsync(db, account, proxy, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AppLog.Warning(
                    "smtp",
                    "Token refresh failed for " + account.Email + " via " + proxy.Summary,
                    ex);
                continue;
            }

            foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
            {
                using var client = new SmtpClient
                {
                    Timeout = 30_000,
                    // EHLO would otherwise announce the Windows computer name.
                    LocalDomain = MailIdentity.EhloName(account.Email),
                    // Revocation lookups are made by Windows itself, outside the proxy.
                    CheckCertificateRevocation = false
                };

                try
                {
                    client.ProxyClient = mailKitProxy;
                    await client.ConnectAsync(
                        endpoints.SmtpHost,
                        endpoints.SmtpPort,
                        SecureSocketOptions.SslOnConnect,
                        cancellationToken);
                    await OAuthAuthenticator.AuthenticateAsync(
                        client,
                        account.Email,
                        account.AccessToken,
                        cancellationToken);
                    await client.SendAsync(message, cancellationToken);
                    await client.DisconnectAsync(true, cancellationToken);

                    if (string.IsNullOrWhiteSpace(account.ActiveProxy))
                        account.ActiveProxy = proxy.Display;
                    db.Accounts.Update(account);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AuthenticationException ex)
                {
                    lastError = ex;
                    AppLog.Warning(
                        "smtp",
                        "Auth rejected for " + account.Email + " via " + proxy.Summary +
                        " (retry used: " + authRetried + ")",
                        ex);

                    // A stored access token can be rejected even when it looks fresh, so
                    // force one refresh before giving up on the remaining proxies.
                    if (authRetried)
                        continue;

                    authRetried = true;
                    TokenService.InvalidateAccessToken(account);
                    db.Accounts.Update(account);
                    await RefreshAccountTokenAsync(db, account, proxy, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AppLog.Warning(
                        "smtp",
                        "Send failed for " + account.Email + " via " + proxy.Summary,
                        ex);
                }
            }
        }

        AppLog.Error("smtp", "All proxy attempts failed sending from " + account.Email, lastError);
        throw lastError ?? new InvalidOperationException("SMTP send failed for " + account.Email);
    }

    private static async Task SendWithAppPasswordAsync(
        MailDatabase db,
        AccountEntity account,
        IReadOnlyList<ProxySpec> proxies,
        int proxyStartIndex,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        var endpoints = MailSite.GetEndpoints(account.Site);
        Exception? lastError = null;
        var proxyCount = proxies.Count;

        for (var attempt = 0; attempt < proxyCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proxy = proxies[(proxyStartIndex + attempt) % proxyCount];

            foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
            {
                using var client = new SmtpClient
                {
                    Timeout = 30_000,
                    LocalDomain = MailIdentity.EhloName(account.Email),
                    CheckCertificateRevocation = false
                };

                try
                {
                    client.ProxyClient = mailKitProxy;
                    await client.ConnectAsync(
                        endpoints.SmtpHost,
                        endpoints.SmtpPort,
                        SecureSocketOptions.SslOnConnect,
                        cancellationToken);
                    await AppPasswordAuthenticator.AuthenticateAsync(
                        client,
                        account.Email,
                        account.Password,
                        cancellationToken);
                    await client.SendAsync(message, cancellationToken);
                    await client.DisconnectAsync(true, cancellationToken);

                    if (string.IsNullOrWhiteSpace(account.ActiveProxy))
                        account.ActiveProxy = proxy.Display;
                    db.Accounts.Update(account);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AppLog.Warning(
                        "smtp",
                        "App password send failed for " + account.Email + " via " + proxy.Summary,
                        ex);
                }
            }
        }

        AppLog.Error("smtp", "All proxy attempts failed sending from " + account.Email, lastError);
        throw lastError ?? new InvalidOperationException("SMTP send failed for " + account.Email);
    }

    /// <summary>
    /// In-Reply-To names the message being answered; References carries the whole thread —
    /// the original's own References followed by its Message-Id — so every client files the
    /// reply into the same conversation.
    /// </summary>
    private static void ApplyThreading(MimeMessage message, string? inReplyTo, IReadOnlyList<string> references)
    {
        if (string.IsNullOrWhiteSpace(inReplyTo))
            return;

        try
        {
            message.InReplyTo = inReplyTo;
            foreach (var id in references)
            {
                if (!string.IsNullOrWhiteSpace(id) && !message.References.Contains(id))
                    message.References.Add(id);
            }

            if (!message.References.Contains(inReplyTo))
                message.References.Add(inReplyTo);
        }
        catch (Exception)
        {
            // A malformed stored Message-Id only costs the threading headers, not the send.
            message.InReplyTo = null;
            message.References.Clear();
        }
    }

    private async Task RefreshAccountTokenAsync(
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        CancellationToken cancellationToken)
    {
        var token = await _tokenService.EnsureAccessTokenAsync(account, proxy, cancellationToken);
        if (token.AccessToken == account.AccessToken &&
            token.RefreshToken == account.RefreshToken &&
            token.ExpiresAt == account.AccessTokenExpiresAt)
        {
            return;
        }

        TokenService.ApplyToAccount(account, token.AccessToken, token.RefreshToken, token.ExpiresAt);
        db.Accounts.Update(account);
    }
}
