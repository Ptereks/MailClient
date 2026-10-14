using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using System.IO;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

/// <summary>Downloads attachments to disk without opening or rendering their contents.</summary>
internal sealed class AttachmentDownloadService
{
    private readonly TokenService _tokenService = new();

    public static string GetAccountDirectory(AccountEntity account)
    {
        var name = account.Email.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        if (string.IsNullOrWhiteSpace(name))
            name = "unknown-account";

        return Path.Combine(AppPaths.DataDirectory, "attachments", name);
    }

    public async Task<AttachmentDownloadResult> DownloadAsync(
        MailDatabase db,
        AccountEntity account,
        MessageEntity message,
        IReadOnlyList<ProxySpec> proxies,
        int startProxyIndex,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (proxies.Count == 0)
            throw new InvalidOperationException("Proxy list is empty.");

        Directory.CreateDirectory(destinationDirectory);
        var folder = db.Folders.FindById(message.FolderId)
            ?? throw new InvalidOperationException("The message folder no longer exists.");
        var endpoints = MailSite.GetEndpoints(account.Site);
        Exception? lastError = null;

        for (var attempt = 0; attempt < proxies.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proxy = proxies[(startProxyIndex + attempt) % proxies.Count];

            try
            {
                return await DownloadWithProxyAsync(
                    db,
                    account,
                    message,
                    folder,
                    proxy,
                    endpoints,
                    destinationDirectory,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Could not download the attachments.");
    }

    private async Task<AttachmentDownloadResult> DownloadWithProxyAsync(
        MailDatabase db,
        AccountEntity account,
        MessageEntity message,
        FolderEntity folderEntity,
        ProxySpec proxy,
        MailEndpoints endpoints,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        NetworkGuard.AssertProxy(proxy);

        var appPassword = AuthRouter.IsAppPassword(account);
        if (!appPassword)
        {
            var token = await _tokenService.EnsureAccessTokenAsync(account, proxy, cancellationToken);
            TokenService.ApplyToAccount(account, token.AccessToken, token.RefreshToken, token.ExpiresAt);
            db.Accounts.Update(account);
        }

        foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
        {
            using var client = new ImapClient { Timeout = 120_000, CheckCertificateRevocation = false };

            try
            {
                client.ProxyClient = mailKitProxy;
                await client.ConnectAsync(
                    endpoints.ImapHost,
                    endpoints.ImapPort,
                    SecureSocketOptions.SslOnConnect,
                    cancellationToken);
                if (appPassword)
                {
                    await AppPasswordAuthenticator.AuthenticateAsync(
                        client,
                        account.Email,
                        account.Password,
                        cancellationToken);
                }
                else
                {
                    await OAuthAuthenticator.AuthenticateAsync(
                        client,
                        account.Email,
                        account.AccessToken,
                        cancellationToken);
                }
                proxy.RememberWorkingScheme(mailKitProxy);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            try
            {
                return await SaveAttachmentsAsync(
                    client,
                    message,
                    folderEntity,
                    destinationDirectory,
                    cancellationToken);
            }
            finally
            {
                try
                {
                    if (client.IsConnected)
                        await client.DisconnectAsync(true, cancellationToken);
                }
                catch
                {
                    // Saved files are already complete; a failed goodbye does not matter.
                }
            }
        }

        throw new InvalidOperationException("Could not connect to download the attachments.");
    }

    private static async Task<AttachmentDownloadResult> SaveAttachmentsAsync(
        ImapClient client,
        MessageEntity message,
        FolderEntity folderEntity,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var folder = string.IsNullOrEmpty(folderEntity.FullName)
            ? client.GetFolder(client.PersonalNamespaces[0])
            : await client.GetFolderAsync(folderEntity.FullName, cancellationToken);

        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        try
        {
            var uid = new UniqueId(message.Uid);
            var summary = (await folder.FetchAsync([uid], MessageSummaryItems.Size, cancellationToken))
                .FirstOrDefault();
            if (summary?.Size is { } size && size > MaxMessageBytes)
            {
                throw new InvalidOperationException(
                    $"The message is {size / (1024 * 1024)} MB; attachments larger than " +
                    $"{MaxMessageBytes / (1024 * 1024)} MB are not downloaded.");
            }

            var mimeMessage = await folder.GetMessageAsync(uid, cancellationToken);
            var paths = new List<string>();
            var index = 0;

            foreach (var attachment in mimeMessage.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                var messagePart = attachment as MessagePart;
                var mimePart = attachment as MimePart;
                if (messagePart?.Message == null && mimePart?.Content == null)
                    continue;

                var suggestedName = attachment.ContentDisposition?.FileName
                    ?? attachment.ContentType.Name
                    ?? (attachment is MessagePart ? $"attachment-{index}.eml" : $"attachment-{index}.bin");
                var path = UniquePath(destinationDirectory, SanitizeFileName(suggestedName, index));

                await using (var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    if (messagePart?.Message is { } embeddedMessage)
                    {
                        await embeddedMessage.WriteToAsync(stream, cancellationToken);
                    }
                    else if (mimePart?.Content is { } content)
                    {
                        await content.DecodeToAsync(stream, cancellationToken);
                    }
                }

                MarkAsFromInternet(path);
                paths.Add(path);
            }

            return new AttachmentDownloadResult(paths);
        }
        finally
        {
            try
            {
                if (folder.IsOpen)
                    await folder.CloseAsync(false, cancellationToken);
            }
            catch
            {
                // Ignore close errors after the files have been saved.
            }
        }
    }

    /// <summary>
    /// Yahoo and AOL cap outgoing messages at 25 MB, so anything far above that is not a real
    /// attachment but a way to exhaust memory: GetMessageAsync buffers the whole MIME tree.
    /// </summary>
    private const uint MaxMessageBytes = 200 * 1024 * 1024;

    /// <summary>
    /// Mark-of-the-Web. Without this stream Windows treats the file as locally created, so
    /// SmartScreen stays silent on an .exe and Office opens a .docm outside Protected View.
    /// Best effort: FAT/exFAT volumes have no alternate data streams.
    /// </summary>
    private static void MarkAsFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch
        {
            // File system without ADS support; the attachment itself is already saved.
        }
    }

    private const int MaxFileNameLength = 150;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// The sender chooses the attachment name, so it is treated as hostile: path separators,
    /// control characters, reserved device names, trailing dots and spaces (which Windows
    /// silently strips, letting "report.pdf." shadow "report.pdf") and absurd lengths are all
    /// normalised before the name touches the file system.
    /// </summary>
    internal static string SanitizeFileName(string value, int index)
    {
        var fallback = $"attachment-{index}.bin";
        var name = (value ?? "").Trim();

        // Both separators explicitly: Path.GetFileName only knows the platform's own.
        var cut = name.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
            name = name[(cut + 1)..];

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(char.IsControl(ch) || Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        name = builder.ToString().TrimEnd('.', ' ').TrimStart(' ');

        if (name.Length == 0 || name.All(c => c == '.' || c == '_'))
            return fallback;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        if (ReservedNames.Contains(stem) || ReservedNames.Contains(name))
            name = "_" + name;

        if (name.Length > MaxFileNameLength)
        {
            extension = extension.Length > 16 ? "" : extension;
            stem = Path.GetFileNameWithoutExtension(name);
            name = stem[..Math.Max(1, MaxFileNameLength - extension.Length)] + extension;
        }

        return name;
    }

    private static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}

internal sealed record AttachmentDownloadResult(IReadOnlyList<string> FilePaths)
{
    public int Count => FilePaths.Count;
}
