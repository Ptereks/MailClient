using System.IO;
using System.Text;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient;

/// <summary>
/// Crash-only log under <c>data/mailclient.log</c>. Routine account, proxy, import and
/// message errors stay in the UI and never reach this file.
/// </summary>
internal static class AppLog
{
    /// <summary>The log must stay small enough to attach to a bug report.</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(AppPaths.DataDirectory, "mailclient.log");

    private static bool _headerWritten;

    public static string FilePath => LogPath;

    /// <summary>
    /// Starts a clean current-run log while preserving the previous crash for diagnostics.
    /// </summary>
    public static void StartNewRun()
    {
        lock (Gate)
        {
            try
            {
                var previous = LogPath + ".previous-crash";
                if (File.Exists(LogPath))
                {
                    File.Delete(previous);
                    File.Move(LogPath, previous);
                }

                _headerWritten = false;
            }
            catch
            {
                // A locked file simply keeps its previous content.
            }
        }
    }

    // Operational failures are already shown beside the affected account/action. Keeping
    // these methods as no-ops avoids touching every proven network call site.
    public static void Warning(string category, string message, Exception? ex = null)
    {
    }

    public static void Error(string category, string message, Exception? ex = null)
    {
    }

    public static void Error(string category, string message, MessageEntity messageMeta, Exception? ex = null)
    {
    }

    public static void Notice(string category, string message) =>
        Write("INFO", category, message, null);

    public static void Fatal(string category, string message, Exception ex) =>
        Write("FATAL", category, message, ex);

    private static void Write(string level, string category, string message, Exception? ex)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                RotateIfOversized();

                var sb = new StringBuilder();

                // Written next to the first problem instead of on every start, so a clean
                // run leaves no file at all but a reported one still names its build.
                if (!_headerWritten)
                {
                    _headerWritten = true;
                    var version = typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown";
                    sb.AppendLine("=== MailClient " + version + " — " + DateTime.UtcNow.ToString("u") + " ===");
                }

                sb.Append('[').Append(DateTime.UtcNow.ToString("u")).Append("] ");
                sb.Append('[').Append(level).Append("] ");
                sb.Append('[').Append(category).Append("] ");
                sb.AppendLine(NetworkGuard.Redact(message));

                if (ex != null)
                {
                    sb.AppendLine(NetworkGuard.Redact(ex.GetType().FullName + ": " + ex.Message));
                    sb.AppendLine(NetworkGuard.Redact(ex.StackTrace ?? ""));

                    if (ex.InnerException != null)
                    {
                        sb.AppendLine("Inner: " + ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message);
                        sb.AppendLine(ex.InnerException.StackTrace);
                    }
                }

                sb.AppendLine(new string('-', 72));
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Never crash the app because logging failed.
            }
        }
    }

    /// <summary>Keeps one previous file as <c>mailclient.log.old</c>.</summary>
    private static void RotateIfOversized()
    {
        var current = new FileInfo(LogPath);
        if (!current.Exists || current.Length < MaxLogBytes)
            return;

        var previous = LogPath + ".old";
        if (File.Exists(previous))
            File.Delete(previous);

        File.Move(LogPath, previous);
        _headerWritten = false;
    }
}
