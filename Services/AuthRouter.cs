using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

internal static class AuthRouter
{
    public static AuthMode Detect(string? refreshToken) =>
        string.IsNullOrWhiteSpace(refreshToken) ? AuthMode.AppPassword : AuthMode.XOAUTH2;

    public static AuthMode Resolve(AccountEntity account)
    {
        if (account.AuthMode == AuthMode.AppPassword)
            return AuthMode.AppPassword;

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            return AuthMode.AppPassword;

        return AuthMode.XOAUTH2;
    }

    public static bool IsAppPassword(AccountEntity account) =>
        Resolve(account) == AuthMode.AppPassword;

    public static void ApplyDetected(AccountEntity account, string? refreshToken) =>
        account.AuthMode = Detect(refreshToken);
}
