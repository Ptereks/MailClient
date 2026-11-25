using System.IO;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

internal sealed class AccountExportService
{
    public AccountSaveResult SaveAll(MailDatabase db, string filePath, string format)
    {
        return SaveAccounts(
            db.Accounts.Query().OrderBy(x => x.Email).ToList(),
            filePath,
            format);
    }

    /// <summary>
    /// Accounts whose last sync succeeded. Each line is written the way that account was imported:
    /// <c>mail:password</c>, <c>mail:password:phone:app_password</c>, or the Settings column order.
    /// </summary>
    public AccountSaveResult SaveLive(MailDatabase db, string filePath, string format)
    {
        // IsGroupPlaceholder is not stored, so it must not appear in a LiteDB query.
        var live = db.Accounts
            .Find(x => x.Status == AccountSyncStatus.Ready)
            .ToList();
        return SaveAccounts(live, filePath, format, keepImportedShape: true);
    }

    public AccountSaveResult SaveAccounts(
        IReadOnlyList<AccountEntity> accounts,
        string filePath,
        string format) =>
        SaveAccounts(accounts, filePath, format, keepImportedShape: false);

    private AccountSaveResult SaveAccounts(
        IReadOnlyList<AccountEntity> accounts,
        string filePath,
        string format,
        bool keepImportedShape)
    {
        var ordered = accounts
            .Where(x => !x.IsGroupPlaceholder)
            .OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columns = AccountFormat.ParseFormatSequence(format);
        var delimiter = MailRecord.DetectDelimiter(format);
        var lines = ordered
            .Select(account => keepImportedShape
                ? FormatImported(account) ?? FormatAccount(account, columns, delimiter)
                : FormatAccount(account, columns, delimiter))
            .ToArray();

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // Replace atomically enough that a failed write does not destroy an existing export.
        var tempPath = filePath + ".tmp";
        File.WriteAllLines(tempPath, lines);
        File.Move(tempPath, filePath, overwrite: true);

        return new AccountSaveResult(filePath, ordered.Count);
    }

    /// <summary>
    /// The short import shapes. Null means the account uses the Settings column order.
    /// Rows imported before the shape was stored are recognized from the fields they kept.
    /// </summary>
    private static string? FormatImported(AccountEntity account)
    {
        var kind = account.ImportedLineKind;
        if (kind == ImportedLineKind.SettingsFormat)
        {
            if (!string.IsNullOrEmpty(account.RefreshToken) || !string.IsNullOrEmpty(account.AccessToken))
                return null;

            if (!string.IsNullOrEmpty(account.MailboxPassword))
                kind = ImportedLineKind.MailPasswordPhoneApp;
            else if (string.IsNullOrEmpty(account.Phone) && AuthRouter.IsAppPassword(account))
                kind = ImportedLineKind.MailAndPassword;
            else
                return null;
        }

        var signIn = account.Password ?? "";
        var mailbox = string.IsNullOrEmpty(account.MailboxPassword) ? signIn : account.MailboxPassword;

        return kind switch
        {
            ImportedLineKind.MailAndPassword =>
                string.Join(':', new[] { account.Email, signIn }.Select(value => Escape(value, ':'))),
            ImportedLineKind.MailPasswordPhoneApp =>
                string.Join(':', new[] { account.Email, mailbox, account.Phone ?? "", signIn }.Select(value => Escape(value, ':'))),
            _ => null
        };
    }

    private static string FormatAccount(
        AccountEntity account,
        IReadOnlyList<FormatFieldItem> columns,
        char delimiter)
    {
        var values = columns.Select(column => column.Key switch
        {
            "mail" => account.Email,
            "password" => string.IsNullOrEmpty(account.MailboxPassword) ? account.Password : account.MailboxPassword,
            "phone" => account.Phone,
            "access_token" => account.AccessToken,
            // The app password rides in the refresh column only so a later import finds it again.
            "refresh_token" => account.RefreshToken.Length == 0 &&
                               account.MailboxPassword.Length > 0 &&
                               account.MailboxPassword != account.Password
                ? account.Password
                : account.RefreshToken,
            _ => ""
        });

        return string.Join(delimiter, values.Select(value => Escape(value, delimiter)));
    }

    private static string Escape(string value, char delimiter)
    {
        if (!value.Contains(delimiter) &&
            !value.Contains('"') &&
            !value.Contains('\r') &&
            !value.Contains('\n'))
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}

internal readonly record struct AccountSaveResult(string FilePath, int AccountCount);
