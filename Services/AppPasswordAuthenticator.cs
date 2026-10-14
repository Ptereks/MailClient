using MailKit;

namespace ZennoVibe.MailClient.Services;

/// <summary>
/// LOGIN/PLAIN for Yahoo and AOL app passwords. Called only after AuthRouter
/// has already chosen AppPassword, and never from the frozen XOAUTH2 connect block.
/// </summary>
internal static class AppPasswordAuthenticator
{
    public static Task AuthenticateAsync(
        IMailService client,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("App password is empty for " + email + ".");

        return client.AuthenticateAsync(email, password, cancellationToken);
    }
}
