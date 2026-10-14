using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

/// <summary>
/// Downloads the body of a single message that the sync stored as metadata only. This is what
/// lets a sync cost one command per folder instead of one per message.
/// </summary>
internal sealed class MessageBodyLoader : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private readonly TokenService _tokenService = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private ImapClient? _sessionClient;
    private IMailFolder? _sessionFolder;
    private int _sessionAccountId;
    private string _sessionFolderName = "";
    private CancellationTokenSource? _idleCancellation;

    public async Task<MessageBody> LoadAsync(
        MailDatabase db,
        AccountEntity account,
        MessageEntity message,
        IReadOnlyList<ProxySpec> proxies,
        int startProxyIndex,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        if (proxies.Count == 0)
            throw new InvalidOperationException("Proxy list is empty.");

        var folder = db.Folders.FindById(message.FolderId);
        if (folder == null)
            return MessageBody.Empty;

        var endpoints = MailSite.GetEndpoints(account.Site);
        progress?.Report(8);
        Exception? lastError = null;

        for (var attempt = 0; attempt < proxies.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proxy = proxies[(startProxyIndex + attempt) % proxies.Count];

            try
            {
                var body = await LoadWithProxyAsync(
                    db,
                    account,
                    message,
                    folder,
                    proxy,
                    endpoints,
                    cancellationToken,
                    progress);

                if (body.Text.Length > 0 || body.Html.Length > 0)
                    db.SaveMessageBody(message, body.Text, body.Html);

                progress?.Report(100);
                return body;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AppLog.Warning(
                    "body.fetch",
                    "Could not download the body of uid=" + message.Uid +
                    " for " + account.Email + " via " + proxy.Summary,
                    ex);
            }
        }

        throw lastError ?? new InvalidOperationException("Could not download the message body.");
    }

    private async Task<MessageBody> LoadWithProxyAsync(
        MailDatabase db,
        AccountEntity account,
        MessageEntity message,
        FolderEntity folderEntity,
        ProxySpec proxy,
        MailEndpoints endpoints,
        CancellationToken cancellationToken,
        IProgress<int>? progress)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            CancelIdleClose();
            await EnsureSessionAsync(db, account, proxy, endpoints, cancellationToken);
            progress?.Report(30);
            var folder = await EnsureFolderOpenAsync(folderEntity, cancellationToken);
            progress?.Report(48);
            var body = await FetchAsync(db, folder, message, cancellationToken, progress);
            ScheduleIdleClose();
            return body;
        }
        catch (OperationCanceledException)
        {
            if (_sessionClient is { IsConnected: true, IsAuthenticated: true })
                ScheduleIdleClose();
            else
                CloseSession();
            throw;
        }
        catch
        {
            CloseSession();
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task EnsureSessionAsync(
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        MailEndpoints endpoints,
        CancellationToken cancellationToken)
    {
        NetworkGuard.AssertProxy(proxy);

        if (_sessionClient is { IsConnected: true, IsAuthenticated: true } &&
            _sessionAccountId == account.Id)
        {
            return;
        }

        CloseSession();
        var appPassword = AuthRouter.IsAppPassword(account);
        if (!appPassword)
        {
            var token = await _tokenService.EnsureAccessTokenAsync(account, proxy, cancellationToken);
            TokenService.ApplyToAccount(account, token.AccessToken, token.RefreshToken, token.ExpiresAt);
            db.Accounts.Update(account);
        }

        foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
        {
            var client = new ImapClient { Timeout = 60_000, CheckCertificateRevocation = false };
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
                _sessionClient = client;
                _sessionAccountId = account.Id;
                return;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw;
            }
            catch
            {
                client.Dispose();
                continue;
            }
        }

        throw new InvalidOperationException("Could not connect to fetch the message body.");
    }

    private async Task<IMailFolder> EnsureFolderOpenAsync(
        FolderEntity folderEntity,
        CancellationToken cancellationToken)
    {
        var client = _sessionClient
            ?? throw new InvalidOperationException("The message session is not connected.");

        if (_sessionFolder is { IsOpen: true } &&
            string.Equals(_sessionFolderName, folderEntity.FullName, StringComparison.Ordinal))
        {
            return _sessionFolder;
        }

        if (_sessionFolder?.IsOpen == true)
            await _sessionFolder.CloseAsync(false, cancellationToken);

        var folder = string.IsNullOrEmpty(folderEntity.FullName)
            ? client.GetFolder(client.PersonalNamespaces[0])
            : await client.GetFolderAsync(folderEntity.FullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        _sessionFolder = folder;
        _sessionFolderName = folderEntity.FullName;
        return folder;
    }

    private static async Task<MessageBody> FetchAsync(
        MailDatabase db,
        IMailFolder folder,
        MessageEntity message,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        var uid = new UniqueId(message.Uid);
        // References rides along for rows synced before the thread chain was stored.
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure;
        if (message.References.Count == 0)
            items |= MessageSummaryItems.References;

        var summaries = await folder.FetchAsync(new[] { uid }, items, cancellationToken);

        var summary = summaries.FirstOrDefault();
        if (summary == null)
            return MessageBody.Empty;

        message.AttachmentFileNames = ImapSyncService.GetAttachmentNames(summary);
        message.HasAttachments = message.AttachmentFileNames.Count > 0;
        message.AttachmentInfoKnown = true;
        if (message.References.Count == 0)
            message.References = ImapSyncService.ReadReferences(summary);
        db.Messages.Update(message);
        progress?.Report(58);

        // Older databases may already have a body but no attachment metadata.
        if (message.BodyLoaded)
            return db.LoadMessageBody(message.Id);

        var (text, html) = await ImapSyncService.LoadMessageBodiesOnceAsync(
            folder,
            summary,
            cancellationToken,
            progress);

        if (text.Length > 0 || html.Length > 0)
            return new MessageBody(text, html);

        var full = await ImapSyncService.LoadFullMessageBodiesAsync(folder, summary, cancellationToken, progress);
        if (full.Text.Length == 0 && full.Html.Length == 0)
        {
            // Attachment-only messages legitimately have no readable body. Remember
            // that inspection completed so selecting them does not download them again.
            message.BodyLoaded = true;
            db.Messages.Update(message);
        }

        return new MessageBody(full.Text, full.Html);
    }

    private void ScheduleIdleClose()
    {
        CancelIdleClose();
        var idle = new CancellationTokenSource();
        _idleCancellation = idle;
        _ = CloseAfterIdleAsync(idle);
    }

    private async Task CloseAfterIdleAsync(CancellationTokenSource idle)
    {
        try
        {
            await Task.Delay(IdleTimeout, idle.Token);
            await _sessionGate.WaitAsync(idle.Token);
            try
            {
                if (ReferenceEquals(_idleCancellation, idle))
                {
                    _idleCancellation = null;
                    CloseSession();
                }
            }
            finally
            {
                _sessionGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A new message reused the connection or the application is closing.
        }
        finally
        {
            idle.Dispose();
        }
    }

    private void CancelIdleClose()
    {
        var idle = _idleCancellation;
        _idleCancellation = null;
        idle?.Cancel();
    }

    private void CloseSession()
    {
        var client = _sessionClient;
        _sessionClient = null;
        _sessionFolder = null;
        _sessionFolderName = "";
        _sessionAccountId = 0;

        if (client == null)
            return;

        try
        {
            if (client.IsConnected)
                client.Disconnect(false);
        }
        catch
        {
            // The session is already unusable.
        }
        finally
        {
            client.Dispose();
        }
    }

    public void Dispose()
    {
        CancelIdleClose();

        // Called from the UI thread while the window closes. A fetch stuck on a dead proxy
        // must not freeze the shutdown; after a short grace period the socket is torn down
        // under it, which faults that fetch but lets the process exit.
        var acquired = _sessionGate.Wait(TimeSpan.FromSeconds(3));
        try
        {
            CloseSession();
        }
        finally
        {
            if (acquired)
                _sessionGate.Release();

            try
            {
                _sessionGate.Dispose();
            }
            catch
            {
                // Disposing while a late waiter is attached is harmless at shutdown.
            }
        }
    }
}
