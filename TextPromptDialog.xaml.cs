using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ZennoVibe.MailClient;

internal partial class TextPromptDialog : FluentWindow
{
    internal TextPromptDialog(string title, string prompt, string? initialValue = null)
    {
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        PromptTextBlock.Text = prompt;
        ValueTextBox.Text = initialValue ?? "";
        WindowGlassChrome.Apply(this);
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public string Value { get; private set; } = "";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ValueTextBox.Focus();
        Keyboard.Focus(ValueTextBox);
        ValueTextBox.SelectAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        Value = ValueTextBox.Text.Trim();
        if (Value.Length == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Enter a value.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ValueTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
