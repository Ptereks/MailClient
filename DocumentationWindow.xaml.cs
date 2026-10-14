using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace ZennoVibe.MailClient;

internal partial class DocumentationWindow : FluentWindow
{
    public DocumentationWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var environment = await WebView2Host.GetDocumentationEnvironmentAsync();
            await DocumentationWebView.EnsureCoreWebView2Async(environment);

            var core = DocumentationWebView.CoreWebView2;
            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsScriptEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsReputationCheckingRequired = false;

            // Same seal as the message viewer: every remote request is refused here,
            // downloads are cancelled. The README is local text and needs nothing remote.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!WebViewGuard.IsLocalUri(args.Request.Uri))
                    args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.SaveAsUIShowing += (_, args) => args.Cancel = true;

            // README links open in the default browser; the embedded view never leaves
            // the local document, so it cannot make requests from this process.
            core.NavigationStarting += (_, args) =>
            {
                if (WebViewGuard.IsLocalUri(args.Uri))
                    return;

                args.Cancel = true;
                WebViewGuard.OpenExternally(args.Uri);
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                WebViewGuard.OpenExternally(args.Uri);
            };

            DocumentationWebView.NavigateToString(MarkdownDocument.LoadReadmeHtml());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            System.Windows.MessageBox.Show(
                this,
                "WebView2 Runtime is not installed. Install Microsoft Edge WebView2 Runtime to view documentation.",
                "Documentation",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Documentation",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            DocumentationWebView.Dispose();
        }
        catch
        {
            // Never initialised or already gone.
        }

        base.OnClosed(e);
    }
}
