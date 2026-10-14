using System.IO;

namespace ZennoVibe.MailClient;

internal static class AppPaths
{
    // Directories are created once instead of on every property read, since these
    // paths are touched on hot paths such as import, sync and error logging.
    private static readonly Lazy<string> DataDir =
        new(() => EnsureDirectory(Path.Combine(AppContext.BaseDirectory, "data")));

    private static readonly Lazy<string> DbDir =
        new(() => EnsureDirectory(Path.Combine(DataDir.Value, "DB")));

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string DataDirectory => DataDir.Value;

    public static string DatabaseDirectory => DbDir.Value;

    public static string DatabaseFile => Path.Combine(DatabaseDirectory, "mailclient.db");

    public static string DefaultProxyFile => Path.Combine(DataDirectory, "proxy.txt");

    public static string DefaultAccountFile => Path.Combine(DataDirectory, "account.txt");

    public static string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");

    public static string WebView2UserDataDirectory => Path.Combine(DataDirectory, "webview2");

    public static string WebView2DocumentationDirectory => Path.Combine(WebView2UserDataDirectory, "docs");

    public static string WebView2LinksDirectory => Path.Combine(WebView2UserDataDirectory, "links");

    /// <summary>
    /// Returns the saved path when it points to an existing custom file; otherwise the default
    /// path under the program data folder.
    /// </summary>
    public static string ResolveDataFilePath(string? savedPath, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(savedPath))
            return defaultPath;

        try
        {
            var normalizedSaved = Path.GetFullPath(savedPath.Trim());
            var normalizedDefault = Path.GetFullPath(defaultPath);

            if (normalizedSaved.Equals(normalizedDefault, StringComparison.OrdinalIgnoreCase))
                return defaultPath;

            if (File.Exists(normalizedSaved))
                return normalizedSaved;
        }
        catch
        {
            // Invalid path stored in settings — fall back to default.
        }

        return defaultPath;
    }

    public static string ResolveAccountFilePath(string? savedPath)
    {
        var resolved = ResolveDataFilePath(savedPath, DefaultAccountFile);
        if (File.Exists(resolved))
            return resolved;

        var legacy = Path.Combine(DataDirectory, "source.txt");
        return File.Exists(legacy) ? legacy : DefaultAccountFile;
    }

    public static string NormalizeSavedDataFilePath(string currentPath, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return defaultPath;

        try
        {
            var full = Path.GetFullPath(currentPath.Trim());
            if (full.Equals(Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase))
                return defaultPath;

            return full;
        }
        catch
        {
            return defaultPath;
        }
    }

    public static void EnsureUserFiles()
    {
        var legacyAccountFile = Path.Combine(DataDirectory, "source.txt");
        if (File.Exists(legacyAccountFile) && !File.Exists(DefaultAccountFile))
            File.Copy(legacyAccountFile, DefaultAccountFile);

        _ = DatabaseDirectory;
        _ = RuntimeDirectory;
        _ = WebView2UserDataDirectory;
        TouchIfMissing(DefaultProxyFile, """
            # proxy.txt — one proxy per line. Paste your list as-is; the app detects the format.
            #
            # Proxy type (optional prefix; without it the line is treated as HTTP):
            #   http, https       HTTP proxy
            #   socks5, socks5h   SOCKS5
            #   socks4, socks4a   SOCKS4 / SOCKS4A
            #
            # Layout (separator can be : ; | , space or tab):
            #   host:port
            #   host:port:user:pass
            #   user:pass:host:port
            #   user:pass@host:port
            #   host:port@user:pass
            #   socks5://user:pass@host:port
            #
            # Examples:
            #   192.168.1.10:8080
            #   proxy.example.com:1080:myuser:mypassword
            #   myuser:mypassword@proxy.example.com:1080
            #   socks5://myuser:mypassword@proxy.example.com:1080
            #
            # Lines starting with # are ignored. Unreadable lines are skipped.
            """.ReplaceLineEndings("\r\n"));
        TouchIfMissing(DefaultAccountFile, "# " + Core.AccountFormat.Default + "\r\n");
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TouchIfMissing(string path, string defaultContent)
    {
        if (File.Exists(path))
            return;

        File.WriteAllText(path, defaultContent);
    }
}
