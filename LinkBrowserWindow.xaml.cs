using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;

namespace ZennoVibe.MailClient;

/// <summary>
/// Opens a link from a message inside the program. The browser profile is bound to the
/// account proxy at the Chromium level (<see cref="WebView2Host.GetLinkEnvironmentAsync"/>),
/// so the page and everything it loads go out through that proxy or not at all.
/// </summary>
internal partial class LinkBrowserWindow : FluentWindow
{
    private readonly ProxySpec _proxy;
    private readonly string _accountEmail;
    private readonly string _startUri;

    public LinkBrowserWindow(ProxySpec proxy, string accountEmail, string uri)
    {
        NetworkGuard.AssertProxy(proxy);
        _proxy = proxy;
        _accountEmail = accountEmail;
        _startUri = uri;

        InitializeComponent();
        WindowGlassChrome.Apply(this);

        AddressTextBox.Text = uri;
        ProxyTextBlock.Text = "via " + proxy.Summary + "  ·  " + accountEmail;
        StatusTextBlock.Text = "Starting…";

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var environment = await WebView2Host.GetLinkEnvironmentAsync(_proxy);
            await LinkWebView.EnsureCoreWebView2Async(environment);

            var core = LinkWebView.CoreWebView2;
            var settings = core.Settings;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            // SmartScreen would send every URL to Microsoft.
            settings.IsReputationCheckingRequired = false;

            // Only the proxy may be answered with the proxy credentials; a site asking for
            // Basic auth is simply refused so the credentials never reach it.
            core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;

            core.NavigationStarting += (_, args) =>
            {
                if (!IsWebUri(args.Uri))
                {
                    args.Cancel = true;
                    return;
                }

                AddressTextBox.Text = args.Uri;
                StatusTextBlock.Text = "Loading…";
            };
            core.NavigationCompleted += (_, args) =>
            {
                BackButton.IsEnabled = core.CanGoBack;
                AddressTextBox.Text = core.Source;
                StatusTextBlock.Text = args.IsSuccess
                    ? "Done"
                    : "Failed: " + args.WebErrorStatus;
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                var title = core.DocumentTitle;
                Title = string.IsNullOrWhiteSpace(title) ? "Link" : title;
                LinkTitleBar.Title = Title.Length > 80 ? Title[..80] + "…" : Title;
            };
            // Pop-ups stay in this window and behind this proxy.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsWebUri(args.Uri))
                    core.Navigate(args.Uri);
            };
            // Downloads and "Save as" bypass the page and are refused here as everywhere else.
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.SaveAsUIShowing += (_, args) => args.Cancel = true;

            WebViewContextMenu.Attach(LinkWebView, uri => core.Navigate(uri));

            core.Navigate(_startUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("WebView2 Runtime is not installed. Install Microsoft Edge WebView2 Runtime to open links.");
        }
        catch (Exception ex)
        {
            AppLog.Error("link.view", "Could not open link browser", ex);
            ShowError("Could not start the browser for this link: " + ex.Message);
        }
    }

    private void OnBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        var isProxyChallenge =
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var challenged) &&
            string.Equals(challenged.Host, _proxy.Host, StringComparison.OrdinalIgnoreCase) &&
            challenged.Port == _proxy.Port;

        if (isProxyChallenge && !string.IsNullOrEmpty(_proxy.Username))
        {
            e.Response.UserName = _proxy.Username;
            e.Response.Password = _proxy.Password ?? "";
            return;
        }

        e.Cancel = true;
    }

    private static bool IsWebUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    private void ShowError(string text)
    {
        LinkWebView.Visibility = Visibility.Collapsed;
        ErrorTextBlock.Text = text;
        ErrorTextBlock.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "Failed";
        ReloadButton.IsEnabled = false;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var core = LinkWebView.CoreWebView2;
        if (core is { CanGoBack: true })
            core.GoBack();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => LinkWebView.CoreWebView2?.Reload();

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = LinkWebView.CoreWebView2?.Source ?? AddressTextBox.Text;
        if (WebViewContextMenu.TryCopy(address))
            StatusTextBlock.Text = "Address copied";
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            LinkWebView.Dispose();
        }
        catch
        {
            // Never initialised or already gone.
        }

        base.OnClosed(e);
    }
}
