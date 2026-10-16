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

    public AccountSaveResult SaveAccounts(
        IReadOnlyList<AccountEntity> accounts,
        string filePath,
        string format)
    {
        var ordered = accounts
            .Where(x => !x.IsGroupPlaceholder)
            .OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columns = AccountFormat.ParseFormatSequence(format);
        var delimiter = MailRecord.DetectDelimiter(format);
        var lines = ordered
            .Select(account => FormatAccount(account, columns, delimiter))
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
