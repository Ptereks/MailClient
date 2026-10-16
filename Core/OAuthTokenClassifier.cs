namespace ZennoVibe.MailClient.Core;

internal static class OAuthTokenClassifier
{
    /// <summary>
    /// Yahoo/AOL refresh tokens contain ~001~ and are much shorter than access tokens.
    /// Examples: AFjEiGrXwP5HXli6qr28uQBUZzkY~001~GgMYG9fdEI8vjKoKN_GtMBP2_Jdm_bByDA--
    /// </summary>
    public static bool IsRefreshToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Contains("~001~", StringComparison.Ordinal))
            return true;

        return value.Length < 250 &&
               value.EndsWith("--", StringComparison.Ordinal) &&
               !value.Contains('.');
    }

    /// <summary>
    /// Yahoo and AOL app passwords are 16 letters, sometimes shown in groups of four.
    /// A refresh token is never this shape.
    /// </summary>
    public static bool IsAppPassword(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsRefreshToken(value) || IsAccessToken(value))
            return false;

        var compact = NormalizeAppPassword(value);
        return compact.Length == 16 && compact.All(char.IsLetter);
    }

    public static string NormalizeAppPassword(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Access tokens are long JWT-like strings with many dot-separated segments.
    /// </summary>
    public static bool IsAccessToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsRefreshToken(value))
            return false;

        return value.Length > 250 || value.Count(c => c == '.') >= 3;
    }

    public static (string AccessToken, string RefreshToken) Resolve(
        IReadOnlyList<string> parts,
        IReadOnlyList<string> columns,
        string accessFromColumns,
        string refreshFromColumns)
    {
        var lastPart = parts[^1].Trim();

        // Last colon-separated field is the refresh token for AOL/Yahoo account lines.
        if (IsRefreshToken(lastPart))
        {
            var access = FindAccessToken(parts, columns, lastPart, accessFromColumns, refreshFromColumns);
            return (access, lastPart);
        }

        if (IsRefreshToken(refreshFromColumns))
            return (accessFromColumns, refreshFromColumns);

        if (IsRefreshToken(accessFromColumns))
            return ("", accessFromColumns);

        return (accessFromColumns, refreshFromColumns);
    }

    private static string FindAccessToken(
        IReadOnlyList<string> parts,
        IReadOnlyList<string> columns,
        string refresh,
        string accessFromColumns,
        string refreshFromColumns)
    {
        if (IsAccessToken(accessFromColumns) && accessFromColumns != refresh)
            return accessFromColumns;

        if (parts.Count >= 2)
        {
            var beforeRefresh = parts[^2].Trim();
            if (beforeRefresh != refresh && IsAccessToken(beforeRefresh))
                return beforeRefresh;
        }

        for (var i = columns.Count - 1; i >= 0 && i < parts.Count; i--)
        {
            if (columns[i] is not ("access_token" or "refresh_token"))
                continue;

            var candidate = parts[i].Trim();
            if (candidate != refresh && IsAccessToken(candidate))
                return candidate;
        }

        return parts
            .Select(x => x.Trim())
            .Where(x => x != refresh && IsAccessToken(x))
            .OrderByDescending(x => x.Length)
            .FirstOrDefault() ?? "";
    }
}
