using System.Windows;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;

namespace ZennoVibe.MailClient;

/// <summary>
/// Shown at startup when the copy is not activated. Displays the machine id and takes an
/// activation code; the window closes with <c>DialogResult = true</c> only after a code that
/// verifies for this machine (see <see cref="Activation"/>).
/// </summary>
public partial class ActivationWindow : FluentWindow
{
    public ActivationWindow()
    {
        InitializeComponent();
        MachineIdBox.Text = Activation.MachineIdDisplay();
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void CopyMachineId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(MachineIdBox.Text);
            ShowInfo("Machine ID copied.");
        }
        catch
        {
            ShowError("Could not copy to the clipboard. Select the text and copy it by hand.");
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            ShowError("Paste the activation code first.");
            return;
        }

        if (Activation.Activate(code) == ActivationStatus.Ok)
        {
            DialogResult = true;
            Close();
            return;
        }

        ShowError("This code does not fit this computer. Check that you sent the exact Machine ID and pasted the whole code.");
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string text)
    {
        ErrorTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x5C, 0x5C));
        ErrorTextBlock.Text = text;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void ShowInfo(string text)
    {
        ErrorTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xFF, 0x8A, 0xD8, 0x8A));
        ErrorTextBlock.Text = text;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
