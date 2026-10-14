using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ZennoVibe.MailClient;

/// <summary>
/// Replaces the Chromium context menu with the program's own. The Chromium menu offers
/// "Open in new window", "Save image as", "Inspect" and similar entries that either leave the
/// sealed document or re-download through Chromium's own network stack; this one only copies
/// text and hands the link back to the caller.
/// </summary>
internal static class WebViewContextMenu
{
    /// <param name="openLink">Called with the link address when the user picks "Open link".</param>
    public static void Attach(WebView2 view, Action<string>? openLink)
    {
        var core = view.CoreWebView2;
        if (core == null)
            return;

        // The event is only raised while default menus are enabled; Handled = true below
        // keeps the Chromium menu from ever appearing.
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.ContextMenuRequested += (_, e) =>
        {
            e.Handled = true;

            var menu = Build(e.ContextMenuTarget, openLink);
            if (menu == null)
                return;

            menu.PlacementTarget = view;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
    }

    private static ContextMenu? Build(CoreWebView2ContextMenuTarget target, Action<string>? openLink)
    {
        var menu = new ContextMenu();
        var link = target.HasLinkUri ? target.LinkUri : null;
        var selection = target.HasSelection ? target.SelectionText : null;
        var image = target.HasSourceUri && target.Kind == CoreWebView2ContextMenuTargetKind.Image
            ? target.SourceUri
            : null;

        if (!string.IsNullOrEmpty(link))
        {
            if (openLink != null)
            {
                var open = new MenuItem { Header = "Open link" };
                open.Click += (_, _) => openLink(link);
                menu.Items.Add(open);
            }

            var copyLink = new MenuItem { Header = "Copy link" };
            copyLink.Click += (_, _) => TryCopy(link);
            menu.Items.Add(copyLink);
        }

        if (!string.IsNullOrEmpty(selection))
        {
            var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
            copy.Click += (_, _) => TryCopy(selection);
            menu.Items.Add(copy);
        }

        if (!string.IsNullOrEmpty(image) && !image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var copyImage = new MenuItem { Header = "Copy image address" };
            copyImage.Click += (_, _) => TryCopy(image);
            menu.Items.Add(copyImage);
        }

        return menu.Items.Count == 0 ? null : menu;
    }

    public static bool TryCopy(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        try
        {
            System.Windows.Clipboard.SetText(value);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("clipboard", "Copy failed", ex);
            return false;
        }
    }
}
