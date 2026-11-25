using System.Diagnostics;
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
/// <remarks>
/// One IMAP session per account is kept open between clicks. Every step that is not strictly
/// needed to show the text has been taken off the click path: the access token is used as
/// stored (the frozen <see cref="TokenService"/> refreshes it only when it has expired),
/// folders are resolved once per session, and a message whose part layout the sync recorded
/// is fetched with a single command.
/// </remarks>
internal sealed class MessageBodyLoader : IDisposable
{
    /// <summary>
    /// Yahoo and AOL keep an idle IMAP connection for roughly half an hour. Closing ours
    /// earlier only makes the next click pay for connect, TLS and authentication again.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    private readonly TokenService _tokenService = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Dictionary<string, IMailFolder> _sessionFolders = new(StringComparer.Ordinal);
    private ImapClient? _sessionClient;
    private IMailFolder? _sessionFolder;
    private int _sessionAccountId;
    private string _sessionFolderName = "";
    private CancellationTokenSource? _idleCancellation;

    /// <summary>True when the next body of this account can go over an already open connection.</summary>
    public bool HasLiveSession(int accountId) =>
        _sessionClient is { IsConnected: true, IsAuthenticated: true } &&
        _sessionAccountId == accountId;

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

        if (TryUseStored(db, message, out var alreadyStored))
        {
            progress?.Report(100);
            return alreadyStored;
        }

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

    /// <summary>
    /// Opens the connection (and, when known, the folder) before the user clicks a message,
    /// so the first body of a freshly selected account is served from a warm session. Best
    /// effort: a busy loader or a failed connect is simply skipped.
    /// </summary>
    public async Task WarmUpAsync(
        MailDatabase db,
        AccountEntity account,
        FolderEntity? folder,
        IReadOnlyList<ProxySpec> proxies,
        int startProxyIndex,
        CancellationToken cancellationToken)
    {
        if (proxies.Count == 0)
            return;

        if (!await _sessionGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            CancelIdleClose();
            if (HasLiveSession(account.Id) &&
                (folder == null || string.Equals(_sessionFolderName, folder.FullName, StringComparison.Ordinal)))
            {
                ScheduleIdleClose();
                return;
            }

            var endpoints = MailSite.GetEndpoints(account.Site);
            var proxy = proxies[startProxyIndex % proxies.Count];
            var watch = Stopwatch.StartNew();
            await EnsureSessionAsync(db, account, proxy, endpoints, cancellationToken);
            if (folder != null)
                await EnsureFolderOpenAsync(folder, cancellationToken);

            AppLog.Notice(
                "body.session",
                "Warm session for " + account.Email +
                (folder != null ? " '" + folder.FullName + "'" : "") +
                " ready in " + watch.ElapsedMilliseconds + "ms via " + proxy.Summary);
            ScheduleIdleClose();
        }
        catch (OperationCanceledException)
        {
            CloseSession();
        }
        catch (Exception ex)
        {
            CloseSession();
            AppLog.Warning("body.session", "Warm-up for " + account.Email + " failed.", ex);
        }
        finally
        {
            _sessionGate.Release();
        }
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

            // Several prefetch queues and a click can line up behind the gate for the same
            // message; whoever went first has already stored it, so the rest skip the wire.
            if (TryUseStored(db, message, out var storedMeanwhile))
                return storedMeanwhile;

            var reused = HasLiveSession(account.Id);

            for (var pass = 0; ; pass++)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await EnsureSessionAsync(db, account, proxy, endpoints, cancellationToken);
                    var connectMs = watch.ElapsedMilliseconds;
                    progress?.Report(30);

                    var folder = await EnsureFolderOpenAsync(folderEntity, cancellationToken);
                    var folderMs = watch.ElapsedMilliseconds - connectMs;
                    progress?.Report(48);

                    var body = await FetchAsync(db, folder, message, cancellationToken, progress);

                    AppLog.Notice(
                        "body.fetch",
                        account.Email + " uid=" + message.Uid + " '" + folderEntity.FullName + "': " +
                        (reused ? "reused session" : "new session") +
                        (message.BodyPartsKnown ? ", direct part" : ", via structure") +
                        " | connect=" + connectMs + "ms folder=" + folderMs +
                        "ms fetch=" + (watch.ElapsedMilliseconds - connectMs - folderMs) +
                        "ms text=" + body.Text.Length + " html=" + body.Html.Length);

                    ScheduleIdleClose();
                    return body;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (reused && pass == 0 && ex is not AuthenticationException)
                {
                    // The server or proxy dropped the idle socket without telling us. One
                    // reconnect on the same proxy, then the normal proxy walk takes over.
                    AppLog.Warning(
                        "body.session",
                        "Idle IMAP session for " + account.Email + " was dead after " +
                        watch.ElapsedMilliseconds + "ms; reconnecting once.",
                        ex);
                    CloseSession();
                    reused = false;
                }
            }
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

    /// <summary>
    /// The database is the authority: the entity in hand may be a stale copy from a list
    /// reload, so the row is re-read and its flags copied back before deciding.
    /// </summary>
    private static bool TryUseStored(MailDatabase db, MessageEntity message, out MessageBody body)
    {
        body = MessageBody.Empty;
        var fresh = db.Messages.FindById(message.Id);
        if (fresh == null || !fresh.BodyLoaded || !fresh.AttachmentInfoKnown)
            return false;

        message.BodyLoaded = true;
        message.AttachmentInfoKnown = true;
        message.HasAttachments = fresh.HasAttachments;
        message.AttachmentFileNames = fresh.AttachmentFileNames;
        message.Preview = fresh.Preview;
        message.SearchText = fresh.SearchText;
        if (message.References.Count == 0)
            message.References = fresh.References;

        body = db.LoadMessageBody(message.Id);
        return true;
    }

    private async Task EnsureSessionAsync(
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        MailEndpoints endpoints,
        CancellationToken cancellationToken)
    {
        NetworkGuard.AssertProxy(proxy);

        if (HasLiveSession(account.Id))
            return;

        CloseSession();
        var appPassword = AuthRouter.IsAppPassword(account);
        var authRetried = false;

        while (true)
        {
            // The stored token is used as long as it has not expired; a refresh costs a
            // round trip through the proxy and is only worth it when the server says so.
            if (!appPassword)
            {
                var token = await _tokenService.EnsureAccessTokenAsync(account, proxy, cancellationToken);
                if (token.AccessToken != account.AccessToken ||
                    token.RefreshToken != account.RefreshToken ||
                    token.ExpiresAt != account.AccessTokenExpiresAt)
                {
                    TokenService.ApplyToAccount(account, token.AccessToken, token.RefreshToken, token.ExpiresAt);
                    db.Accounts.Update(account);
                }
            }

            AuthenticationException? authError = null;
            Exception? connectError = null;

            foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
            {
                NetworkGuard.AssertMailKitProxy(mailKitProxy);
                var client = new ImapClient { Timeout = 30_000, CheckCertificateRevocation = false };
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
                catch (AuthenticationException ex)
                {
                    client.Dispose();
                    authError = ex;
                    break;
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    connectError = ex;
                }
            }

            // A token the server no longer accepts is replaced once; the second rejection
            // is reported as it is, so a dead account does not loop.
            if (authError != null && !appPassword && !authRetried)
            {
                AppLog.Warning(
                    "body.session",
                    "IMAP rejected the stored access token for " + account.Email + "; refreshing once.",
                    authError);
                TokenService.InvalidateAccessToken(account);
                db.Accounts.Update(account);
                authRetried = true;
                continue;
            }

            if (authError != null)
                throw authError;

            throw connectError ?? new InvalidOperationException("Could not connect to fetch the message body.");
        }
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

        // Resolving a folder is a LIST round trip; one per session and folder is enough.
        // The previous folder is not closed explicitly: EXAMINE of the next one replaces it.
        if (!_sessionFolders.TryGetValue(folderEntity.FullName, out var folder))
        {
            folder = string.IsNullOrEmpty(folderEntity.FullName)
                ? client.GetFolder(client.PersonalNamespaces[0])
                : await client.GetFolderAsync(folderEntity.FullName, cancellationToken);
            _sessionFolders[folderEntity.FullName] = folder;
        }

        if (!folder.IsOpen)
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        _sessionFolder = folder;
        _sessionFolderName = folderEntity.FullName;
        return folder;
    }

    /// <summary>
    /// Fetches one body over an already selected folder. Shared with the sync, which uses
    /// it to pre-load the newest messages while its own connection is still open.
    /// </summary>
    internal static async Task<MessageBody> FetchAsync(
        MailDatabase db,
        IMailFolder folder,
        MessageEntity message,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        var uid = new UniqueId(message.Uid);

        // The sync already saw the BODYSTRUCTURE and recorded the readable parts, so the
        // text is one FETCH away. HTML is preferred for the same reason as in
        // LoadMessageBodiesOnceAsync: the plain twin is derived locally.
        // The specifier overload is ImapFolder-only (IMailFolder exposes just the BodyPart one).
        if (message.BodyPartsKnown && message.AttachmentInfoKnown && !message.BodyLoaded &&
            folder is ImapFolder imapFolder)
        {
            var html = message.HtmlPartSpecifier;
            var specifier = html ?? message.TextPartSpecifier;
            if (specifier != null)
            {
                var entity = await imapFolder.GetBodyPartAsync(uid, specifier, cancellationToken, new BodyTransferProgress(progress));
                var text = ImapSyncService.ReadTextEntity(entity);
                if (text.Length > 0)
                    return html != null ? new MessageBody("", text) : new MessageBody(text, "");

                // The direct fetch came back empty: fall through to the structure-based path.
            }
        }

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

        var (text2, html2) = await ImapSyncService.LoadMessageBodiesOnceAsync(
            folder,
            summary,
            cancellationToken,
            progress);

        if (text2.Length > 0 || html2.Length > 0)
            return new MessageBody(text2, html2);

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
        _sessionFolders.Clear();
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
