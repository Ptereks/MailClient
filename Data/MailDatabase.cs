using System.IO;
using LiteDB;

namespace ZennoVibe.MailClient.Data;

internal sealed class MailDatabase : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly object _idSeedLock = new();
    private readonly object _contactLock = new();
    private Dictionary<string, ContactSeed>? _pendingContacts;
    private long _messageIdSeed = -1;

    public MailDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        _db = OpenEncrypted(databasePath, directory);

        Accounts = _db.GetCollection<AccountEntity>("accounts");
        AccountGroups = _db.GetCollection<AccountGroupEntity>("account_groups");
        Folders = _db.GetCollection<FolderEntity>("folders");
        Messages = _db.GetCollection<MessageEntity>("messages");
        MessageBodies = _db.GetCollection<MessageBodyEntity>("message_bodies");
        Contacts = _db.GetCollection<ContactEntity>("contacts");
        Settings = _db.GetCollection<AppSettingsEntity>("settings");

        Accounts.EnsureIndex(x => x.Email, unique: true);
        AccountGroups.EnsureIndex(x => x.NormalizedName, unique: true);
        Contacts.EnsureIndex(x => x.Email, unique: true);
        Folders.EnsureIndex(x => x.AccountId);
        Messages.EnsureIndex(x => x.AccountId);
        Messages.EnsureIndex(x => x.FolderId);
        Messages.EnsureIndex(x => x.DateUtc);
        MessageBodies.EnsureIndex(x => x.AccountId);

        // SearchText holds message text and exceeds the LiteDB 1023-byte index key limit,
        // so it must stay unindexed and be filtered in memory instead. Older databases
        // still carry that index and every insert fails until it is gone.
        try
        {
            Messages.DropIndex(nameof(MessageEntity.SearchText));
        }
        catch
        {
            // No such index on a fresh database.
        }

        // A process can be closed while accounts are in flight. "Syncing" is not a durable
        // state; leaving it behind makes the next launch look permanently stuck.
        foreach (var account in Accounts.Find(x => x.Status == AccountSyncStatus.Syncing))
        {
            account.Status = AccountSyncStatus.Pending;
            Accounts.Update(account);
        }
    }

    public ILiteCollection<AccountEntity> Accounts { get; }
    public ILiteCollection<AccountGroupEntity> AccountGroups { get; }
    public ILiteCollection<FolderEntity> Folders { get; }
    public ILiteCollection<MessageEntity> Messages { get; }
    public ILiteCollection<MessageBodyEntity> MessageBodies { get; }
    public ILiteCollection<ContactEntity> Contacts { get; }
    public ILiteCollection<AppSettingsEntity> Settings { get; }

    public static string DefaultPath => AppPaths.DatabaseFile;

    /// <summary>
    /// The file on disk is always AES-encrypted with a per-user key. A database written by
    /// an older build, or an unencrypted export dropped into place, is encrypted on first
    /// open so nothing is ever left readable next to the executable.
    /// </summary>
    private static LiteDatabase OpenEncrypted(string databasePath, string directory)
    {
        var password = DatabaseKeyStore.TryLoad(directory);
        var databaseExists = File.Exists(databasePath);

        if (password == null)
        {
            password = DatabaseKeyStore.Create(directory);
            if (databaseExists)
                EncryptInPlace(databasePath, password);

            return Open(databasePath, password);
        }

        try
        {
            return Open(databasePath, password);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // The file is open in another process — almost always a second copy of this
            // program from the same folder. Has nothing to do with the key.
            throw new InvalidOperationException(
                "Mail Client is already running from this folder, or another program holds " +
                databasePath + ". Close it and start Mail Client again.",
                ex);
        }
        catch (Exception) when (databaseExists && IsPlainDatabase(databasePath))
        {
            EncryptInPlace(databasePath, password);
            return Open(databasePath, password);
        }
        catch (Exception ex) when (databaseExists && ex is LiteException or IOException)
        {
            throw new InvalidOperationException(
                "The database " + databasePath + " cannot be opened with the key stored next to it. " +
                "It was probably copied from another machine together with its key file. Restore an " +
                "unencrypted export from Settings, or delete the data\\DB folder to start fresh.",
                ex);
        }
    }

    private static LiteDatabase Open(string databasePath, string? password) =>
        new(new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Direct,
            Password = password
        });

    private static bool IsSharingViolation(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            // ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).
            if (current is IOException && (current.HResult & 0xFFFF) is 32 or 33)
                return true;
        }

        return false;
    }

    private static bool IsPlainDatabase(string databasePath)
    {
        try
        {
            using var probe = Open(databasePath, null);
            _ = probe.GetCollectionNames().ToList();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// LiteDB's own <c>Rebuild</c> with a password writes the encrypted file but then fails
    /// to reopen it, so the copy is made by hand: every collection is written into a new
    /// encrypted file, verified, and only then swapped in for the plain one.
    /// </summary>
    private static void EncryptInPlace(string databasePath, string password)
    {
        var directory = Path.GetDirectoryName(databasePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(databasePath);
        var encrypted = Path.Combine(directory, stem + ".encrypting.db");
        var encryptedLog = Path.Combine(directory, stem + ".encrypting-log.db");
        var plainLog = Path.Combine(directory, stem + "-log.db");

        File.Delete(encrypted);
        File.Delete(encryptedLog);

        Dictionary<string, int> counts;
        using (var plain = Open(databasePath, null))
        using (var target = Open(encrypted, password))
        {
            counts = CopyAllCollections(plain, target);
        }

        using (var verify = Open(encrypted, password))
        {
            foreach (var (name, expected) in counts)
            {
                if (verify.GetCollection(name).Count() != expected)
                    throw new InvalidOperationException("Encrypted copy of '" + name + "' is incomplete.");
            }
        }

        // Swap with a safety net: the plain file is only deleted once the encrypted one sits
        // under the real name. If the move fails, the original is still there as *.plain-backup.
        var backup = Path.Combine(directory, stem + ".plain-backup.db");
        File.Delete(backup);
        File.Move(databasePath, backup);
        try
        {
            File.Move(encrypted, databasePath);
        }
        catch
        {
            File.Move(backup, databasePath, overwrite: true);
            throw;
        }

        File.Delete(backup);
        File.Delete(plainLog);
        File.Delete(encryptedLog);
    }

    private static Dictionary<string, int> CopyAllCollections(LiteDatabase source, LiteDatabase target)
    {
        var counts = new Dictionary<string, int>();
        foreach (var name in source.GetCollectionNames())
        {
            var documents = source.GetCollection(name).FindAll().ToList();
            counts[name] = documents.Count;
            if (documents.Count > 0)
                target.GetCollection(name).InsertBulk(documents);
        }

        target.Checkpoint();
        return counts;
    }

    /// <summary>
    /// Writes a plain, unencrypted copy of every collection for moving the mailbox store to
    /// another machine or user. The copy has no key file and is readable by any build.
    /// </summary>
    public void ExportUnencrypted(string targetPath)
    {
        if (string.Equals(
                Path.GetFullPath(targetPath),
                Path.GetFullPath(AppPaths.DatabaseFile),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a location outside the live database.");
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = Path.Combine(
            directory ?? "",
            Path.GetFileNameWithoutExtension(targetPath) + ".export-tmp.db");
        var temporaryLog = Path.Combine(
            directory ?? "",
            Path.GetFileNameWithoutExtension(temporary) + "-log.db");
        File.Delete(temporary);
        File.Delete(temporaryLog);

        _db.Checkpoint();

        try
        {
            using (var target = Open(temporary, null))
                CopyAllCollections(_db, target);

            File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
                File.Delete(temporaryLog);
            }
            catch
            {
                // Leftover temp files are harmless; the export itself already succeeded or failed.
            }
        }
    }

    /// <summary>
    /// Message ids are assigned by the application instead of LiteDB auto-id so that a
    /// message and its body can be bulk-inserted in one pass without a read-back.
    /// </summary>
    public int NextMessageId()
    {
        if (Volatile.Read(ref _messageIdSeed) < 0)
        {
            lock (_idSeedLock)
            {
                if (_messageIdSeed < 0)
                    _messageIdSeed = Messages.Count() == 0 ? 0 : Messages.Max(x => x.Id);
            }
        }

        return (int)Interlocked.Increment(ref _messageIdSeed);
    }

    /// <summary>
    /// Folders are reused between syncs so their uid bookkeeping survives, and so message rows
    /// keep pointing at the same folder id.
    /// </summary>
    public FolderEntity GetOrCreateFolder(int accountId, string fullName, string displayName)
    {
        var existing = Folders.FindOne(x => x.AccountId == accountId && x.FullName == fullName);
        if (existing != null)
        {
            existing.DisplayName = displayName;
            return existing;
        }

        var created = new FolderEntity
        {
            AccountId = accountId,
            FullName = fullName,
            DisplayName = displayName
        };

        Folders.Insert(created);
        return created;
    }

    /// <summary>Drops one folder's mail, used when the server reissues its uids.</summary>
    public void DeleteFolderMessages(int folderId)
    {
        var ids = Messages.Find(x => x.FolderId == folderId).Select(x => x.Id).ToList();

        Messages.DeleteMany(x => x.FolderId == folderId);

        foreach (var id in ids)
            MessageBodies.Delete(id);
    }

    /// <summary>Stores a body fetched after the sync, when the user opens an older message.</summary>
    public void SaveMessageBody(MessageEntity message, string text, string html)
    {
        MessageBodies.Upsert(new MessageBodyEntity
        {
            Id = message.Id,
            AccountId = message.AccountId,
            TextBody = text,
            HtmlBody = html
        });

        var searchableBody = !string.IsNullOrWhiteSpace(text)
            ? text
            : MessageBodyDocument.StripHtml(html);
        var normalized = searchableBody.Replace('\r', ' ').Replace('\n', ' ').Trim();

        message.BodyLoaded = true;
        if (normalized.Length > 0)
        {
            message.Preview = normalized.Length > 240 ? normalized[..240] : normalized;
            var indexedBody = normalized.Length > 4_000 ? normalized[..4_000] : normalized;
            message.SearchText = string.Join(' ', message.Subject, message.From, message.To, indexedBody)
                .ToLowerInvariant();
        }

        if (!Messages.Update(message))
        {
            // The account may have been deleted while its body was downloading. Do not
            // leave a body document with no owning message.
            MessageBodies.Delete(message.Id);
        }
    }

    public MessageBody LoadMessageBody(int messageId)
    {
        var stored = MessageBodies.FindById(messageId);
        if (stored != null)
            return new MessageBody(Normalize(stored.TextBody), Normalize(stored.HtmlBody));

        // Databases written before bodies moved to their own collection keep them inline
        // on the message document. Read those raw so old mail stays readable until re-sync.
        var legacy = _db.GetCollection("messages").FindById(messageId);
        if (legacy == null)
            return MessageBody.Empty;

        return new MessageBody(
            ReadLegacyField(legacy, nameof(MessageBodyEntity.TextBody), "TextBody"),
            ReadLegacyField(legacy, nameof(MessageBodyEntity.HtmlBody), "HtmlBody"));
    }

    private static string Normalize(string? value) => value ?? "";

    private static string ReadLegacyField(LiteDB.BsonDocument doc, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!doc.TryGetValue(key, out var value) || value.IsNull)
                continue;

            var text = value.AsString;
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return "";
    }

    /// <summary>
    /// Merges seen addresses into the address book. Existing notes are preserved, so this is
    /// safe to call on every sync.
    /// </summary>
    public void MergeContacts(IEnumerable<ContactSeed> seeds)
    {
        lock (_contactLock)
        {
            var normalized = NormalizeContactSeeds(seeds).ToList();
            if (_pendingContacts != null)
            {
                foreach (var seed in normalized)
                {
                    if (_pendingContacts.TryGetValue(seed.Email, out var pending))
                    {
                        _pendingContacts[seed.Email] = pending with
                        {
                            DisplayName = pending.DisplayName.Length > 0
                                ? pending.DisplayName
                                : seed.DisplayName,
                            IsOwnAccount = pending.IsOwnAccount || seed.IsOwnAccount,
                            SeenCount = pending.SeenCount + seed.SeenCount,
                            LastSeenUtc = seed.LastSeenUtc > pending.LastSeenUtc
                                ? seed.LastSeenUtc
                                : pending.LastSeenUtc
                        };
                    }
                    else
                    {
                        _pendingContacts.Add(seed.Email, seed);
                    }
                }

                return;
            }

            MergeContactsCore(normalized);
        }
    }

    /// <summary>
    /// During a large sync, collect contacts in memory so account workers never queue behind
    /// hundreds of indexed LiteDB writes. CompleteContactBatch merges each address once.
    /// </summary>
    public void BeginContactBatch()
    {
        lock (_contactLock)
            _pendingContacts ??= new Dictionary<string, ContactSeed>(StringComparer.OrdinalIgnoreCase);
    }

    public void CompleteContactBatch()
    {
        lock (_contactLock)
        {
            if (_pendingContacts == null)
                return;

            var pending = _pendingContacts.Values.ToList();
            _pendingContacts = null;
            MergeContactsCore(pending);
        }
    }

    private static IEnumerable<ContactSeed> NormalizeContactSeeds(IEnumerable<ContactSeed> seeds)
    {
        foreach (var raw in seeds)
        {
            var email = raw.Email.Trim().ToLowerInvariant();
            if (email.Length > 0)
                yield return raw with { Email = email };
        }
    }

    private void MergeContactsCore(IEnumerable<ContactSeed> seeds)
    {
        foreach (var seed in seeds)
        {
            var existing = Contacts.FindOne(x => x.Email == seed.Email);
            if (existing == null)
            {
                Contacts.Insert(new ContactEntity
                {
                    Email = seed.Email,
                    DisplayName = seed.DisplayName,
                    IsOwnAccount = seed.IsOwnAccount,
                    SeenCount = seed.SeenCount,
                    LastSeenUtc = seed.LastSeenUtc
                });
                continue;
            }

            existing.SeenCount += seed.SeenCount;
            existing.IsOwnAccount |= seed.IsOwnAccount;

            if (existing.DisplayName.Length == 0 && seed.DisplayName.Length > 0)
                existing.DisplayName = seed.DisplayName;

            if (seed.LastSeenUtc > existing.LastSeenUtc)
                existing.LastSeenUtc = seed.LastSeenUtc;

            Contacts.Update(existing);
        }
    }

    public void SaveContactNote(int contactId, string note)
    {
        var contact = Contacts.FindById(contactId);
        if (contact == null)
            return;

        contact.Note = note;
        Contacts.Update(contact);
    }

    public AccountGroupEntity CreateAccountGroup(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Group name cannot be empty.");

        var normalized = trimmed.ToLowerInvariant();
        if (AccountGroups.FindOne(x => x.NormalizedName == normalized) != null)
            throw new InvalidOperationException("A group with this name already exists.");

        var group = new AccountGroupEntity
        {
            Name = trimmed,
            NormalizedName = normalized,
            IsExpanded = true
        };

        AccountGroups.Insert(group);
        return group;
    }

    public void MoveAccountsToGroup(IEnumerable<int> accountIds, int? groupId)
    {
        if (groupId.HasValue && AccountGroups.FindById(groupId.Value) == null)
            throw new InvalidOperationException("The selected group no longer exists.");

        foreach (var id in accountIds.Distinct())
        {
            var account = Accounts.FindById(id);
            if (account == null)
                continue;

            account.GroupId = groupId;
            Accounts.Update(account);
        }
    }

    public void SetAccountGroupExpanded(int groupId, bool isExpanded)
    {
        var group = AccountGroups.FindById(groupId);
        if (group == null || group.IsExpanded == isExpanded)
            return;

        group.IsExpanded = isExpanded;
        AccountGroups.Update(group);
    }

    public void DeleteAccountGroup(int groupId, bool deleteAccounts)
    {
        var accountIds = Accounts
            .Find(x => x.GroupId == groupId)
            .Select(x => x.Id)
            .ToList();

        if (deleteAccounts)
        {
            foreach (var accountId in accountIds)
                DeleteAccount(accountId);
        }
        else
        {
            MoveAccountsToGroup(accountIds, null);
        }

        AccountGroups.Delete(groupId);
    }

    public AppSettingsEntity GetOrCreateSettings()
    {
        var raw = _db.GetCollection("settings").FindById(1);
        var settings = Settings.FindById(1);
        if (settings != null)
        {
            var changed = false;

            // LiteDB maps by property name. Preserve the user's numeric choice from builds
            // where the same UI field was still called MessagesPerFolder.
            if (raw != null &&
                !raw.ContainsKey(nameof(AppSettingsEntity.MessageLimit)) &&
                raw.TryGetValue("MessagesPerFolder", out var legacyLimit) &&
                legacyLimit.IsInt32)
            {
                settings.MessageLimit = Math.Clamp(legacyLimit.AsInt32, 10, 2000);
                changed = true;
            }

            if (settings.Threads is < 1 or > 500)
            {
                settings.Threads = settings.Threads < 1 ? 50 : 500;
                changed = true;
            }

            // Covers databases created before MessageLimit replaced MessagesPerFolder, as
            // well as manually damaged settings documents.
            if (settings.MessageLimit is < 10 or > 2000)
            {
                settings.MessageLimit = 200;
                changed = true;
            }

            // Move only the untouched legacy default to the wider Accounts layout. Any
            // dimensions the user dragged remain authoritative.
            if (Math.Abs(settings.AccountsColumnWeight - 19) < 0.001 &&
                Math.Abs(settings.FoldersColumnWeight - 18) < 0.001 &&
                Math.Abs(settings.MailColumnWeight - 63) < 0.001)
            {
                settings.AccountsColumnWeight = 22;
                settings.MailColumnWeight = 60;
                changed = true;
            }

            if (changed)
                Settings.Update(settings);

            return settings;
        }

        settings = new AppSettingsEntity
        {
            ProxyFile = AppPaths.DefaultProxyFile,
            SourceFile = AppPaths.DefaultAccountFile
        };
        Settings.Insert(settings);
        return settings;
    }

    public void SaveSettings(AppSettingsEntity settings)
    {
        settings.Id = 1;
        Settings.Upsert(settings);
    }

    public void DeleteAccountData(int accountId)
    {
        MessageBodies.DeleteMany(x => x.AccountId == accountId);
        Messages.DeleteMany(x => x.AccountId == accountId);
        Folders.DeleteMany(x => x.AccountId == accountId);
    }

    public void DeleteAccount(int accountId)
    {
        DeleteAccountData(accountId);
        Accounts.Delete(accountId);
    }

    public void Dispose() => _db.Dispose();
}
