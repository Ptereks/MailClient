using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ZennoVibe.MailClient;

internal partial class AccountGroupDialog : FluentWindow
{
    private readonly bool _createMode;

    internal AccountGroupDialog(
        bool createMode,
        IReadOnlyList<AccountGroupEntity> groups,
        bool selectForDeletion = false)
    {
        _createMode = createMode;
        InitializeComponent();
        WindowGlassChrome.Apply(this);

        if (createMode)
        {
            Title = "Create account group";
            DialogTitleBar.Title = Title;
            PromptTextBlock.Text = "Enter a group name. Accounts can be added now or later.";
            GroupNameTextBox.Visibility = Visibility.Visible;
            GroupComboBox.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "Create";
        }
        else
        {
            Title = selectForDeletion ? "Delete account group" : "Move selected accounts";
            DialogTitleBar.Title = Title;
            PromptTextBlock.Text = selectForDeletion
                ? "Choose the group you want to delete."
                : "Choose a destination group.";
            GroupNameTextBox.Visibility = Visibility.Collapsed;
            GroupComboBox.Visibility = Visibility.Visible;
            var choices = groups.Select(x => new GroupChoice(x.Id, x.Name));
            GroupComboBox.ItemsSource = selectForDeletion
                ? choices.ToList()
                : new[] { new GroupChoice(null, "Ungrouped") }.Concat(choices).ToList();
            GroupComboBox.SelectedIndex = 0;
            ConfirmButton.Content = selectForDeletion ? "Continue" : "Move";
        }

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public string GroupName { get; private set; } = "";

    public int? SelectedGroupId { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_createMode)
        {
            GroupNameTextBox.Focus();
            Keyboard.Focus(GroupNameTextBox);
        }
        else
        {
            GroupComboBox.Focus();
            Keyboard.Focus(GroupComboBox);
        }
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
        if (_createMode)
        {
            GroupName = GroupNameTextBox.Text.Trim();
            if (GroupName.Length == 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Enter a group name.",
                    "Account group",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                GroupNameTextBox.Focus();
                return;
            }
        }
        else if (GroupComboBox.SelectedItem is GroupChoice choice)
        {
            SelectedGroupId = choice.Id;
        }
        else
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record GroupChoice(int? Id, string Name);
}
