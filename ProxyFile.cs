using System.IO;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient;

/// <summary>The proxy list from the file chosen in Settings. One place to read it from.</summary>
internal static class ProxyFile
{
    public static string ResolvePath(MailDatabase db)
    {
        var settings = db.GetOrCreateSettings();
        return AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);
    }

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="FormatException">The file has no usable proxy line.</exception>
    public static List<ProxySpec> Load(MailDatabase db)
    {
        var proxyPath = ResolvePath(db);
        if (!File.Exists(proxyPath))
            throw new FileNotFoundException("Proxy file not found.", proxyPath);

        return ProxySpec.LoadFromFile(proxyPath);
    }
}
