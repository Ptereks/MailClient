using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

internal sealed class TokenService
{
    /// <summary>
    /// A token that expires while a mailbox is being walked fails mid-sync, so it is
    /// treated as stale slightly before the server-declared expiry.
    /// </summary>
    private static readonly TimeSpan ExpiryGuard = TimeSpan.FromMinutes(2);

    public static DateTime ComputeAccessTokenExpiry(int expiresInSeconds) =>
        DateTime.UtcNow.AddSeconds(Math.Clamp(expiresInSeconds, 60, 7200));

    /// <summary>
    /// Access tokens obtained via OAuth refresh are valid for about one hour.
    /// Tokens copied from an account file have unknown age and must not skip refresh.
    /// </summary>
    public static bool HasFreshAccessToken(AccountEntity account) =>
        !string.IsNullOrWhiteSpace(account.AccessToken) &&
        account.AccessTokenRefreshedAt.HasValue &&
        account.AccessTokenExpiresAt.HasValue &&
        account.AccessTokenExpiresAt.Value - ExpiryGuard > DateTime.UtcNow;

    public async Task<(string AccessToken, string RefreshToken, DateTime ExpiresAt)> EnsureAccessTokenAsync(
        AccountEntity account,
        ProxySpec? proxy,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!forceRefresh && HasFreshAccessToken(account))
        {
            return (
                account.AccessToken,
                account.RefreshToken,
                account.AccessTokenExpiresAt!.Value);
        }

        var response = await YahooAolTokenClient.RefreshAsync(
            account.Email,
            account.RefreshToken,
            account.Site,
            proxy,
            cancellationToken);

        var refreshToken = string.IsNullOrWhiteSpace(response.RefreshToken)
            ? account.RefreshToken
            : response.RefreshToken;

        var expiresAt = ComputeAccessTokenExpiry(response.ExpiresInSeconds);
        return (response.AccessToken, refreshToken, expiresAt);
    }

    public static void ApplyToAccount(AccountEntity account, string accessToken, string refreshToken, DateTime expiresAt)
    {
        account.AccessToken = accessToken;
        account.RefreshToken = refreshToken;
        account.AccessTokenExpiresAt = expiresAt;
        account.AccessTokenRefreshedAt = DateTime.UtcNow;
    }

    public static void InvalidateAccessToken(AccountEntity account)
    {
        account.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        account.AccessTokenRefreshedAt = null;
    }
}
