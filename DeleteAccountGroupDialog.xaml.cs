using System.Windows;
using Wpf.Ui.Controls;

namespace ZennoVibe.MailClient;

internal enum DeleteAccountGroupChoice
{
    Cancel,
    KeepAccounts,
    DeleteWithAccounts
}

internal partial class DeleteAccountGroupDialog : FluentWindow
{
    internal DeleteAccountGroupDialog(string groupName)
    {
        InitializeComponent();
        QuestionTextBlock.Text = "Delete group \"" + groupName + "\"?";
    }

    public DeleteAccountGroupChoice Choice { get; private set; }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteAccountGroupChoice.Cancel;
        DialogResult = false;
    }

    private void KeepAccounts_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteAccountGroupChoice.KeepAccounts;
        DialogResult = true;
    }

    private void DeleteWithAccounts_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteAccountGroupChoice.DeleteWithAccounts;
        DialogResult = true;
    }
}
