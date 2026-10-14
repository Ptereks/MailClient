using Wpf.Ui.Controls;

namespace ZennoVibe.MailClient;

internal partial class MessageSourceWindow : FluentWindow
{
    public MessageSourceWindow(string subject, string source)
    {
        InitializeComponent();
        var title = string.IsNullOrWhiteSpace(subject) ? "Message source" : subject;
        Title = title.Length > 80 ? title[..80] + "..." : title;
        SourceTitleBar.Title = Title;
        SourceTextBox.Text = source;
        SourceInitialized += (_, _) => Activate();
    }
}
