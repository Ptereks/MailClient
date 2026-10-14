namespace ZennoVibe.MailClient.Core;

internal sealed record FormatFieldItem(string Key, string Title);

internal static class AccountFormat
{
    public const string Default = "mail:password:phone:access_token:refresh_token";

    public static readonly string[] CoreKeys = ["mail", "password", "refresh_token"];

    public static readonly string[] BuiltInExtraKeys = ["phone", "access_token"];

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mail"] = "Mail",
        ["password"] = "Password",
        ["phone"] = "Phone",
        ["refresh_token"] = "Refresh token",
        ["access_token"] = "Access token"
    };

    public static bool IsCoreKey(string key) =>
        CoreKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static bool IsBuiltInExtra(string key) =>
        BuiltInExtraKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static bool IsExtraKey(string key) =>
        !IsCoreKey(key);

    public static FormatFieldItem CreateItem(string key) => ToItem(key);

    public static IReadOnlyList<FormatFieldItem> ParseFormatSequence(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return CoreKeys.Select(ToItem).ToList();

        var delimiter = MailRecord.DetectDelimiter(format);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FormatFieldItem>();

        foreach (var raw in format.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = NormalizeKey(raw);
            if (key.Length == 0 || !seen.Add(key))
                continue;

            result.Add(ToItem(key));
        }

        return result;
    }

    public static IReadOnlyList<string> ParseExtraTagKeys(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Array.Empty<string>();

        return stored.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeKey)
            .Where(x => x.Length > 0 && IsExtraKey(x) && !IsBuiltInExtra(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? NormalizeCustomTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = NormalizeKey(value);
        if (normalized.Length == 0)
            return null;

        if (!normalized.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            return null;

        if (IsCoreKey(normalized))
            return null;

        return normalized;
    }

    public static string ToFormatString(IEnumerable<FormatFieldItem> fields) =>
        string.Join(':', fields.Select(x => x.Key));

    public static string ToFormatString(IEnumerable<string> keys) =>
        string.Join(':', keys);

    public static string ToExtraTagsString(IEnumerable<string> keys) =>
        string.Join(':', keys.Where(k => IsExtraKey(k) && !IsBuiltInExtra(k)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static FormatFieldItem ToItem(string key) =>
        new(key, Titles.TryGetValue(key, out var title) ? title : key.Replace('_', ' '));

    public static string NormalizeKey(string value)
    {
        var normalized = value.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');

        return normalized switch
        {
            "email" => "mail",
            "e_mail" => "mail",
            "pass" => "password",
            "pwd" => "password",
            "telephone" => "phone",
            "mobile" => "phone",
            "refresh" => "refresh_token",
            "token" => "access_token",
            "access" => "access_token",
            _ => normalized
        };
    }
}
