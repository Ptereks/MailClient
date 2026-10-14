using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using System.IO;
using System.Net.Sockets;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient.Services;

/// <summary>
/// One IMAP connection for a full account sync. Up to three reconnects per account run.
/// </summary>
internal sealed class ImapSyncSession
{
    /// <summary>Reconnect budget for one account sync (initial connect does not count).</summary>
    public const int MaxReconnectAttempts = 3;

    private readonly AccountEntity _account;
    private readonly MailDatabase _db;
    private readonly MailEndpoints _endpoints;
    private readonly Func<CancellationToken, Task> _refreshToken;
    private readonly IProxyClient _proxyClient;

    private int _reconnectCount;

    public ImapSyncSession(
        ImapClient client,
        AccountEntity account,
        IProxyClient proxyClient,
        MailDatabase db,
        MailEndpoints endpoints,
        Func<CancellationToken, Task> refreshToken)
    {
        Client = client;
        _account = account;
        _proxyClient = proxyClient;
        _db = db;
        _endpoints = endpoints;
        _refreshToken = refreshToken;
    }

    public ImapClient Client { get; }

    public MailDatabase Database => _db;

    public AccountEntity Account => _account;

    public int ReconnectCount => _reconnectCount;

    public bool CanReconnect => _reconnectCount < MaxReconnectAttempts;

    /// <summary>
    /// Opens a folder after reconnect. The namespace root has an empty <see cref="IMailFolder.FullName"/>
    /// and must be resolved through <see cref="ImapClient.GetFolder(FolderNamespace)"/>, not
    /// <c>GetFolderAsync("")</c> which breaks on Yahoo/AOL.
    /// </summary>
    public async Task<IMailFolder> OpenFolderByFullNameAsync(string fullName, CancellationToken cancellationToken)
    {
        if (!Client.IsConnected)
            throw new ServiceNotConnectedException("The ImapClient is not connected.");

        var folder = string.IsNullOrEmpty(fullName)
            ? Client.GetFolder(Client.PersonalNamespaces[0])
            : await Client.GetFolderAsync(fullName, cancellationToken);

        if (!folder.IsOpen)
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        return folder;
    }

    /// <summary>
    /// Re-establishes the account connection. Counts toward the three-attempt budget for
    /// this sync — not per message.
    /// </summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken)
    {
        if (!CanReconnect)
        {
            AppLog.Error(
                "imap",
                "Reconnect limit reached for " + _account.Email +
                " (" + MaxReconnectAttempts + " attempts used during this sync).");
            return false;
        }

        _reconnectCount++;

        try
        {
            if (Client.IsConnected)
                await Client.DisconnectAsync(false, cancellationToken);
        }
        catch
        {
            // Socket may already be dead.
        }

        try
        {
            await _refreshToken(cancellationToken);

            Client.ProxyClient = _proxyClient;
            await Client.ConnectAsync(
                _endpoints.ImapHost,
                _endpoints.ImapPort,
                SecureSocketOptions.SslOnConnect,
                cancellationToken);
            await OAuthAuthenticator.AuthenticateAsync(
                Client,
                _account.Email,
                _account.AccessToken,
                cancellationToken);

            AppLog.Warning(
                "imap",
                "Reconnected to IMAP for " + _account.Email +
                " (" + _reconnectCount + "/" + MaxReconnectAttempts + ").");

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "imap",
                "Reconnect failed for " + _account.Email +
                " (" + _reconnectCount + "/" + MaxReconnectAttempts + ").",
                ex);
            return false;
        }
    }

    public static bool IsConnectionError(Exception ex)
    {
        while (true)
        {
            if (ex is ImapProtocolException or ServiceNotConnectedException or IOException or SocketException)
                return true;

            if (ex.InnerException == null)
                return false;

            ex = ex.InnerException;
        }
    }
}
