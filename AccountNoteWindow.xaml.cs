using System.Windows;
using Wpf.Ui.Controls;

namespace ZennoVibe.MailClient;

internal partial class AccountNoteWindow : FluentWindow
{
    private readonly string _email;
    private bool _closing;

    public AccountNoteWindow(string email, string note, IReadOnlyList<string> proxies, string? selectedProxy)
    {
        InitializeComponent();
        _email = email ?? "";
        EmailTextBlock.Text = _email;
        NoteTextBox.Text = note ?? "";

        foreach (var proxy in proxies)
            ProxyComboBox.Items.Add(proxy);

        if (!string.IsNullOrWhiteSpace(selectedProxy))
        {
            if (!ProxyComboBox.Items.Contains(selectedProxy))
                ProxyComboBox.Items.Insert(0, selectedProxy);

            ProxyComboBox.SelectedItem = selectedProxy;
        }
        else if (ProxyComboBox.Items.Count > 0)
        {
            ProxyComboBox.SelectedIndex = 0;
        }

        UpdateNotePlaceholder();
        UpdateProxyPlaceholder();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
    }

    private void NoteTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateNotePlaceholder();

    private void NoteTextBox_FocusChanged(object sender, RoutedEventArgs e) =>
        UpdateNotePlaceholder();

    private void UpdateNotePlaceholder()
    {
        if (_closing)
            return;

        NotePlaceholder.Visibility = NoteTextBox.Text.Length == 0 && !NoteTextBox.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NewProxyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateProxyPlaceholder();

    private void NewProxyTextBox_FocusChanged(object sender, RoutedEventArgs e) =>
        UpdateProxyPlaceholder();

    private void UpdateProxyPlaceholder()
    {
        if (_closing)
            return;

        ProxyPlaceholder.Visibility = NewProxyTextBox.Text.Length == 0 && !NewProxyTextBox.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public string Note => NoteTextBox.Text.Trim();

    public string? SelectedProxy => ProxyComboBox.SelectedItem as string;

    public string NewProxy => NewProxyTextBox.Text.Trim();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_email.Length == 0)
            return;

        try
        {
            Clipboard.SetText(_email);
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DialogResult != null)
            return;

        DialogResult = true;
    }
}
