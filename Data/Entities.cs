namespace ZennoVibe.MailClient.Data;

using LiteDB;
using ZennoVibe.MailClient.Core;

internal enum AccountSyncStatus
{
    Pending,
    Syncing,
    Ready,
    Error
}

internal sealed class AccountEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Site { get; set; } = "yahoo";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public AuthMode AuthMode { get; set; } = AuthMode.XOAUTH2;

    /// <summary>Higher value is synced before the rest of the run.</summary>
    public int SyncPriority { get; set; }

    /// <summary>True after Inbox/Bulk/Spam of this run were stored and are safe to open.</summary>
    public bool PriorityFoldersReady { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }
    public DateTime? AccessTokenRefreshedAt { get; set; }
    public string? ActiveProxy { get; set; }
    public AccountSyncStatus Status { get; set; } = AccountSyncStatus.Pending;
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public int LastSyncAttempts { get; set; }
    public string? LastRetryError { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public int? GroupId { get; set; }

    /// <summary>User-written. Import and sync never overwrite it.</summary>
    public string Note { get; set; } = "";

    [BsonIgnore]
    public string NoteToolTip =>
        string.IsNullOrWhiteSpace(Note) ? "Add a note" : Note;

    [BsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>
    /// MessageLimit used by the last complete sync. Raising the setting triggers one full
    /// metadata rescan so previously skipped older messages can fill the larger window.
    /// </summary>
    public int SyncedMessageLimit { get; set; }

    /// <summary>
    /// True once every stored message has its attachment flag, so later syncs skip the
    /// mailbox-wide BODYSTRUCTURE scan and only inspect newly arrived mail.
    /// </summary>
    public bool AttachmentBackfillDone { get; set; }

    /// <summary>Phase timings of the last sync in milliseconds; shown in the status bar.</summary>
    public int LastTokenMs { get; set; }
    public int LastConnectMs { get; set; }
    public int LastFoldersMs { get; set; }
    public int LastStoreMs { get; set; }

    [BsonIgnore]
    public string PhaseSummary =>
        LastSyncAt == null
            ? ""
            : "token " + Seconds(LastTokenMs) + " · connect " + Seconds(LastConnectMs) +
              " · folders " + Seconds(LastFoldersMs) + " · store " + Seconds(LastStoreMs);

    internal static string Seconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";

    /// <summary>Transient grouping key populated when the account list is loaded.</summary>
    [BsonIgnore]
    public AccountGroupKey GroupKey { get; set; } = AccountGroupKey.Ungrouped;

    /// <summary>Transient invisible row used only to make an empty group section visible.</summary>
    [BsonIgnore]
    public bool IsGroupPlaceholder { get; set; }

    [BsonIgnore]
    public string? StatusToolTip
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LastError))
                return LastError;

            var text = LastSyncAttempts > 1 && !string.IsNullOrWhiteSpace(LastRetryError)
                ? $"Succeeded after {LastSyncAttempts} attempts. First error: {LastRetryError}"
                : null;

            var phases = PhaseSummary;
            if (phases.Length == 0)
                return text;

            return text == null ? "Last sync: " + phases : text + Environment.NewLine + "Last sync: " + phases;
        }
    }
}

internal sealed class AccountGroupEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
}

/// <summary>Binding value used by WPF's grouped collection view.</summary>
internal sealed record AccountGroupKey(int? Id, string Name, bool IsExpanded, int AccountCount)
{
    public static AccountGroupKey Ungrouped { get; } = new(null, "Ungrouped", true, 0);
}

internal sealed class FolderEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string FullName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int MessageCount { get; set; }
    public int ReadCount { get; set; }
    public bool ReadCountKnown { get; set; }

    [BsonIgnore]
    public string ReadSummary => ReadCountKnown
        ? $"{ReadCount} of {MessageCount} read"
        : $"{MessageCount} total · sync";

    [BsonIgnore]
    public string CountToolTip => ReadCountKnown
        ? $"{DisplayName}: {ReadCount} read messages out of {MessageCount} total."
        : $"{DisplayName}: {MessageCount} total messages. Sync to update the read count.";

    /// <summary>
    /// IMAP UIDVALIDITY. When the server changes it, every stored uid for this folder is
    /// meaningless and the folder has to be fetched from scratch.
    /// </summary>
    public uint UidValidity { get; set; }

    /// <summary>Highest uid already stored, so the next sync only asks for what arrived after it.</summary>
    public uint LastUid { get; set; }
}

/// <summary>
/// Message metadata only. Bodies live in <see cref="MessageBodyEntity"/> so that listing a
/// mailbox does not read megabytes of mail text per query.
/// </summary>
internal sealed class MessageEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public int FolderId { get; set; }
    public uint Uid { get; set; }
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTime DateUtc { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }
    public bool AttachmentInfoKnown { get; set; }
    public List<string> AttachmentFileNames { get; set; } = [];
    public string Preview { get; set; } = "";
    public string SearchText { get; set; } = "";

    /// <summary>RFC 5322 Message-Id without angle brackets; empty for rows synced by older builds.</summary>
    public string MessageId { get; set; } = "";

    /// <summary>
    /// The References header of the message (ids without angle brackets, oldest first), so a
    /// reply can carry the whole thread. Empty for the first message of a thread and for
    /// rows synced by older builds.
    /// </summary>
    public List<string> References { get; set; } = [];

    /// <summary>The thread a reply to this message belongs to: its References plus its own id.</summary>
    [BsonIgnore]
    public IReadOnlyList<string> ThreadReferences
    {
        get
        {
            var chain = new List<string>(References.Where(id => !string.IsNullOrWhiteSpace(id)));
            if (!string.IsNullOrWhiteSpace(MessageId) && !chain.Contains(MessageId))
                chain.Add(MessageId);
            return chain;
        }
    }

    /// <summary>
    /// False while only metadata is stored. The body is then downloaded the first time the
    /// message is opened, which keeps a sync down to one command per folder.
    /// </summary>
    public bool BodyLoaded { get; set; }

    [BsonIgnore]
    public string AttachmentSummary => AttachmentFileNames is { Count: > 0 } names
        ? $"Attachments ({names.Count}): {string.Join(", ", names)}"
        : "";
}

/// <summary>
/// Every address the app has seen, either as an imported account or on a synced message.
/// Notes are user-written and are never touched by a sync.
/// </summary>
internal sealed class ContactEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsOwnAccount { get; set; }
    public int SeenCount { get; set; }
    public DateTime LastSeenUtc { get; set; }

    [BsonIgnore]
    public string NoteOrHint => Note.Length > 0 ? Note : "no note";
}

internal sealed class MessageBodyEntity
{
    /// <summary>Same value as the owning <see cref="MessageEntity.Id"/>.</summary>
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
}

internal readonly record struct ContactSeed(
    string Email,
    string DisplayName,
    bool IsOwnAccount,
    int SeenCount,
    DateTime LastSeenUtc);

internal readonly record struct MessageBody(string Text, string Html)
{
    public static MessageBody Empty { get; } = new("", "");
}

internal sealed class AppSettingsEntity
{
    public int Id { get; set; } = 1;
    public string ProxyFile { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string SourceFormat { get; set; } = AccountFormat.Default;
    public string ExtraFormatTags { get; set; } = "";
    public int Threads { get; set; } = 50;
    public double MessagesPaneWeight { get; set; } = 3;
    public double PreviewPaneWeight { get; set; } = 1;
    public double ComposePaneWeight { get; set; } = 2;
    public double AccountsColumnWeight { get; set; } = 25;
    public double FoldersColumnWeight { get; set; } = 17;
    public double MailColumnWeight { get; set; } = 58;
    public double MessageFromColumnWidth { get; set; } = 300;
    public double MessageSubjectColumnWidth { get; set; } = 300;
    public double MessageDateColumnWidth { get; set; } = 144;
    public double MessageAttachmentColumnWidth { get; set; } = 38;
    public double AddressEmailColumnWidth { get; set; } = 300;
    public double AddressNoteColumnWidth { get; set; } = 420;
    public double AddressMessagesColumnWidth { get; set; } = 90;
    public double AddressLastSeenColumnWidth { get; set; } = 110;

    /// <summary>Toggled by the Compose checkbox in Settings. Off by default.</summary>
    public bool ComposeVisible { get; set; }

    /// <summary>
    /// When on, the message window fetches remote images, fonts, video and stylesheets
    /// through the account proxy. Off by default so a sender never sees the real IP.
    /// </summary>
    public bool LoadMedia { get; set; }

    /// <summary>
    /// Newest messages taken from the whole mailbox during the initial history load. Already
    /// stored mail remains, and later syncs append every newly arrived message.
    /// </summary>
    public int MessageLimit { get; set; } = 200;

}
