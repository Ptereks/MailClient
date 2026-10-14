using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ZennoVibe.MailClient;

public partial class AboutWindow : FluentWindow
{
    public AboutWindow()
    {
        InitializeComponent();

        ProductNameTextBlock.Text = AppInfo.ProductName;
        VersionTextBlock.Text = AppInfo.VersionLabel;
        TelegramHandleTextBlock.Text = AppInfo.CreatorTelegramHandle;
        TelegramUrlTextBlock.Text = AppInfo.CreatorTelegramUrl;

        SourceInitialized += (_, _) => Activate();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    private void OpenTelegram(object sender, MouseButtonEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppInfo.CreatorTelegramUrl) { UseShellExecute = true });
    }

    protected override void OnClosed(EventArgs e)
    {
        Owner?.Activate();
        Owner?.Focus();
        base.OnClosed(e);
    }
}
