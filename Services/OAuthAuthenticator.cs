using MailKit;
using MailKit.Security;

namespace ZennoVibe.MailClient.Services;

internal static class OAuthAuthenticator
{
    public static async Task AuthenticateAsync(
        IMailService client,
        string email,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (client.AuthenticationMechanisms.Contains("OAUTHBEARER"))
        {
            await client.AuthenticateAsync(new SaslMechanismOAuthBearer(email, accessToken), cancellationToken);
            return;
        }

        if (client.AuthenticationMechanisms.Contains("XOAUTH2"))
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(email, accessToken), cancellationToken);
            return;
        }

        throw new AuthenticationException("Server does not support OAUTHBEARER or XOAUTH2.");
    }
}
