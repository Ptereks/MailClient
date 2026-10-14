namespace ZennoVibe.MailClient.Core;

/// <summary>
/// What an outgoing message says about the machine it was sent from. By default MailKit and
/// MimeKit put the Windows computer name into EHLO and into Message-ID, which ends up in the
/// headers of every mail from every account — one name tying all of them together. These
/// values look like an ordinary desktop client instead, and differ per account.
/// </summary>
internal static class MailIdentity
{
    /// <summary>
    /// EHLO argument. A desktop client behind a home router announces its private LAN address
    /// as an address literal, e.g. <c>[192.168.1.23]</c>; that is what the server records in
    /// the Received header. The address is derived from the account so it stays the same for
    /// that account and differs between accounts.
    /// </summary>
    public static string EhloName(string email)
    {
        var hash = Fnv(email);
        var subnet = (hash >> 8) % 2;          // 192.168.0.x and 192.168.1.x are what home routers hand out
        var host = 2 + (hash % 253);           // .2 – .254, never the router's .1
        return "[192.168." + subnet + "." + host + "]";
    }

    /// <summary>
    /// Domain for the Message-ID: the sender's own mail domain, as webmail and most clients do.
    /// </summary>
    public static string MessageIdDomain(string email)
    {
        var at = email.LastIndexOf('@');
        var domain = at >= 0 && at < email.Length - 1 ? email[(at + 1)..].Trim().ToLowerInvariant() : "";
        return domain.Length > 0 ? domain : "mail.yahoo.com";
    }

    private static uint Fnv(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value)
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;

        return hash;
    }
}
