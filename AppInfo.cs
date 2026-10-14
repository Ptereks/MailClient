using System.Reflection;

namespace ZennoVibe.MailClient;

public static class AppInfo
{
    public const string ProductName = "Yahoo AOL Mail Client";
    public const string CreatorTelegramUrl = "https://t.me/Ptereks";
    public const string CreatorTelegramHandle = "@Ptereks";

    public static string Version { get; } = ResolveVersion();

    public static string VersionLabel => "v" + Version;

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion) && TryFormatVersion(fileVersion, out var fromFile))
            return fromFile;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
            var trimmed = plusIndex >= 0 ? informational[..plusIndex] : informational;
            if (TryFormatVersion(trimmed, out var fromInfo))
                return fromInfo;
        }

        var version = assembly.GetName().Version;
        return version == null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static bool TryFormatVersion(string value, out string formatted)
    {
        formatted = "";
        if (!System.Version.TryParse(value, out var parsed))
            return false;

        formatted = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        return true;
    }
}
