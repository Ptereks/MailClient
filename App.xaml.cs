using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;

namespace ZennoVibe.MailClient;

public partial class App : Application
{
    private static int _fatalErrorShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        AppLog.StartNewRun();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        base.OnStartup(e);

        // One copy per folder: a second one would find the database locked and stop with
        // an error anyway. Two copies from different folders have different data and may run.
        if (!TryClaimInstance())
        {
            System.Windows.MessageBox.Show(
                "Mail Client is already running from " + AppPaths.BaseDirectory + ".",
                "Yahoo AOL Mail Client",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        AppPaths.EnsureUserFiles();
        TryLoadJetBrainsMono();
        WebView2NativeBootstrap.EnsureLoaderSearchPath();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None);

        await WebView2Warmup.StartAsync();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static Mutex? _instanceMutex;

    private static bool TryClaimInstance()
    {
        var hash = 2166136261u;
        foreach (var ch in AppPaths.BaseDirectory)
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;

        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, "Local\\ZennoVibe.MailClient." + hash.ToString("x8"), out var createdNew);
            return createdNew;
        }
        catch (Exception ex)
        {
            // Cannot tell; let the database lock decide as before.
            AppLog.Warning("startup", "Single-instance check failed", ex);
            return true;
        }
    }

    private static void TryLoadJetBrainsMono()
    {
        var runtimeDir = AppPaths.RuntimeDirectory;
        var regular = Path.Combine(runtimeDir, "JetBrainsMono-Regular.ttf");
        var semibold = Path.Combine(runtimeDir, "JetBrainsMono-SemiBold.ttf");
        if (!File.Exists(regular) && !File.Exists(semibold))
            return;

        var baseUri = new Uri(
            runtimeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar,
            UriKind.Absolute);
        Current.Resources["JetBrainsMono"] = new FontFamily(baseUri, "./#JetBrains Mono");
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowFatalError(ex);
    }

    private static void ShowFatalError(Exception ex)
    {
        if (Interlocked.Exchange(ref _fatalErrorShown, 1) != 0)
            return;

        AppLog.Fatal("crash", "Unhandled exception terminated Mail Client.", ex);

        System.Windows.MessageBox.Show(
            $"Mail Client stopped because of an unexpected error.{Environment.NewLine}{Environment.NewLine}{UserMessage(ex)}{Environment.NewLine}{Environment.NewLine}Crash details: {AppLog.FilePath}",
            "Yahoo AOL Mail Client",
            System.Windows.MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>
    /// WPF wraps a constructor failure in XamlParseException, whose message is useless.
    /// The exception we threw is inside.
    /// </summary>
    private static string UserMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException != null &&
               current is System.Windows.Markup.XamlParseException
                   or System.Reflection.TargetInvocationException)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}
