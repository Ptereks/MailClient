using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Data;

namespace ZennoVibe.MailClient;

internal partial class AddressBookWindow : FluentWindow
{
    private readonly MailDatabase _db;
    private readonly DispatcherTimer _filterDebounce;
    private ContactEntity? _selected;

    public AddressBookWindow(MailDatabase db)
    {
        _db = db;
        InitializeComponent();
        ApplySavedColumnWidths();
        ContactsListView.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(ContactsColumn_DragCompleted),
            handledEventsToo: true);

        _filterDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            Reload();
        };

        Reload();
        SourceInitialized += (_, _) => Activate();
    }

    private void ApplySavedColumnWidths()
    {
        var settings = _db.GetOrCreateSettings();
        EmailColumn.Width = SavedWidth(settings.AddressEmailColumnWidth, 300);
        NoteColumn.Width = SavedWidth(settings.AddressNoteColumnWidth, 420);
        SeenCountColumn.Width = SavedWidth(settings.AddressMessagesColumnWidth, 90);
        LastSeenColumn.Width = SavedWidth(settings.AddressLastSeenColumnWidth, 110);
    }

    private static double SavedWidth(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;

    private void ContactsColumn_DragCompleted(object sender, DragCompletedEventArgs e) =>
        SaveColumnWidths();

    private void SaveColumnWidths()
    {
        var settings = _db.GetOrCreateSettings();
        settings.AddressEmailColumnWidth = EmailColumn.Width;
        settings.AddressNoteColumnWidth = NoteColumn.Width;
        settings.AddressMessagesColumnWidth = SeenCountColumn.Width;
        settings.AddressLastSeenColumnWidth = LastSeenColumn.Width;
        _db.SaveSettings(settings);
    }

    /// <summary>Called by the main window after a sync so an open book is not stale.</summary>
    internal void RefreshFromDatabase() => Reload();

    private void Reload()
    {
        _filterDebounce.Stop();

        var selectedId = _selected?.Id;
        var query = FilterTextBox.Text.Trim();

        IEnumerable<ContactEntity> contacts = _db.Contacts.Query()
            .OrderByDescending(x => x.LastSeenUtc)
            .ToEnumerable();

        if (query.Length > 0)
        {
            contacts = contacts.Where(x =>
                x.Email.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Note.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = contacts.ToList();
        ContactsListView.ItemsSource = list;
        ContactsListView.SelectedItem = selectedId.HasValue
            ? list.FirstOrDefault(x => x.Id == selectedId.Value)
            : null;

        CountTextBlock.Text = list.Count == 1 ? "1 address" : list.Count + " addresses";
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholderTextBlock.Visibility = FilterTextBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void ContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ContactsListView.SelectedItem as ContactEntity;

        NoteTextBox.Text = _selected?.Note ?? "";
        NoteTextBox.IsEnabled = _selected != null;
        SaveNoteButton.IsEnabled = _selected != null;
        SelectedEmailTextBlock.Text = _selected?.Email ?? "Select an address to write a note.";
    }

    private void ContactsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CopySelectedEmail();

    private void NoteTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        SaveNote();
        e.Handled = true;
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e) => SaveNote();

    private void SaveNote()
    {
        if (_selected == null)
            return;

        var note = NoteTextBox.Text.Trim();
        _selected.Note = note;
        _db.SaveContactNote(_selected.Id, note);

        // Refresh in place so the Note column shows the new text without losing the row.
        ContactsListView.Items.Refresh();
        SelectedEmailTextBlock.Text = _selected.Email + " — note saved";
    }

    private void CopyEmail_Click(object sender, RoutedEventArgs e) => CopySelectedEmail();

    private void CopyRowEmail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ContactEntity contact })
            return;

        CopyEmail(contact);
    }

    private void NoteCell_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is System.Windows.Controls.TextBox box)
            SaveCellNote(box);

        e.Handled = true;
    }

    private void NoteCell_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
            SaveCellNote(box);
    }

    private void SaveCellNote(System.Windows.Controls.TextBox box)
    {
        if (box.DataContext is not ContactEntity contact)
            return;

        var note = box.Text.Trim();
        if (note == contact.Note)
            return;

        contact.Note = note;
        _db.SaveContactNote(contact.Id, note);

        if (ReferenceEquals(_selected, contact))
        {
            NoteTextBox.Text = note;
            SelectedEmailTextBlock.Text = contact.Email + " — note saved";
        }
    }

    private void CopySelectedEmail()
    {
        if (_selected == null)
            return;

        CopyEmail(_selected);
    }

    private void CopyEmail(ContactEntity contact)
    {
        try
        {
            System.Windows.Clipboard.SetText(contact.Email);
            SelectedEmailTextBlock.Text = contact.Email + " — copied";
        }
        catch (Exception ex)
        {
            SelectedEmailTextBlock.Text = "Could not copy: " + ex.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _filterDebounce.Stop();
        SaveColumnWidths();
        base.OnClosed(e);
    }
}
