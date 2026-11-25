using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using MimeKit;
using System.Diagnostics;
using System.IO;
using System.Text;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

internal sealed class ImapSyncService
{
    private const int InsertBatchSize = 250;
    private const int SmallFolderSinglePassLimit = 20;

    /// <summary>How many of the newest messages get their body downloaded during the sync itself.</summary>
    private const int PreloadBodyCount = 5;
    private const uint MaxBodyPartOctets = 512 * 1024;
    private const MessageSummaryItems RankingSummaryItems =
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.InternalDate;
    private const MessageSummaryItems StoredSummaryItems =
        MessageSummaryItems.Envelope |
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.BodyStructure |
        // RFC822.SIZE is free in the same FETCH and decides whether a body can be batched.
        MessageSummaryItems.Size |
        // The thread chain, so a reply can carry the full References header.
        MessageSummaryItems.References;

    /// <summary>Hard cap on how many newest messages may be kept for one mailbox.</summary>
    private const int MaxMessages = 2_000;

    /// <summary>
    /// Whole messages up to this size are pre-loaded in one FETCH for the batch; larger ones
    /// (or ones with unknown size) fall back to fetching just their text part.
    /// </summary>
    private const long PreloadWholeMessageMaxBytes = 512 * 1024;

    private readonly TokenService _tokenService = new();

    public async Task SyncAccountAsync(
        MailDatabase db,
        AccountEntity account,
        IReadOnlyList<ProxySpec> proxies,
        int startProxyIndex,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        NetworkGuard.AssertProxies(proxies);
        account.PriorityFoldersReady = false;

        Exception? lastError = null;
        var proxyCount = proxies.Count;
        var attemptCount = Math.Max(2, proxyCount);
        account.LastSyncAttempts = 0;
        account.LastRetryError = null;
        account.LastTokenMs = 0;
        account.LastConnectMs = 0;
        account.LastFoldersMs = 0;
        account.LastStoreMs = 0;

        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            account.LastSyncAttempts = attempt + 1;

            // Preserve the established proxy walk. With only one proxy, add one delayed
            // retry after that original attempt so a transient socket failure is not final.
            if (attempt >= proxyCount)
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

            var proxy = proxies[(startProxyIndex + attempt) % proxyCount];

            try
            {
                await SyncWithProxyAsync(db, account, proxy, cancellationToken, progress);
                if (string.IsNullOrWhiteSpace(account.ActiveProxy))
                    account.ActiveProxy = proxy.Display;
                account.Status = AccountSyncStatus.Ready;
                account.LastError = null;
                account.LastSyncAt = DateTime.UtcNow;
                db.Accounts.Update(account);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TokenEndpointException ex) when (ex.IsTerminal)
            {
                AppLog.Error(
                    "sync",
                    "Token endpoint rejected " + account.Email + " permanently, no further proxy attempts.",
                    ex);
                throw;
            }
            catch (Exception ex)
            {
                account.LastRetryError ??= ex.Message;
                AppLog.Warning(
                    "sync",
                    "Attempt " + (attempt + 1) + "/" + attemptCount + " failed for " + account.Email +
                    " via " + proxy.Summary,
                    ex);
                lastError = ex;
            }
        }

        AppLog.Error("sync", "All " + attemptCount + " attempts failed for " + account.Email, lastError);
        throw lastError ?? new InvalidOperationException("IMAP sync failed for " + account.Email);
    }

    private async Task SyncWithProxyAsync(
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        NetworkGuard.AssertProxy(proxy);

        if (AuthRouter.IsAppPassword(account))
        {
            await SyncWithProxyAppPasswordAsync(db, account, proxy, cancellationToken, progress);
            return;
        }

        var endpoints = MailSite.GetEndpoints(account.Site);
        var authRetried = false;

        while (true)
        {
            var tokenWatch = Stopwatch.StartNew();
            var tokenOlderThanOneHour = !account.AccessTokenRefreshedAt.HasValue ||
                DateTime.UtcNow - account.AccessTokenRefreshedAt.Value >= TimeSpan.FromHours(1);
            await RefreshAccountTokenAsync(
                db,
                account,
                proxy,
                cancellationToken,
                forceRefresh: authRetried || tokenOlderThanOneHour);
            account.LastTokenMs += (int)tokenWatch.ElapsedMilliseconds;

            Exception? lastConnectError = null;
            AuthenticationException? authError = null;

            foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
            {
                NetworkGuard.AssertMailKitProxy(mailKitProxy);
                using var client = new ImapClient
                {
                    // A sync only fetches metadata, so a proxy that stays silent for 40 seconds
                    // is dead; waiting two minutes for it only holds a sync thread hostage.
                    Timeout = 40_000,
                    // Revocation lookups are made by Windows itself, outside the proxy.
                    CheckCertificateRevocation = false
                };

                // Connect and authenticate only. Anything after this point is a sync problem,
                // not a connection problem, and must not be retried against another proxy.
                var connectWatch = Stopwatch.StartNew();
                try
                {
                    client.ProxyClient = mailKitProxy;
                    await client.ConnectAsync(
                        endpoints.ImapHost,
                        endpoints.ImapPort,
                        SecureSocketOptions.SslOnConnect,
                        cancellationToken);
                    await OAuthAuthenticator.AuthenticateAsync(
                        client,
                        account.Email,
                        account.AccessToken,
                        cancellationToken);

                    proxy.RememberWorkingScheme(mailKitProxy);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AuthenticationException ex)
                {
                    account.LastConnectMs += (int)connectWatch.ElapsedMilliseconds;
                    AppLog.Warning(
                        "imap.connect",
                        "Auth rejected for " + account.Email + " via " + proxy.Display,
                        ex);
                    authError = ex;
                    break;
                }
                catch (Exception ex)
                {
                    account.LastConnectMs += (int)connectWatch.ElapsedMilliseconds;
                    AppLog.Warning(
                        "imap.connect",
                        "Connect failed for " + account.Email + " via " + proxy.Display,
                        ex);
                    lastConnectError = ex;
                    continue;
                }

                account.LastConnectMs += (int)connectWatch.ElapsedMilliseconds;
                await RunAccountSyncAsync(client, db, account, proxy, mailKitProxy, endpoints, cancellationToken, progress);
                return;
            }

            if (authError != null && !authRetried)
            {
                TokenService.InvalidateAccessToken(account);
                db.Accounts.Update(account);
                authRetried = true;
                continue;
            }

            if (authError != null)
                throw authError;

            throw lastConnectError ?? new InvalidOperationException("IMAP connection failed for " + account.Email);
        }
    }

    /// <summary>
    /// Runs on an already authenticated connection. Failures here are logged as sync errors
    /// and rethrown unchanged, so they are never mistaken for a bad proxy.
    /// </summary>
    private async Task RunAccountSyncAsync(
        ImapClient client,
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        IProxyClient mailKitProxy,
        MailEndpoints endpoints,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        try
        {
            // Stored mail is kept: each folder only asks the server for uids above the ones
            // it already has, so a repeat sync costs one command per folder.
            var options = SyncOptions.Read(db);
            var fullScan = account.SyncedMessageLimit < options.MessageLimit;
            AppLog.Notice(
                "sync",
                "Begin " + account.Email + " via " + proxy.Summary +
                ": messageLimit=" + options.MessageLimit +
                " syncedLimit=" + account.SyncedMessageLimit +
                (fullScan ? " fullScan" : " incremental") +
                " lastSync=" + (account.LastSyncAt?.ToString("u") ?? "never"));

            var session = new ImapSyncSession(
                client,
                account,
                mailKitProxy,
                db,
                endpoints,
                ct => RefreshAccountTokenAsync(db, account, proxy, ct));

            var contacts = new Dictionary<string, ContactSeed>(StringComparer.OrdinalIgnoreCase);
            var root = client.GetFolder(client.PersonalNamespaces[0]);

            // Folders that have never been synced are only scanned during the walk. Their
            // mail is stored afterwards, because the message limit is a budget for the whole
            // mailbox and the newest messages can only be picked once every folder is known.
            var scanned = new List<ScannedFolder>();
            var collected = new List<CollectedFolder>();
            var foldersWatch = Stopwatch.StartNew();
            await CollectFolderTreeAsync(session, root, collected, cancellationToken);

            var priority = collected
                .Where(x => PriorityRank(x.Folder.Name) >= 0)
                .OrderBy(x => PriorityRank(x.Folder.Name))
                .ThenBy(x => x.Folder.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rest = collected
                .Where(x => PriorityRank(x.Folder.Name) < 0)
                .ToList();

            foreach (var item in priority)
            {
                await SyncFolderAsync(
                    session,
                    item.Folder,
                    contacts,
                    options,
                    fullScan,
                    scanned,
                    cancellationToken,
                    item.StatusPrefetched,
                    storeImmediately: true);
            }

            MarkPriorityFoldersReady(db, account, progress);
            account.LastFoldersMs += (int)foldersWatch.ElapsedMilliseconds;

            foreach (var item in rest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncFolderAsync(
                    session,
                    item.Folder,
                    contacts,
                    options,
                    fullScan,
                    scanned,
                    cancellationToken,
                    item.StatusPrefetched,
                    storeImmediately: false);
            }

            account.LastFoldersMs += (int)foldersWatch.ElapsedMilliseconds;

            var storeWatch = Stopwatch.StartNew();
            await StoreNewestOfMailboxAsync(session, contacts, scanned, options, cancellationToken);

            // Rows written by builds before the attachment column need one BODYSTRUCTURE pass.
            // Mail stored by this build already carries the flag, so after one complete pass
            // the mailbox-wide scan is never repeated.
            if (!account.AttachmentBackfillDone)
                account.AttachmentBackfillDone = await BackfillAttachmentInfoAsync(client, db, account, cancellationToken);

            // The newest few messages are what the operator opens first. Their bodies are
            // pulled now, over this connection, so those clicks never wait for the proxy.
            await PreloadNewestBodiesAsync(db, account, collected, cancellationToken);

            // The limit controls how far the initial history scan reaches. Once a message is
            // stored it remains visible; later runs append only newly arrived mail.
            account.SyncedMessageLimit = options.MessageLimit;

            AddContact(contacts, account.Email, "", true, DateTime.UtcNow);

            // The address book is a convenience: never fail a finished sync over it.
            try
            {
                db.MergeContacts(contacts.Values);
            }
            catch (Exception ex)
            {
                AppLog.Error("contacts", "Could not merge the address book for " + account.Email, ex);
            }

            account.LastStoreMs += (int)storeWatch.ElapsedMilliseconds;

            AppLog.Notice(
                "sync",
                "Done " + account.Email + ": folders=" + collected.Count +
                " messagesInDb=" + db.Messages.Count(x => x.AccountId == account.Id) +
                " token=" + account.LastTokenMs + "ms connect=" + account.LastConnectMs +
                "ms folders=" + account.LastFoldersMs + "ms store=" + account.LastStoreMs + "ms");

            // A broken socket must not turn a finished sync into a failure.
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLog.Warning("imap.sync", "Disconnect failed for " + account.Email, ex);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "imap.sync",
                "Sync failed for " + account.Email + " after a successful login (proxy " + proxy.Display + ")",
                ex);
            throw;
        }
    }

    private async Task SyncWithProxyAppPasswordAsync(
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var endpoints = MailSite.GetEndpoints(account.Site);
        Exception? lastConnectError = null;
        AuthenticationException? authError = null;

        foreach (var mailKitProxy in proxy.CreateMailKitProxyCandidates())
        {
            NetworkGuard.AssertMailKitProxy(mailKitProxy);
            using var client = new ImapClient
            {
                Timeout = 40_000,
                CheckCertificateRevocation = false
            };

            var connectWatch = Stopwatch.StartNew();
            try
            {
                client.ProxyClient = mailKitProxy;
                await client.ConnectAsync(
                    endpoints.ImapHost,
                    endpoints.ImapPort,
                    SecureSocketOptions.SslOnConnect,
                    cancellationToken);
                await AppPasswordAuthenticator.AuthenticateAsync(
                    client,
                    account.Email,
                    account.Password,
                    cancellationToken);

                proxy.RememberWorkingScheme(mailKitProxy);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AuthenticationException ex)
            {
                account.LastConnectMs += (int)connectWatch.ElapsedMilliseconds;
                AppLog.Warning(
                    "imap.connect",
                    "App password rejected for " + account.Email + " via " + proxy.Display,
                    ex);
                authError = ex;
                break;
            }
            catch (Exception ex)
            {
                account.LastConnectMs += (int)connectWatch.ElapsedMilliseconds;
                AppLog.Warning(
                    "imap.connect",
                    "App password connect failed for " + account.Email + " via " + proxy.Display,
                    ex);
                lastConnectError = ex;
                continue;
            }

            account.LastConnectMs += (int)connectWatch.ElapsedMilliseconds;
            await RunAccountSyncAsync(client, db, account, proxy, mailKitProxy, endpoints, cancellationToken, progress);
            return;
        }

        if (authError != null)
            throw authError;

        throw lastConnectError ?? new InvalidOperationException("IMAP connection failed for " + account.Email);
    }

    private async Task RefreshAccountTokenAsync(
        MailDatabase db,
        AccountEntity account,
        ProxySpec proxy,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        try
        {
            var token = await _tokenService.EnsureAccessTokenAsync(account, proxy, cancellationToken, forceRefresh);

            // A still-valid token comes back unchanged; writing the account row for it would
            // be one database write per account per sync for nothing.
            if (token.AccessToken == account.AccessToken &&
                token.RefreshToken == account.RefreshToken &&
                token.ExpiresAt == account.AccessTokenExpiresAt)
            {
                return;
            }

            TokenService.ApplyToAccount(account, token.AccessToken, token.RefreshToken, token.ExpiresAt);
            db.Accounts.Update(account);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "token",
                "Access token for " + account.Email + " could not be refreshed via " + proxy.Summary +
                " (forceRefresh=" + forceRefresh + "). IMAP was not contacted.",
                ex);
            throw;
        }
    }

    private static int PriorityRank(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return -1;

        if (name.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (name.Equals("Bulk", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Bulk Mail", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (name.Equals("Spam", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Junk", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Junk E-mail", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Junk Email", StringComparison.OrdinalIgnoreCase))
            return 2;

        return -1;
    }

    private static void MarkPriorityFoldersReady(
        MailDatabase db,
        AccountEntity account,
        IProgress<string>? progress)
    {
        account.PriorityFoldersReady = true;
        account.Status = AccountSyncStatus.Ready;
        db.Accounts.Update(account);
        progress?.Report("PRIORITY " + account.Email);
    }

    private static async Task CollectFolderTreeAsync(
        ImapSyncSession session,
        IMailFolder folder,
        List<CollectedFolder> collected,
        CancellationToken cancellationToken,
        bool statusPrefetched = false)
    {
        // Personal namespace roots may report Exists while having no user-visible name.
        // Still recurse so Inbox and the rest of the tree are collected underneath.
        if (folder.Exists && !string.IsNullOrWhiteSpace(folder.Name))
            collected.Add(new CollectedFolder(folder, statusPrefetched));

        // LIST marks nearly every Yahoo/AOL leaf with \HasNoChildren. Asking each such
        // folder for children adds one guaranteed-empty network round trip.
        if (folder.Attributes.HasFlag(FolderAttributes.HasNoChildren))
            return;

        IList<IMailFolder> subfolders;
        var subfoldersHaveStatus = true;
        try
        {
            subfolders = await folder.GetSubfoldersAsync(
                StatusItems.Count |
                StatusItems.Unread |
                StatusItems.UidNext |
                StatusItems.UidValidity,
                false,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Some servers do not support LIST-STATUS. Preserve the proven folder walk and
            // simply fall back to ordinary LIST; those folders will use the open-folder path.
            try
            {
                subfolders = await folder.GetSubfoldersAsync(false, cancellationToken);
                subfoldersHaveStatus = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return;
            }
        }

        foreach (var subfolder in subfolders)
        {
            await CollectFolderTreeAsync(
                session,
                subfolder,
                collected,
                cancellationToken,
                subfoldersHaveStatus);
        }
    }

    private static async Task SyncFolderAsync(
        ImapSyncSession session,
        IMailFolder folder,
        Dictionary<string, ContactSeed> contacts,
        SyncOptions options,
        bool fullScan,
        List<ScannedFolder> scanned,
        CancellationToken cancellationToken,
        bool statusPrefetched,
        bool storeImmediately)
    {
        var folderFullName = folder.FullName;
        var db = session.Database;
        var folderEntity = db.GetOrCreateFolder(session.Account.Id, folder.FullName, folder.Name);

        var email = session.Account.Email;
        var storedCountBefore = folderEntity.MessageCount;

        // STATUS is enough to discard empty folders before SELECT.
        //
        // UIDNEXT is deliberately NOT used to skip a folder. Yahoo/AOL report it as a
        // "predicted" value that can stay behind the newest delivered uid (their own IMAP
        // documentation shows SELECT returning UIDNEXT 44627 while message 44627 already
        // exists), so a "UIDNEXT <= LastUid + 1" shortcut silently hid new Inbox mail on
        // every repeat sync. Re-importing the account reset LastUid and made it visible
        // again, which is how the bug was noticed. One FETCH above LastUid per folder is
        // the only check that holds on these servers.
        if (statusPrefetched && folder.UidValidity != 0)
        {
            if (folderEntity.UidValidity != folder.UidValidity)
            {
                if (folderEntity.UidValidity != 0)
                    db.DeleteFolderMessages(folderEntity.Id);

                folderEntity.UidValidity = folder.UidValidity;
                folderEntity.LastUid = 0;
            }

            folderEntity.MessageCount = folder.Count;
            folderEntity.ReadCount = Math.Clamp(folder.Count - folder.Unread, 0, folder.Count);
            folderEntity.ReadCountKnown = true;
            db.Folders.Update(folderEntity);

            if (folder.Count == 0 &&
                !(folder.UidNext.HasValue && folder.UidNext.Value.Id > 1))
            {
                LogFolder(email, folderFullName, folder, folderEntity, storedCountBefore, fullScan, "skip: empty (STATUS)");
                return;
            }
        }

        try
        {
            if (!folder.IsOpen)
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "imap.folder",
                "Could not open folder '" + folderFullName + "' for " + session.Account.Email,
                ex);
            return;
        }

        // The folder is deliberately left selected: EXAMINE of the next folder replaces it on
        // the server side, so an explicit CLOSE would only add one round trip per folder.

        // A new UIDVALIDITY means the server reissued its uids: everything stored for this
        // folder now points at the wrong messages.
        if (folderEntity.UidValidity != folder.UidValidity)
        {
            db.DeleteFolderMessages(folderEntity.Id);
            folderEntity.UidValidity = folder.UidValidity;
            folderEntity.LastUid = 0;
        }

        folderEntity.MessageCount = folder.Count;
        folderEntity.ReadCount = Math.Clamp(folder.Count - folder.Unread, 0, folder.Count);
        folderEntity.ReadCountKnown = true;
        db.Folders.Update(folderEntity);

        if (folder.Count == 0)
        {
            LogFolder(email, folderFullName, folder, folderEntity, storedCountBefore, fullScan, "skip: empty (SELECT)");
            return;
        }

        // See the note above: UIDNEXT from SELECT/EXAMINE is not trusted either. A folder
        // synced before always asks for uids above LastUid; the answer is usually empty.
        var summaries = await FetchSummariesAsync(
            folder,
            folderEntity.LastUid,
            options,
            fullScan,
            cancellationToken,
            storeImmediately);

        // New mail is appended. MessageLimit only controls the initial history window;
        // existing local rows are never removed by a normal sync.
        if (folderEntity.LastUid > 0 && !fullScan)
        {
            var stored = summaries.Count == 0
                ? 0
                : StoreMessages(session, contacts, folderEntity, summaries, cancellationToken);
            LogFolder(
                email, folderFullName, folder, folderEntity, storedCountBefore, fullScan,
                "incremental: fetched=" + summaries.Count + " new=" + stored);
            return;
        }

        if (summaries.Count == 0)
        {
            LogFolder(email, folderFullName, folder, folderEntity, storedCountBefore, fullScan, "first scan: fetched=0");
            return;
        }

        // Inbox/Bulk/Spam must be in the database before the rest of the tree is scanned.
        if (storeImmediately)
        {
            var stored = StoreMessages(session, contacts, folderEntity, summaries, cancellationToken);
            LogFolder(
                email, folderFullName, folder, folderEntity, storedCountBefore, fullScan,
                "first scan (priority): fetched=" + summaries.Count + " new=" + stored);
            return;
        }

        LogFolder(
            email, folderFullName, folder, folderEntity, storedCountBefore, fullScan,
            "first scan: fetched=" + summaries.Count + ", stored after the walk");

        scanned.Add(new ScannedFolder(
            folderEntity,
            folder,
            folder.Count <= SmallFolderSinglePassLimit,
            summaries));
    }

    /// <summary>
    /// A folder that has been synced before is asked only for uids above the highest one
    /// stored, which is a single command returning nothing when no new mail arrived. On a
    /// first sync a folder cannot contribute more than the whole limit, so that is as deep
    /// as the scan ever goes.
    /// </summary>
    private static async Task<IList<IMessageSummary>> FetchSummariesAsync(
        IMailFolder folder,
        uint lastUid,
        SyncOptions options,
        bool fullScan,
        CancellationToken cancellationToken,
        bool storeImmediately = false)
    {
        if (lastUid > 0 && !fullScan)
        {
            var range = new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue);
            return await folder.FetchAsync(range, StoredSummaryItems, cancellationToken);
        }

        var firstIndex = Math.Max(0, folder.Count - options.MessageLimit);
        var items = folder.Count <= SmallFolderSinglePassLimit || storeImmediately
            ? StoredSummaryItems
            : RankingSummaryItems;
        return await folder.FetchAsync(firstIndex, -1, items, cancellationToken);
    }

    /// <summary>
    /// Keeps the newest messages of the whole mailbox, not of each folder: everything the
    /// walk scanned is ordered by date and only the newest <c>MessageLimit</c> are stored.
    /// Every scanned folder then remembers the newest uid it was asked about, so the mail
    /// left outside the limit is neither downloaded again nor stored twice.
    /// </summary>
    private static async Task StoreNewestOfMailboxAsync(
        ImapSyncSession session,
        Dictionary<string, ContactSeed> contacts,
        List<ScannedFolder> scanned,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        if (scanned.Count == 0)
            return;

        var newest = scanned
            .SelectMany(entry => entry.Summaries.Select(summary => (Entry: entry, Summary: summary)))
            .OrderByDescending(pair => SortDate(pair.Summary))
            .Take(options.MessageLimit)
            .GroupBy(pair => pair.Entry);

        foreach (var group in newest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = group.Select(pair => pair.Summary).ToList();

            if (group.Key.HasFullMetadata)
            {
                StoreMessages(
                    session,
                    contacts,
                    group.Key.Folder,
                    selected,
                    cancellationToken);
                continue;
            }

            var mailFolder = group.Key.MailFolder;

            if (!mailFolder.IsOpen)
                await mailFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var selectedUids = group
                .Select(pair => pair.Summary.UniqueId)
                .OrderBy(uid => uid.Id)
                .ToList();
            var details = await mailFolder.FetchAsync(
                selectedUids,
                StoredSummaryItems,
                cancellationToken);

            StoreMessages(
                session,
                contacts,
                group.Key.Folder,
                details,
                cancellationToken);
        }

        var db = session.Database;

        foreach (var entry in scanned)
        {
            var highestScanned = entry.Summaries.Max(summary => summary.UniqueId.Id);
            if (highestScanned <= entry.Folder.LastUid)
                continue;

            entry.Folder.LastUid = highestScanned;
            db.Folders.Update(entry.Folder);
        }
    }

    /// <summary>
    /// Bodies of the newest <see cref="PreloadBodyCount"/> messages of the account, fetched
    /// over the sync connection while it is still open. Small messages of one folder go in a
    /// single <c>UID FETCH a,b,c (BODY.PEEK[])</c>; the rest cost one command each. Folders
    /// come from the walk that just ran, so no LIST is needed. Any failure here is logged
    /// and the sync still counts as done.
    /// </summary>
    private static async Task PreloadNewestBodiesAsync(
        MailDatabase db,
        AccountEntity account,
        List<CollectedFolder> collected,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var fetched = 0;
        var batched = 0;
        var skipped = 0;
        var commands = 0;
        try
        {
            var newest = db.Messages.Query()
                .Where(x => x.AccountId == account.Id)
                .OrderByDescending(x => x.DateUtc)
                .Limit(PreloadBodyCount)
                .ToList()
                .Where(x => !x.BodyLoaded)
                .ToList();

            foreach (var group in newest.GroupBy(x => x.FolderId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var folderEntity = db.Folders.FindById(group.Key);
                var folder = folderEntity == null
                    ? null
                    : collected
                        .Select(x => x.Folder)
                        .FirstOrDefault(x => string.Equals(x.FullName, folderEntity.FullName, StringComparison.Ordinal));
                if (folder == null)
                {
                    skipped += group.Count();
                    continue;
                }

                if (!folder.IsOpen)
                    await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                var whole = new List<MessageEntity>();
                var singles = new List<MessageEntity>();
                foreach (var message in group)
                {
                    // Whole-message fetch is only worth it when the size is known and small:
                    // an attachment would otherwise ride along for nothing.
                    if (folder is ImapFolder &&
                        message.Size > 0 &&
                        message.Size <= PreloadWholeMessageMaxBytes &&
                        !message.HasAttachments)
                    {
                        whole.Add(message);
                    }
                    else
                    {
                        singles.Add(message);
                    }
                }

                if (whole.Count > 0)
                {
                    commands++;
                    var byUid = whole.ToDictionary(x => x.Uid);
                    var imapFolder = (ImapFolder)folder;
                    await imapFolder.GetStreamsAsync(
                        whole.Select(x => new UniqueId(x.Uid)).ToList(),
                        async (_, _, uid, stream, token) =>
                        {
                            if (!byUid.TryGetValue(uid.Id, out var message))
                                return;

                            try
                            {
                                var mime = await MimeMessage.LoadAsync(stream, token);
                                var text = mime.TextBody ?? "";
                                var html = mime.HtmlBody ?? "";
                                if (text.Length > 0 || html.Length > 0)
                                {
                                    db.SaveMessageBody(message, text, html);
                                    fetched++;
                                    batched++;
                                }
                                else
                                {
                                    // Nothing readable inside: remember so clicks do not refetch.
                                    message.BodyLoaded = true;
                                    db.Messages.Update(message);
                                    skipped++;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                skipped++;
                                AppLog.Warning(
                                    "sync.preload",
                                    "Could not parse pre-loaded uid=" + uid.Id + " for " + account.Email,
                                    ex);
                            }
                        },
                        cancellationToken);
                }

                foreach (var message in singles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    commands++;
                    var body = await MessageBodyLoader.FetchAsync(db, folder, message, cancellationToken);
                    if (body.Text.Length > 0 || body.Html.Length > 0)
                    {
                        db.SaveMessageBody(message, body.Text, body.Html);
                        fetched++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sync.preload", "Body pre-load for " + account.Email + " stopped early.", ex);
        }

        AppLog.Notice(
            "sync.preload",
            account.Email + ": bodies fetched=" + fetched + " (batched=" + batched + ") skipped=" + skipped +
            " commands=" + commands + " in " + watch.ElapsedMilliseconds + "ms");
    }

    /// <summary>Returns true when every stored message of the account now has attachment info.</summary>
    private static async Task<bool> BackfillAttachmentInfoAsync(
        ImapClient client,
        MailDatabase db,
        AccountEntity account,
        CancellationToken cancellationToken)
    {
        var missing = db.Messages
            .Find(x => x.AccountId == account.Id)
            .Where(x => !x.AttachmentInfoKnown)
            .GroupBy(x => x.FolderId)
            .ToList();

        var complete = true;

        foreach (var group in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderEntity = db.Folders.FindById(group.Key);
            if (folderEntity == null)
                continue;

            try
            {
                var folder = string.IsNullOrEmpty(folderEntity.FullName)
                    ? client.GetFolder(client.PersonalNamespaces[0])
                    : await client.GetFolderAsync(folderEntity.FullName, cancellationToken);
                if (!folder.IsOpen)
                    await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                var byUid = group.ToDictionary(x => x.Uid);
                var summaries = await folder.FetchAsync(
                    byUid.Keys.Select(uid => new UniqueId(uid)).ToList(),
                    MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure,
                    cancellationToken);

                foreach (var summary in summaries)
                {
                    if (!byUid.TryGetValue(summary.UniqueId.Id, out var message))
                        continue;

                    message.AttachmentFileNames = GetAttachmentNames(summary);
                    message.HasAttachments = message.AttachmentFileNames.Count > 0;
                    message.AttachmentInfoKnown = true;
                    db.Messages.Update(message);
                    byUid.Remove(summary.UniqueId.Id);
                }

                // Messages the server no longer has cannot be inspected; treat them as done
                // so the mailbox-wide scan is not repeated for them on every sync.
                foreach (var orphan in byUid.Values)
                {
                    orphan.AttachmentInfoKnown = true;
                    db.Messages.Update(orphan);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Leave these rows unknown so a later sync can retry the metadata backfill.
                complete = false;
            }
        }

        return complete;
    }

    internal static List<string> GetAttachmentNames(IMessageSummary summary)
    {
        var names = new List<string>();
        if (summary.Body == null)
            return names;

        try
        {
            var index = 0;
            foreach (var attachment in summary.Attachments)
            {
                index++;
                var name = attachment.FileName?.Trim();
                names.Add(string.IsNullOrWhiteSpace(name) ? $"attachment-{index}" : name);
            }
        }
        catch
        {
            // A broken BODYSTRUCTURE still stores the row; the paperclip can wait.
        }

        return names;
    }

    /// <summary>Returns how many rows were newly inserted (duplicates and skipped rows excluded).</summary>
    private static int StoreMessages(
        ImapSyncSession session,
        Dictionary<string, ContactSeed> contacts,
        FolderEntity folderEntity,
        IList<IMessageSummary> summaries,
        CancellationToken cancellationToken)
    {
        var db = session.Database;
        var batch = new List<MessageEntity>(InsertBatchSize);
        var knownUids = db.Messages
            .Find(x => x.FolderId == folderEntity.Id)
            .Select(x => x.Uid)
            .ToHashSet();
        var skipped = 0;
        var inserted = 0;
        var highestUid = folderEntity.LastUid;

        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If the process stopped after a batch insert but before LastUid was saved, the
            // next sync sees the same server rows again. Ignore them instead of duplicating
            // messages in the local database.
            if (!knownUids.Add(summary.UniqueId.Id))
            {
                if (summary.UniqueId.Id > highestUid)
                    highestUid = summary.UniqueId.Id;
                continue;
            }

            // One malformed message must never abort the folder, let alone the account.
            try
            {
                batch.Add(BuildMessage(db.NextMessageId(), session.Account, folderEntity, summary));
                inserted++;

                if (summary.UniqueId.Id > highestUid)
                    highestUid = summary.UniqueId.Id;

                try
                {
                    CollectContacts(contacts, summary);
                }
                catch
                {
                    // Address-book indexing is optional; a bad From must not drop the message.
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                AppLog.Error(
                    "imap.message",
                    "Skipped message uid=" + summary.UniqueId.Id +
                    " in '" + folderEntity.FullName + "' for " + session.Account.Email +
                    ", subject=" + (summary.Envelope?.Subject ?? ""),
                    ex);
            }

            if (batch.Count >= InsertBatchSize)
                FlushBatch(db, batch);
        }

        FlushBatch(db, batch);

        if (highestUid > folderEntity.LastUid)
        {
            folderEntity.LastUid = highestUid;
            db.Folders.Update(folderEntity);
        }

        if (skipped > 0)
        {
            AppLog.Warning(
                "imap.folder",
                "Folder '" + folderEntity.FullName + "' for " + session.Account.Email +
                ": skipped " + skipped + " of " + summaries.Count + " message(s).");
        }

        return inserted;
    }

    /// <summary>
    /// One line per folder per sync. Together with the account OK/FAIL lines this is what a
    /// user attaches when mail is missing: it shows the server counters the decision was
    /// based on and how many rows actually reached the database.
    /// </summary>
    private static void LogFolder(
        string email,
        string folderFullName,
        IMailFolder folder,
        FolderEntity folderEntity,
        int storedCountBefore,
        bool fullScan,
        string outcome)
    {
        AppLog.Notice(
            "sync.folder",
            email + " '" + folderFullName + "': " + outcome +
            " | server count=" + folder.Count +
            " unread=" + folder.Unread +
            " uidnext=" + (folder.UidNext.HasValue ? folder.UidNext.Value.Id.ToString() : "?") +
            " uidvalidity=" + folder.UidValidity +
            " | local lastUid=" + folderEntity.LastUid +
            " countBefore=" + storedCountBefore +
            (fullScan ? " fullScan" : ""));
    }

    /// <summary>
    /// The date the mailbox is ranked by. The envelope date is written by the sender and can
    /// be wrong or missing, so the server's own timestamp is preferred.
    /// </summary>
    private static DateTime SortDate(IMessageSummary summary) =>
        summary.InternalDate?.UtcDateTime ??
        summary.Envelope?.Date?.UtcDateTime ??
        DateTime.MinValue;

    private static void FlushBatch(MailDatabase db, List<MessageEntity> messages)
    {
        if (messages.Count == 0)
            return;

        db.Messages.InsertBulk(messages);
        messages.Clear();
    }

    /// <summary>Used by <see cref="MessageBodyLoader"/> when a message is opened.</summary>
    internal static async Task<(string Text, string Html)> LoadMessageBodiesOnceAsync(
        IMailFolder folder,
        IMessageSummary summary,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        var uid = summary.UniqueId;

        // One part per message. The plain-text twin of an HTML mail is derived locally in
        // BuildIndexText instead of costing a second round trip through the proxy.
        if (summary.HtmlBody != null && !ReferenceEquals(summary.TextBody, summary.HtmlBody))
        {
            var htmlOnly = await LoadTextPartAsync(folder, uid, summary.HtmlBody, cancellationToken, progress);
            if (htmlOnly.Length > 0)
                return ("", htmlOnly);
        }

        var textBody = await LoadTextPartAsync(folder, uid, summary.TextBody, cancellationToken, progress);
        return (textBody, "");
    }

    internal static async Task<(string Text, string Html)> LoadFullMessageBodiesAsync(
        IMailFolder folder,
        IMessageSummary summary,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        var uid = summary.UniqueId;
        var mime = await folder.GetMessageAsync(uid, cancellationToken, new BodyTransferProgress(progress));
        var text = mime.TextBody ?? "";
        var html = mime.HtmlBody ?? "";

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(html))
        {
            AppLog.Warning(
                "imap.body",
                "Empty body after full download uid=" + uid.Id +
                ", subject=" + (summary.Envelope?.Subject ?? ""));
        }

        return (text, html);
    }

    private static async Task<string> LoadTextPartAsync(
        IMailFolder folder,
        UniqueId uid,
        BodyPartText? part,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        if (part == null)
            return "";

        if (part.Octets > MaxBodyPartOctets)
        {
            AppLog.Warning(
                "imap.body",
                "Skipped oversized body part uid=" + uid.Id + ", octets=" + part.Octets);
            return "";
        }

        var entity = await folder.GetBodyPartAsync(uid, part, cancellationToken, new BodyTransferProgress(progress));
        return ReadTextEntity(entity);
    }

    /// <summary>Decoded text of a fetched body part; empty when the part is not text.</summary>
    internal static string ReadTextEntity(MimeEntity? entity)
    {
        if (entity is TextPart textPart)
            return textPart.Text ?? "";

        if (entity is MimePart mimePart &&
            mimePart.ContentType.IsMimeType("text", "plain") &&
            mimePart.Content != null)
        {
            using var stream = new MemoryStream();
            mimePart.Content.DecodeTo(stream);
            var charset = mimePart.ContentType.CharsetEncoding ?? Encoding.UTF8;
            return charset.GetString(stream.ToArray());
        }

        return "";
    }

    /// <summary>
    /// The specifier the body loader can fetch directly. A part the loader would refuse
    /// anyway (oversized) is not recorded, so the structure-based path handles it.
    /// </summary>
    private static string? PartSpecifierOrNull(BodyPartText? part)
    {
        if (part == null || part.Octets > MaxBodyPartOctets)
            return null;

        return part.PartSpecifier ?? "";
    }

    private static MessageEntity BuildMessage(
        int messageId,
        AccountEntity account,
        FolderEntity folder,
        IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        string subject;
        string from;
        string to;
        try
        {
            subject = envelope?.Subject ?? "";
            from = envelope?.From?.ToString() ?? "";
            to = envelope?.To?.ToString() ?? "";
        }
        catch
        {
            subject = "";
            from = "";
            to = "";
        }

        return new MessageEntity
        {
            Id = messageId,
            AccountId = account.Id,
            FolderId = folder.Id,
            Uid = summary.UniqueId.Id,
            Subject = subject,
            From = from,
            To = to,
            DateUtc = summary.InternalDate?.UtcDateTime ?? envelope?.Date?.UtcDateTime ?? DateTime.UtcNow,
            IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            AttachmentFileNames = GetAttachmentNames(summary),
            HasAttachments = summary.Body != null && summary.Attachments.Any(),
            AttachmentInfoKnown = summary.Body != null,
            MessageId = envelope?.MessageId ?? "",
            References = ReadReferences(summary),
            BodyLoaded = false,
            BodyPartsKnown = summary.Body != null,
            TextPartSpecifier = PartSpecifierOrNull(summary.TextBody),
            HtmlPartSpecifier = ReferenceEquals(summary.HtmlBody, summary.TextBody)
                ? null
                : PartSpecifierOrNull(summary.HtmlBody),
            Size = summary.Size ?? 0,
            Preview = subject,
            SearchText = BuildSearchText(subject, from, to)
        };
    }

    /// <summary>
    /// References as fetched; a message that only has In-Reply-To (some clients send no
    /// References) gets that one id so the chain still has a root.
    /// </summary>
    internal static List<string> ReadReferences(IMessageSummary summary)
    {
        var result = new List<string>();
        try
        {
            if (summary.References != null)
                result.AddRange(summary.References.Where(id => !string.IsNullOrWhiteSpace(id)));

            var inReplyTo = summary.Envelope?.InReplyTo;
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(inReplyTo))
                result.Add(inReplyTo);
        }
        catch
        {
            // A malformed header only costs the thread chain.
        }

        return result;
    }

    private static void CollectContacts(Dictionary<string, ContactSeed> contacts, IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        if (envelope == null)
            return;

        var seenAt = envelope.Date?.UtcDateTime ?? DateTime.UtcNow;

        foreach (var mailbox in envelope.From?.Mailboxes ?? [])
            AddContact(contacts, mailbox.Address, mailbox.Name ?? "", false, seenAt);

        foreach (var mailbox in envelope.To?.Mailboxes ?? [])
            AddContact(contacts, mailbox.Address, mailbox.Name ?? "", false, seenAt);
    }

    private static void AddContact(
        Dictionary<string, ContactSeed> contacts,
        string? email,
        string displayName,
        bool isOwnAccount,
        DateTime seenAt)
    {
        var address = email?.Trim() ?? "";
        if (address.Length == 0 || !address.Contains('@'))
            return;

        if (contacts.TryGetValue(address, out var existing))
        {
            contacts[address] = existing with
            {
                DisplayName = existing.DisplayName.Length > 0 ? existing.DisplayName : displayName,
                IsOwnAccount = existing.IsOwnAccount || isOwnAccount,
                SeenCount = existing.SeenCount + 1,
                LastSeenUtc = seenAt > existing.LastSeenUtc ? seenAt : existing.LastSeenUtc
            };
            return;
        }

        contacts[address] = new ContactSeed(address, displayName, isOwnAccount, 1, seenAt);
    }

    private static string BuildSearchText(string subject, string from, string to) =>
        string.Join(' ', subject, from, to).ToLowerInvariant();

    /// <summary>
    /// User-tunable limits, read once per account so a settings change cannot alter the shape
    /// of a run half way through.
    /// </summary>
    internal readonly record struct SyncOptions(int MessageLimit)
    {
        public static SyncOptions Read(MailDatabase db) =>
            new(Math.Clamp(db.GetOrCreateSettings().MessageLimit, 10, MaxMessages));
    }

    /// <summary>A folder read during the walk whose mail still has to survive the limit.</summary>
    private readonly record struct ScannedFolder(
        FolderEntity Folder,
        IMailFolder MailFolder,
        bool HasFullMetadata,
        IList<IMessageSummary> Summaries);

    private readonly record struct CollectedFolder(
        IMailFolder Folder,
        bool StatusPrefetched);
}

/// <summary>
/// Maps one IMAP literal onto a slice of the preview percent. MailKit often delivers a short
/// message in a single read, so this slice moves only as far as the bytes actually arrive.
/// </summary>
internal sealed class BodyTransferProgress : ITransferProgress
{
    private readonly IProgress<int>? _progress;
    private readonly int _from;
    private readonly int _to;
    private int _last = -1;

    public BodyTransferProgress(IProgress<int>? progress, int from = 60, int to = 98)
    {
        _progress = progress;
        _from = from;
        _to = to;
    }

    public void Report(long bytesTransferred, long totalSize)
    {
        if (_progress == null || totalSize <= 0)
            return;

        var inner = (int)Math.Clamp(bytesTransferred * 100 / totalSize, 0, 100);
        var percent = _from + inner * (_to - _from) / 100;
        if (percent == _last)
            return;

        _last = percent;
        _progress.Report(percent);
    }

    public void Report(long bytesTransferred)
    {
    }
}
