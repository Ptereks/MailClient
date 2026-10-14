using System.IO;
using System.Windows;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient;

/// <summary>
/// A link clicked in a message opens in <see cref="LinkBrowserWindow"/> behind the proxy of
/// the account the message belongs to. Without a proxy the link is not opened at all.
/// </summary>
internal static class MessageLinks
{
    /// <returns>A status line: what was opened through which proxy, or why it was refused.</returns>
    public static string Open(Window owner, MailDatabase db, int accountId, string uri, out bool opened)
    {
        opened = false;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return "Only http and https links can be opened.";
        }

        var account = db.Accounts.FindById(accountId);
        if (account == null)
            return "Link not opened: the account of this message no longer exists.";

        var proxy = ResolveProxy(db, account);
        if (proxy == null)
            return "Link not opened: no proxy available for " + account.Email + ".";

        try
        {
            new LinkBrowserWindow(proxy, account.Email, parsed.AbsoluteUri) { Owner = owner }.Show();
            opened = true;
            return "Opening link through " + proxy.Summary + ".";
        }
        catch (Exception ex)
        {
            AppLog.Error("link.view", "Could not open link window", ex);
            return "Link not opened: " + ex.Message;
        }
    }

    /// <summary>The proxy the account already uses for IMAP, SMTP and media.</summary>
    public static ProxySpec? ResolveProxy(MailDatabase db, AccountEntity account)
    {
        try
        {
            var proxies = ProxyFile.Load(db);
            return proxies.Count == 0 ? null : proxies[MainWindow.ProxyStartIndex(account, proxies)];
        }
        catch
        {
            return null;
        }
    }
}
