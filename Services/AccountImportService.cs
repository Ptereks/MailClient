using System.IO;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

internal sealed class AccountImportService
{
    private const int MaxReportedErrors = 50;

    public ImportResult ImportFromFile(MailDatabase db, string sourcePath, string format)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Account file not found.", sourcePath);

        var delimiter = MailRecord.DetectDelimiter(format);
        var updated = 0;
        var failed = 0;
        var errors = new List<string>();

        // Emails are unique, so pending inserts are staged by email to also collapse
        // duplicate lines inside the file itself.
        var pending = new Dictionary<string, AccountEntity>(StringComparer.OrdinalIgnoreCase);
        var existingByEmail = new Dictionary<string, AccountEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in db.Accounts.FindAll())
            existingByEmail.TryAdd(account.Email, account);

        foreach (var line in File.ReadLines(sourcePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            try
            {
                var record = MailRecord.Parse(line, format, delimiter);
                var site = MailSite.Detect(record.Email);
                existingByEmail.TryGetValue(record.Email, out var existing);

                if (existing == null)
                {
                    pending[record.Email] = CreateAccountEntity(record, site);
                    continue;
                }

                ApplyRecord(existing, record, site);
                db.Accounts.Update(existing);
                updated++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add(ex.Message);
            }
        }

        var imported = InsertPending(db, pending, errors, ref failed);

        SeedAddressBook(db);

        var xoauth = 0;
        var appPassword = 0;
        foreach (var account in db.Accounts.FindAll())
        {
            if (AuthRouter.Resolve(account) == AuthMode.AppPassword)
                appPassword++;
            else
                xoauth++;
        }

        AppLog.Notice("import", "Auth modes: XOAUTH2=" + xoauth + " AppPassword=" + appPassword);

        return new ImportResult(imported, updated, failed, errors, xoauth, appPassword);
    }

    /// <summary>
    /// A single unusable row must not cost the whole file, so a failed bulk insert is retried
    /// account by account.
    /// </summary>
    private static int InsertPending(
        MailDatabase db,
        Dictionary<string, AccountEntity> pending,
        List<string> errors,
        ref int failed)
    {
        if (pending.Count == 0)
            return 0;

        try
        {
            db.Accounts.InsertBulk(pending.Values);
            return pending.Count;
        }
        catch (Exception ex)
        {
            AppLog.Warning("import", "Bulk insert failed, retrying one account at a time.", ex);
        }

        var imported = 0;

        foreach (var account in pending.Values)
        {
            try
            {
                // InsertBulk may have committed a prefix before reporting a later bad row.
                // Do not misreport those already inserted accounts as failures on fallback.
                if (db.Accounts.FindOne(x => x.Email == account.Email) != null)
                {
                    imported++;
                    continue;
                }

                db.Accounts.Insert(account);
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add(account.Email + ": " + ex.Message);

                AppLog.Error("import", "Could not import " + account.Email, ex);
            }
        }

        return imported;
    }

    /// <summary>
    /// Own addresses belong in the address book right away. The account list is materialised
    /// first: passing a live LiteDB cursor into a method that writes to the same database
    /// fails once there is anything to enumerate.
    /// </summary>
    private static void SeedAddressBook(MailDatabase db)
    {
        try
        {
            var seeds = db.Accounts.FindAll()
                .Select(x => new ContactSeed(x.Email, "", true, 0, DateTime.UtcNow))
                .ToList();

            db.MergeContacts(seeds);
        }
        catch (Exception ex)
        {
            // The address book is a convenience: it must never fail an import.
            AppLog.Error("import", "Could not seed the address book after import.", ex);
        }
    }

    private static AccountEntity CreateAccountEntity(MailRecord record, string site) =>
        new()
        {
            Email = record.Email,
            Password = record.Password,
            MailboxPassword = record.MailboxPassword,
            Phone = record.Phone,
            AccessToken = record.AccessToken,
            RefreshToken = record.RefreshToken,
            AccessTokenExpiresAt = null,
            AccessTokenRefreshedAt = null,
            Site = site,
            AuthMode = AuthRouter.Detect(record.RefreshToken),
            Status = AccountSyncStatus.Pending
        };

    private static void ApplyRecord(AccountEntity account, MailRecord record, string site)
    {
        account.Password = record.Password;
        account.MailboxPassword = record.MailboxPassword;
        account.Phone = record.Phone;
        account.RefreshToken = record.RefreshToken;
        account.Site = site;
        AuthRouter.ApplyDetected(account, record.RefreshToken);
        account.Status = AccountSyncStatus.Pending;
        account.LastError = null;

        if (record.AccessToken.Length > 0)
            account.AccessToken = record.AccessToken;

        account.AccessTokenExpiresAt = null;
        account.AccessTokenRefreshedAt = null;
    }
}

internal readonly record struct ImportResult(
    int Imported,
    int Updated,
    int Failed,
    IReadOnlyList<string> Errors,
    int XoauthCount,
    int AppPasswordCount);

internal sealed class SyncOrchestrator
{
    private readonly ImapSyncService _imapSync = new();

    public Task<SyncRunResult> SyncAllAsync(
        MailDatabase db,
        IReadOnlyList<ProxySpec> proxies,
        int threads,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        return SyncAccountsAsync(
            db,
            db.Accounts.FindAll().ToList(),
            proxies,
            threads,
            log,
            cancellationToken);
    }

    public async Task<SyncRunResult> SyncAccountsAsync(
        MailDatabase db,
        IReadOnlyList<AccountEntity> requested,
        IReadOnlyList<ProxySpec> proxies,
        int threads,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        NetworkGuard.AssertProxies(proxies);

        if (proxies.Count == 0)
            throw new InvalidOperationException("Proxy file is empty.");

        var accounts = requested
            .Where(x => !x.IsGroupPlaceholder)
            .OrderByDescending(x => x.SyncPriority)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0)
            throw new InvalidOperationException("No accounts in database. Import account file first.");

        var effectiveThreads = Math.Min(accounts.Count, Math.Max(1, threads));
        var success = 0;
        var failed = 0;
        db.BeginContactBatch();

        // The index fixes an account's primary proxy round-robin, while one global gate keeps
        // the user's thread limit authoritative even when there are more proxy lines than
        // threads. With one proxy every worker uses it; with many proxies work stays balanced.
        // A proxy line without an explicit scheme is tried as http first and socks5 second
        // (ProxySpec.CreateMailKitProxyCandidates); the scheme that worked is remembered for
        // the rest of the run, so only the first wave of workers ever pays for the probe.
        using var gate = new SemaphoreSlim(effectiveThreads, effectiveThreads);
        var tasks = accounts.Select(async (account, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var proxyIndex = index % proxies.Count;
                var ok = await SyncOneAsync(db, account, proxies, proxyIndex, log, cancellationToken);
                if (ok)
                    Interlocked.Increment(ref success);
                else
                    Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            try
            {
                db.CompleteContactBatch();
            }
            catch (Exception ex)
            {
                // Contact indexing is a convenience and must not invalidate completed mail syncs.
                AppLog.Error("contacts", "Could not flush the batched address book.", ex);
            }
        }

        if (failed > 0)
        {
            AppLog.Warning(
                "sync",
                "Run finished with failures: " + failed + " of " + accounts.Count +
                " account(s) failed, " + effectiveThreads + " thread(s) over " +
                proxies.Count + " proxy line(s).");
        }

        return new SyncRunResult(accounts.Count, success, failed, effectiveThreads);
    }

    public async Task<bool> SyncOneAsync(
        MailDatabase db,
        AccountEntity account,
        IReadOnlyList<ProxySpec> proxies,
        int startProxyIndex,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            account.Status = AccountSyncStatus.Syncing;
            account.LastError = null;
            db.Accounts.Update(account);

            var mode = AuthRouter.Resolve(account);
            log?.Report("Sync " + account.Email + " [" + mode + "]...");
            await _imapSync.SyncAccountAsync(db, account, proxies, startProxyIndex, cancellationToken, log);
            log?.Report("OK " + account.Email);
            return true;
        }
        catch (OperationCanceledException)
        {
            account.Status = AccountSyncStatus.Pending;
            db.Accounts.Update(account);
            throw;
        }
        catch (Exception ex)
        {
            account.Status = AccountSyncStatus.Error;
            account.LastError = ex.Message;
            db.Accounts.Update(account);
            // The account row keeps LastError for its tooltip. The shared status bar must
            // stay concise because many parallel accounts report through the same line.
            log?.Report("FAIL " + account.Email);

            // The UI only shows ex.Message, so the full chain has to reach the log file.
            AppLog.Error("sync", "Account " + account.Email + " ended with an error.", ex);
            return false;
        }
    }
}

internal readonly record struct SyncRunResult(int Total, int Success, int Failed, int EffectiveThreads);

internal static class MessageSearchService
{
    public static List<MessageEntity> Search(
        MailDatabase db,
        int accountId,
        int? folderId,
        string query,
        int limit)
    {
        var normalized = query.Trim().ToLowerInvariant();

        if (normalized.Length == 0)
        {
            if (folderId.HasValue)
            {
                // FolderId is unique. LiteDB 5 can return an empty set when Query() ANDs
                // two different indexes (AccountId && FolderId), so the folder view uses
                // Find on FolderId alone.
                return db.Messages
                    .Find(x => x.FolderId == folderId.Value)
                    .OrderByDescending(x => x.DateUtc)
                    .Take(limit)
                    .ToList();
            }

            return db.Messages.Query()
                .Where(x => x.AccountId == accountId)
                .OrderByDescending(x => x.DateUtc)
                .Limit(limit)
                .ToList();
        }

        var source = folderId.HasValue
            ? db.Messages.Find(x => x.FolderId == folderId.Value)
            : db.Messages.Find(x => x.AccountId == accountId);

        return source
            .Where(x => (x.SearchText ?? "").Contains(normalized, StringComparison.Ordinal))
            .OrderByDescending(x => x.DateUtc)
            .Take(limit)
            .ToList();
    }

    public static List<MessageEntity> SearchAll(MailDatabase db, string query, int limit)
    {
        var normalized = query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return [];

        return db.Messages.FindAll()
            .Where(x => (x.SearchText ?? "").Contains(normalized, StringComparison.Ordinal))
            .OrderByDescending(x => x.DateUtc)
            .Take(limit)
            .ToList();
    }
}
