using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ZennoVibe.MailClient.Services;

namespace ZennoVibe.MailClient;

/// <summary>
/// "Attach" button plus the chips of chosen files. Keeps the total under the 25 MB Yahoo and
/// AOL accept per message and says so in the file dialog, where the user is choosing.
/// </summary>
internal partial class AttachmentPicker : UserControl
{
    private const string LimitNote = "25 MB per message in total";

    private readonly ObservableCollection<AttachmentChip> _chips = [];

    public AttachmentPicker()
    {
        InitializeComponent();
        ChipsHost.ItemsSource = _chips;
    }

    /// <summary>Full paths of the attached files, in the order they were added.</summary>
    public IReadOnlyList<string> Paths => _chips.Select(chip => chip.Path).ToList();

    public long TotalBytes => _chips.Sum(chip => chip.Bytes);

    public event EventHandler? Changed;

    public void Clear()
    {
        _chips.Clear();
        RefreshSummary();
    }

    /// <summary>Drops files that vanished since they were attached; returns their names.</summary>
    public IReadOnlyList<string> RemoveMissing()
    {
        var missing = _chips.Where(chip => !File.Exists(chip.Path)).ToList();
        foreach (var chip in missing)
            _chips.Remove(chip);
        if (missing.Count > 0)
            RefreshSummary();
        return missing.Select(chip => chip.Name).ToList();
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach files — " + LimitNote,
            Multiselect = true,
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*"
        };

        var owner = Window.GetWindow(this);
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true)
            return;

        AddFiles(dialog.FileNames);
    }

    public void AddFiles(IEnumerable<string> files)
    {
        var rejected = new List<string>();
        foreach (var path in files)
        {
            if (_chips.Any(chip => string.Equals(chip.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            long bytes;
            try
            {
                bytes = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                rejected.Add(System.IO.Path.GetFileName(path) + " — " + ex.Message);
                continue;
            }

            if (TotalBytes + bytes > OutgoingMessageBuilder.MaxAttachmentBytes)
            {
                rejected.Add(System.IO.Path.GetFileName(path) + " (" + OutgoingMessageBuilder.FormatSize(bytes) + ")");
                continue;
            }

            _chips.Add(new AttachmentChip(path, bytes));
        }

        RefreshSummary();

        if (rejected.Count > 0)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                "Not attached — the message would exceed " + LimitNote + ":\n\n" +
                string.Join("\n", rejected) +
                "\n\nAttached now: " + OutgoingMessageBuilder.FormatSize(TotalBytes) + " of 25 MB.",
                "Attachment limit",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        var chip = _chips.FirstOrDefault(c => c.Path == path);
        if (chip != null)
            _chips.Remove(chip);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (_chips.Count == 0)
        {
            SummaryTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            SummaryTextBlock.Text =
                _chips.Count + (_chips.Count == 1 ? " file, " : " files, ") +
                OutgoingMessageBuilder.FormatSize(TotalBytes) + " of 25 MB";
            SummaryTextBlock.Visibility = Visibility.Visible;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record AttachmentChip(string Path, long Bytes)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Size => OutgoingMessageBuilder.FormatSize(Bytes);
    }
}
