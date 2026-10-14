using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;
using ZennoVibe.MailClient.Services;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ZennoVibe.MailClient;

internal partial class MessageViewWindow : FluentWindow
{
    private readonly MailDatabase _db;
    private readonly MessageEntity _message;
    private readonly AttachmentDownloadService _attachmentDownloader = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _attachmentCancellation;
    private bool _networkSealed;
    private bool _loadMedia;
    private ProxySpec? _mediaProxy;

    private string? _code;

    public MessageViewWindow(MailDatabase db, MessageEntity message)
    {
        _db = db;
        _message = message;
        InitializeComponent();
        WindowGlassChrome.Apply(this);

        var subject = message.Subject ?? "";
        var title = string.IsNullOrWhiteSpace(subject) ? "Message" : subject;
        Title = title.Length > 80 ? title[..80] + "..." : title;
        MessageTitleBar.Title = Title;

        SubjectTextBlock.Text = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject;
        MetaTextBlock.Text =
            (message.From ?? "") + " -> " + (message.To ?? "") + " | " +
            message.DateUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";
        DownloadAttachmentsButton.Visibility = message.HasAttachments
            ? Visibility.Visible
            : Visibility.Collapsed;
        AttachmentSummaryTextBlock.Text = message.AttachmentSummary;
        AttachmentSummaryPanel.Visibility = message.AttachmentFileNames.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        MessageBody body;
        try
        {
            body = _db.LoadMessageBody(_message.Id);
        }
        catch (Exception ex)
        {
            AppLog.Error("message.view", "Could not read message body from database", _message, ex);
            ShowPlainFallback(MessageBodyDocument.ResolvePlainText(_message, MessageBody.Empty));
            NotifyFailure("Could not read this message from the database.", ex);
            return;
        }

        ShowCode(VerificationCode.Find(
            _message.Subject ?? "",
            MessageBodyDocument.ResolvePlainText(_message, body)));

        try
        {
            await ShowInWebViewAsync(body);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            AppLog.Error("message.view", "WebView2 runtime missing", _message, ex);
            ShowPlainFallback(MessageBodyDocument.ResolvePlainText(_message, body));
            NotifyFailure(
                "WebView2 Runtime is not installed. Showing plain text instead.",
                ex);
        }
        catch (Exception ex)
        {
            AppLog.Error("message.view", "WebView2 failed to render message", _message, ex);
            ShowPlainFallback(MessageBodyDocument.ResolvePlainText(_message, body));
            NotifyFailure("Could not render this message in the browser view. Showing plain text instead.", ex);
        }
    }

    private async Task ShowInWebViewAsync(MessageBody body)
    {
        var environment = await WebView2Host.GetEnvironmentAsync();
        await MessageWebView.EnsureCoreWebView2Async(environment);

        var core = MessageWebView.CoreWebView2;
        var settings = core.Settings;
        // The Chromium menu offers "Save image as" / "Open in new tab", which re-download
        // through Chromium's own network stack. Copy still works via keyboard.
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;

        // Mail is untrusted content: no scripts, no dialogs and no dev tools.
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        // SmartScreen would send URLs from the message to Microsoft.
        settings.IsReputationCheckingRequired = false;

        // WebView2 itself never talks to the network: every remote request is either blocked
        // or answered here with bytes fetched through the account proxy. Leaving a request
        // unanswered would let Chromium use the real IP.
        if (!_networkSealed)
        {
            _networkSealed = true;
            _loadMedia = TryPrepareMediaProxy();
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            // Clicked links open in the in-app browser behind the account proxy; frames only
            // get cancelled.
            core.NavigationStarting += (_, e) => WebViewGuard.HandleMessageNavigation(e, OpenLink);
            core.FrameNavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) => WebViewGuard.HandleMessageNewWindow(e, OpenLink);
            WebViewContextMenu.Attach(MessageWebView, OpenLink);
            // Downloads and "Save as" bypass the request filter entirely.
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.SaveAsUIShowing += (_, e) => e.Cancel = true;
        }

        MessageWebView.NavigateToString(MessageBodyDocument.Build(_message, body, _loadMedia));
    }

    /// <summary>
    /// Resolves the same proxy the account already uses for IMAP. Returns false when the
    /// setting is off or no proxy is available — remote media then stays blocked.
    /// </summary>
    private bool TryPrepareMediaProxy()
    {
        var settings = _db.GetOrCreateSettings();
        if (!settings.LoadMedia)
            return false;

        var account = _db.Accounts.FindById(_message.AccountId);
        if (account == null)
            return false;

        try
        {
            var proxyPath = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);
            if (!File.Exists(proxyPath))
                return false;

            var proxies = ProxySpec.LoadFromFile(proxyPath);
            if (proxies.Count == 0)
                return false;

            _mediaProxy = proxies[ProxyStartIndex(account, proxies)];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (WebViewGuard.IsLocalUri(e.Request.Uri))
            return;

        if (!_loadMedia ||
            _mediaProxy == null ||
            !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
            !ProxiedMediaFetcher.IsAllowed(e.ResourceContext, uri) ||
            !string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            e.Response = BlockedResponse();
            return;
        }

        var deferral = e.GetDeferral();
        var proxy = _mediaProxy;
        _ = ServeMediaAsync(e, uri, proxy, deferral);
    }

    private async Task ServeMediaAsync(
        CoreWebView2WebResourceRequestedEventArgs e,
        Uri uri,
        ProxySpec proxy,
        CoreWebView2Deferral deferral)
    {
        FetchedMedia? fetched = null;
        try
        {
            fetched = await ProxiedMediaFetcher.GetAsync(uri, proxy, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            fetched = null;
        }
        catch
        {
            fetched = null;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (fetched == null)
                {
                    e.Response = BlockedResponse();
                    return;
                }

                var reason = fetched.StatusCode is >= 200 and < 300 ? "OK" : "Error";
                e.Response = MessageWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    fetched.Body,
                    fetched.StatusCode,
                    reason,
                    "Content-Type: " + fetched.ContentType + "\r\nCache-Control: no-store");
            });
        }
        catch
        {
            // Window already closing.
        }
        finally
        {
            try
            {
                deferral.Complete();
            }
            catch
            {
                // Deferral already completed or the view is gone.
            }
        }
    }

    private CoreWebView2WebResourceResponse BlockedResponse() =>
        MessageWebView.CoreWebView2.Environment.CreateWebResourceResponse(
            null,
            403,
            "Blocked",
            "");

    private void OpenLink(string uri)
    {
        var status = MessageLinks.Open(this, _db, _message.AccountId, uri, out var opened);
        if (opened)
            return;

        System.Windows.MessageBox.Show(this, status, "Open link", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!WebViewGuard.IsLocalUri(e.Uri))
            e.Cancel = true;
    }

    private void ShowPlainFallback(string text)
    {
        MessageWebView.Visibility = Visibility.Collapsed;
        PlainTextFallback.Visibility = Visibility.Visible;
        PlainTextFallbackBlock.Text = text;
    }

    private void NotifyFailure(string summary, Exception ex)
    {
        System.Windows.MessageBox.Show(
            this,
            summary +
            Environment.NewLine + Environment.NewLine +
            ex.Message,
            "Message viewer",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    private void ShowCode(string? code)
    {
        _code = code;
        if (code == null)
            return;

        CopyCodeButton.Content = "Copy " + code;
        CopyCodeButton.Visibility = Visibility.Visible;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_code != null)
            Copy(_code);
    }

    private void CopyFrom_Click(object sender, RoutedEventArgs e) =>
        Copy(MailAddressText.ExtractFirst(_message.From ?? ""));

    private void CopyTo_Click(object sender, RoutedEventArgs e) =>
        Copy(MailAddressText.ExtractFirst(_message.To ?? ""));

    private async void DownloadAttachments_Click(object sender, RoutedEventArgs e)
    {
        var account = _db.Accounts.FindById(_message.AccountId);
        if (account == null)
            return;
        var destination = AttachmentDownloadService.GetAccountDirectory(account);

        _attachmentCancellation?.Cancel();
        _attachmentCancellation?.Dispose();
        _attachmentCancellation = new CancellationTokenSource();
        DownloadAttachmentsButton.IsEnabled = false;

        try
        {
            var settings = _db.GetOrCreateSettings();
            var proxyPath = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);
            var proxies = ProxySpec.LoadFromFile(proxyPath);
            var result = await _attachmentDownloader.DownloadAsync(
                _db,
                account,
                _message,
                proxies,
                ProxyStartIndex(account, proxies),
                destination,
                _attachmentCancellation.Token);

            System.Windows.MessageBox.Show(
                this,
                result.Count + " attachment(s) saved to:" + Environment.NewLine + destination,
                "Attachments downloaded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // Window closed while downloading.
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Attachment download failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            DownloadAttachmentsButton.IsEnabled = true;
            _attachmentCancellation?.Dispose();
            _attachmentCancellation = null;
        }
    }

    private static int ProxyStartIndex(AccountEntity account, IReadOnlyList<ProxySpec> proxies)
    {
        if (proxies.Count <= 1)
            return 0;

        if (!string.IsNullOrWhiteSpace(account.ActiveProxy))
        {
            for (var i = 0; i < proxies.Count; i++)
            {
                if (string.Equals(proxies[i].Display, account.ActiveProxy, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        var hash = 2166136261u;
        foreach (var ch in account.Email ?? "")
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;

        return (int)(hash % (uint)proxies.Count);
    }

    private void Copy(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        try
        {
            System.Windows.Clipboard.SetText(value);
        }
        catch (Exception ex)
        {
            AppLog.Warning("clipboard", "Copy failed", ex);
            System.Windows.MessageBox.Show(
                this,
                "Could not copy to the clipboard: " + ex.Message,
                "Copy",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _attachmentCancellation?.Cancel();
        _attachmentCancellation?.Dispose();
        _lifetimeCancellation.Dispose();

        // Release the msedgewebview2 processes now instead of waiting for the GC.
        try
        {
            MessageWebView.Dispose();
        }
        catch
        {
            // Never initialised (plain-text fallback) or already gone.
        }

        base.OnClosed(e);
    }
}
