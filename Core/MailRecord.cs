using System.Text;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Core;

internal sealed class MailRecord
{
    public string Email { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string Phone { get; private set; } = "";
    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";

    public static MailRecord Parse(string line, string format, char delimiter)
    {
        var columns = SplitDelimitedLine(format, delimiter)
            .Select(AccountFormat.NormalizeKey)
            .Where(x => x.Length > 0)
            .ToList();

        if (columns.Count == 0)
            throw new FormatException("Account format is empty.");

        var parts = SplitDelimitedLine(line, delimiter);
        if (parts.Count < columns.Count)
        {
            var hasTrailingRefresh = parts.Count >= 4 &&
                                     OAuthTokenClassifier.IsRefreshToken(parts[^1].Trim());
            if (!hasTrailingRefresh)
            {
                throw new FormatException(
                    $"Expected at least {columns.Count} columns ({format}) but got {parts.Count}. Line: {line}");
            }
        }

        var record = new MailRecord();
        var accessFromColumns = "";
        var refreshFromColumns = "";

        for (var i = 0; i < columns.Count && i < parts.Count; i++)
        {
            var value = parts[i].Trim();

            switch (columns[i])
            {
                case "mail":
                    record.Email = value;
                    break;
                case "password":
                    record.Password = value;
                    break;
                case "phone":
                    record.Phone = value;
                    break;
                case "access_token":
                    accessFromColumns = value;
                    break;
                case "refresh_token":
                    refreshFromColumns = value;
                    break;
            }
        }

        (record.AccessToken, record.RefreshToken) = OAuthTokenClassifier.Resolve(
            parts,
            columns,
            accessFromColumns,
            refreshFromColumns);

        if (record.Email.Length == 0)
            throw new FormatException("Mail column is empty. Line: " + line);

        if (!record.Email.Contains('@'))
            throw new FormatException("Mail column is not an email address: " + record.Email);

        if (record.RefreshToken.Length == 0)
        {
            if (record.Password.Length == 0)
            {
                throw new FormatException(
                    "App password (password column) is required when refresh token is empty. Line: " + line);
            }

            return record;
        }

        if (!OAuthTokenClassifier.IsRefreshToken(record.RefreshToken))
            throw new FormatException("Refresh token was not recognized in the last column. Line: " + line);

        return record;
    }

    public static char DetectDelimiter(string format) =>
        format.Contains(':') ? ':' : ',';

    private static List<string> SplitDelimitedLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Replaces OAuth token fields in an existing account line, keeping all other columns unchanged.
    /// </summary>
    public static string ReplaceOAuthTokens(string line, AccountEntity account, string format)
    {
        var delimiter = DetectDelimiter(format);
        var columns = AccountFormat.ParseFormatSequence(format).Select(x => x.Key).ToList();
        var parts = SplitDelimitedLine(line, delimiter);

        for (var i = 0; i < columns.Count && i < parts.Count; i++)
        {
            switch (columns[i])
            {
                case "access_token":
                    parts[i] = account.AccessToken;
                    break;
                case "refresh_token":
                    parts[i] = account.RefreshToken;
                    break;
            }
        }

        if (parts.Count > 0 && OAuthTokenClassifier.IsRefreshToken(parts[^1].Trim()))
            parts[^1] = account.RefreshToken;

        if (parts.Count >= 2)
        {
            var beforeRefresh = parts[^2].Trim();
            if (OAuthTokenClassifier.IsAccessToken(beforeRefresh))
                parts[^2] = account.AccessToken;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var trimmed = parts[i].Trim();
            if (trimmed == account.RefreshToken || !OAuthTokenClassifier.IsAccessToken(trimmed))
                continue;

            parts[i] = account.AccessToken;
            break;
        }

        return string.Join(delimiter, parts);
    }
}
