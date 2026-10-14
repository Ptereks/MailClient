namespace ZennoVibe.MailClient.Core;

internal static class MailSite
{
    public static string Detect(string email)
    {
        var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "";
        return domain.Contains("aol") ? "aol" : "yahoo";
    }

    public static MailEndpoints GetEndpoints(string site) =>
        site == "aol" ? MailEndpoints.Aol : MailEndpoints.Yahoo;
}

internal readonly record struct MailEndpoints(
    string ImapHost,
    int ImapPort,
    string PopHost,
    int PopPort,
    string SmtpHost,
    int SmtpPort)
{
    public static MailEndpoints Yahoo { get; } = new(
        "imap.mail.yahoo.com", 993,
        "pop.mail.yahoo.com", 995,
        "smtp.mail.yahoo.com", 465);

    public static MailEndpoints Aol { get; } = new(
        "imap.aol.com", 993,
        "pop.aol.com", 995,
        "smtp.aol.com", 465);
}
