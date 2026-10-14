using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace ZennoVibe.MailClient;

/// <summary>
/// Starts both WebView2 processes before the main window appears and keeps them alive.
/// Mail uses the proxied environment. Documentation uses the default one.
/// </summary>
internal static class WebView2Warmup
{
    private static Window? _host;

    public static async Task StartAsync()
    {
        if (_host != null)
            return;

        var mailView = new WebView2();
        var docsView = new WebView2();
        var root = new Grid();
        root.Children.Add(mailView);
        root.Children.Add(docsView);

        _host = new Window
        {
            Width = 4,
            Height = 4,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = root
        };
        _host.Show();

        try
        {
            await Task.WhenAll(WarmMail(mailView), WarmDocumentation(docsView));
        }
        catch (Exception ex)
        {
            AppLog.Error("webview2.warmup", "Browser preload failed; views will start on first use.", ex);
        }
    }

    private static async Task WarmMail(WebView2 view)
    {
        var environment = await WebView2Host.GetEnvironmentAsync();
        await view.EnsureCoreWebView2Async(environment);
    }

    private static async Task WarmDocumentation(WebView2 view)
    {
        var environment = await WebView2Host.GetDocumentationEnvironmentAsync();
        await view.EnsureCoreWebView2Async(environment);
    }
}
