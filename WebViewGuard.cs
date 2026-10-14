using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace ZennoVibe.MailClient;

/// <summary>
/// Shared rules for the embedded browser. WebView2 bypasses the account proxy, so no
/// message content may ever cause a network request from this process.
/// </summary>
internal static class WebViewGuard
{
    /// <summary>
    /// The document produced by <c>NavigateToString</c> lives at <c>about:blank</c>; inline
    /// images arrive as <c>data:</c> URIs. Everything else is remote.
    /// </summary>
    public static bool IsLocalUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return true;

        return uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
               uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A link the user clicked inside a message: the navigation is cancelled (the viewer never
    /// leaves the local document) and the address is handed to <paramref name="openLink"/>.
    /// Anything the page tried on its own — a stripped meta refresh, a frame, a redirect — is
    /// only cancelled.
    /// </summary>
    public static void HandleMessageNavigation(CoreWebView2NavigationStartingEventArgs e, Action<string> openLink)
    {
        if (IsLocalUri(e.Uri))
            return;

        e.Cancel = true;
        if (e.IsUserInitiated)
            openLink(e.Uri);
    }

    /// <summary>Same rule for <c>target="_blank"</c> links, which ask for a new window.</summary>
    public static void HandleMessageNewWindow(CoreWebView2NewWindowRequestedEventArgs e, Action<string> openLink)
    {
        e.Handled = true;
        if (e.IsUserInitiated)
            openLink(e.Uri);
    }

    /// <summary>Opens a link in the user's default browser instead of inside the app.</summary>
    public static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No default browser or the shell refused; the link is simply not opened.
        }
    }
}
