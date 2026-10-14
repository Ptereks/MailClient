using System.IO;
using System.Windows;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;
using ZennoVibe.MailClient.Services;

namespace ZennoVibe.MailClient;

internal partial class ReplyWindow : FluentWindow
{
    private readonly MailDatabase _db;
    private readonly AccountEntity _account;
    private readonly SmtpSendService _smtp;
    private readonly string? _inReplyTo;
    private readonly IReadOnlyList<string> _references;
    private readonly QuoteParts? _quote;
    private readonly Action<string>? _statusReporter;
    private CancellationTokenSource? _sendCancellation;
    private Task? _sendTask;

    /// <param name="quote">The quoted original; null for a reply without one.</param>
    /// <param name="inReplyTo">Message-Id of the original, without angle brackets.</param>
    /// <param name="references">The original's thread chain including its own id (<see cref="MessageEntity.ThreadReferences"/>).</param>
    public ReplyWindow(
        MailDatabase db,
        AccountEntity account,
        SmtpSendService smtp,
        string to,
        string subject,
        string body,
        QuoteParts? quote,
        string? inReplyTo,
        IReadOnlyList<string> references,
        Action<string>? statusReporter)
    {
        InitializeComponent();
        _db = db;
        _account = account;
        _smtp = smtp;
        _inReplyTo = inReplyTo;
        _references = references;
        _quote = quote;
        _statusReporter = statusReporter;

        FromTextBlock.Text = "From: " + account.Email;
        ToTextBox.Text = to ?? "";
        SubjectTextBox.Text = subject ?? "";
        BodyTextBox.Text = body ?? "";
        QuoteTextBox.Text = _quote?.Text ?? "";
        QuoteHost.Visibility = _quote == null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += (_, _) =>
        {
            BodyTextBox.Focus();
            BodyTextBox.CaretIndex = 0;
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _sendCancellation?.Cancel();
        base.OnClosing(e);
    }

    private void QuoteToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = QuoteTextBox.Visibility != Visibility.Visible;
        QuoteTextBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        QuoteToggleButton.Content = open ? "Original message  ▾" : "Original message";
    }

    private void CopyFrom_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_account.Email))
            return;

        try
        {
            Clipboard.SetText(_account.Email);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Copy address",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sendTask != null)
            return;

        var to = ToTextBox.Text.Trim();
        if (to.Length == 0)
        {
            StatusTextBlock.Text = "Enter at least one recipient address.";
            return;
        }

        if (!SmtpSendService.TryParseRecipients(to, out var recipients, out var recipientError))
        {
            StatusTextBlock.Text = recipientError;
            return;
        }

        List<ProxySpec> proxies;
        try
        {
            proxies = ProxyFile.Load(_db);
            if (proxies.Count == 0)
                throw new InvalidOperationException("Proxy file is empty.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = ex.Message;
            return;
        }

        var missing = Attachments.RemoveMissing();
        if (missing.Count > 0)
        {
            StatusTextBlock.Text = "Removed, no longer on disk: " + string.Join(", ", missing);
            return;
        }

        SendButton.IsEnabled = false;
        StatusTextBlock.Text = "Sending...";
        _sendCancellation = new CancellationTokenSource();
        var token = _sendCancellation.Token;

        try
        {
            var request = new OutgoingMail(
                recipients,
                SubjectTextBox.Text.Trim(),
                BodyTextBox.Text.TrimEnd(),
                _quote,
                _inReplyTo,
                _references,
                Attachments.Paths);
            _sendTask = Task.Run(
                () => _smtp.SendAsync(
                    _db,
                    _account,
                    proxies,
                    MainWindow.ProxyStartIndex(_account, proxies),
                    request,
                    token),
                token);
            await _sendTask;

            var summary = recipients.Count == 1
                ? recipients[0].Address
                : recipients.Count + " recipients";
            _statusReporter?.Invoke("Mail sent from " + _account.Email + " to " + summary + ".");
            _sendTask = null;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Sending cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Send failed.";
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Send failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _sendTask = null;
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            if (IsLoaded)
                SendButton.IsEnabled = true;
        }
    }
}
