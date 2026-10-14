using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;
using ZennoVibe.MailClient.Services;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ZennoVibe.MailClient;

public partial class MainWindow : FluentWindow
{
    private const int MessageListLimit = 5000;
    private const int PreviewBodyLimit = 40_000;
    private const double DefaultMessagesPaneWeight = 3;
    private const double DefaultPreviewPaneWeight = 1;
    private const double DefaultComposePaneWeight = 2;
    private const int NeighbourPrefetchCount = 5;
    private readonly MailDatabase _db = new(MailDatabase.DefaultPath);
    private readonly SyncOrchestrator _syncOrchestrator = new();
    private readonly AccountExportService _accountExportService = new();
    private readonly SmtpSendService _smtpSendService = new();
    private readonly MessageBodyLoader _bodyLoader = new();
    private readonly AttachmentDownloadService _attachmentDownloader = new();
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _accountFilterDebounce;
    private CancellationTokenSource? _syncCancellation;
    private CancellationTokenSource? _bodyFetchCancellation;
    private CancellationTokenSource? _attachmentDownloadCancellation;
    private CancellationTokenSource? _sendCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task? _activeSyncTask;
    private Task<MessageBody>? _bodyFetch;
    private Task<AttachmentDownloadResult>? _attachmentDownload;
    private Task? _prefetchTask;
    private Task? _sendTask;
    private int _prefetchGeneration;
    private bool _closeAfterSend;
    private MessageEntity? _replyTarget;
    private AccountEntity? _selectedAccount;
    private FolderEntity? _selectedFolder;
    private AddressBookWindow? _addressBook;
    private MessageEntity? _previewMessage;
    private string? _previewCode;
    private bool _previewWebSealed;
    private bool _previewAwaitingNavigation;
    private string _messageSortColumn = "Date";
    private bool _messageSortDescending = true;
    private bool _syncRunning;
    private readonly Dictionary<int, CancellationTokenSource> _accountSyncCancellation = new();
    private readonly Dictionary<int, Task> _accountSyncTasks = new();
    private int _singleSyncCount;
    private bool _composeVisible;
    private bool _suppressAccountSelection;
    private bool _adjustingMessageColumns;
    private int _syncRunSuccess;
    private int _syncRunTotal;

    public MainWindow()
    {
        InitializeComponent();
        MessagesListView.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(MessagesColumnHeader_DragCompleted),
            handledEventsToo: true);
        MessagesListView.AddHandler(
            Thumb.DragDeltaEvent,
            new DragDeltaEventHandler(MessagesColumnHeader_DragDelta),
            handledEventsToo: true);

        _searchDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ReloadMessages();
        };

        _accountFilterDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _accountFilterDebounce.Tick += (_, _) =>
        {
            _accountFilterDebounce.Stop();
            ReloadAccounts();
        };

        WindowGlassChrome.Apply(this);
        ReloadAccounts();
        Title = AppInfo.ProductName + " " + AppInfo.VersionLabel;
        VersionTextBlock.Text = AppInfo.VersionLabel;
        StatusTextBlock.Text = "Ready. Use sync icons or open Settings.";
        Loaded += OnMainLoaded;
    }

    private void OnMainLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnMainLoaded;
        ApplySavedPaneLayout();
    }

    private void ApplySavedPaneLayout()
    {
        var settings = _db.GetOrCreateSettings();
        AccountsPaneColumn.Width = StarLength(settings.AccountsColumnWeight, 25);
        MailPaneColumn.Width = StarLength(settings.MailColumnWeight, 58);
        MessagesPaneRow.Height = StarLength(settings.MessagesPaneWeight, DefaultMessagesPaneWeight);
        PreviewPaneRow.Height = StarLength(settings.PreviewPaneWeight, DefaultPreviewPaneWeight);
        FromColumn.Width = SavedColumnWidth(
            settings.MessageFromColumnWidth is 180 or 280 ? 300 : settings.MessageFromColumnWidth,
            300);
        SubjectColumn.Width = SavedColumnWidth(
            settings.MessageSubjectColumnWidth == 320 ? 300 : settings.MessageSubjectColumnWidth,
            300);
        DateColumn.Width = SavedColumnWidth(
            settings.MessageDateColumnWidth is 140 or 120 ? 144 : settings.MessageDateColumnWidth,
            144);
        AttachmentColumn.Width = SavedColumnWidth(settings.MessageAttachmentColumnWidth, 38);
        ApplyComposeVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FitMessageColumns()));
    }

    /// <summary>
    /// Compose is driven purely by the Compose checkbox in Settings; when it is off the
    /// pane and its splitter take up no space at all.
    /// </summary>
    private void ApplyComposeVisibility()
    {
        var settings = _db.GetOrCreateSettings();
        _composeVisible = settings.ComposeVisible;

        if (_composeVisible)
        {
            ComposePaneRow.Height = StarLength(settings.ComposePaneWeight, DefaultComposePaneWeight);
            ComposePaneRow.MinHeight = 120;
            PreviewComposeSplitter.Visibility = Visibility.Visible;
            ComposePanel.Visibility = Visibility.Visible;
        }
        else
        {
            ComposePaneRow.Height = new GridLength(0);
            ComposePaneRow.MinHeight = 0;
            PreviewComposeSplitter.Visibility = Visibility.Collapsed;
            ComposePanel.Visibility = Visibility.Collapsed;
        }

        UpdateComposePanel();
    }

    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settings = _db.GetOrCreateSettings();
        settings.MessagesPaneWeight = StarValue(MessagesPaneRow, DefaultMessagesPaneWeight);
        settings.PreviewPaneWeight = StarValue(PreviewPaneRow, DefaultPreviewPaneWeight);
        if (_composeVisible)
            settings.ComposePaneWeight = StarValue(ComposePaneRow, DefaultComposePaneWeight);
        _db.SaveSettings(settings);
    }

    private void MainColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        => SaveMainColumnWidths();

    private void SaveMainColumnWidths()
    {
        var settings = _db.GetOrCreateSettings();
        settings.AccountsColumnWeight = StarValue(AccountsPaneColumn, 25);
        settings.MailColumnWeight = StarValue(MailPaneColumn, 58);
        _db.SaveSettings(settings);
    }

    private static GridLength StarLength(double value, double fallback) =>
        new(value > 0 ? value : fallback, GridUnitType.Star);

    private static double StarValue(RowDefinition row, double fallback) =>
        row.Height.IsStar && row.Height.Value > 0 ? row.Height.Value : fallback;

    private static double StarValue(ColumnDefinition column, double fallback) =>
        column.Width.IsStar && column.Width.Value > 0 ? column.Width.Value : fallback;

    private static double SavedColumnWidth(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;

    private void ReloadAccounts()
    {
        _accountFilterDebounce.Stop();

        var selectedId = _selectedAccount?.Id;
        var selectedIds = AccountsListBox.SelectedItems
            .OfType<AccountEntity>()
            .Where(x => !x.IsGroupPlaceholder)
            .Select(x => x.Id)
            .ToHashSet();
        if (selectedId.HasValue)
            selectedIds.Add(selectedId.Value);

        var query = AccountFilterTextBox.Text.Trim();

        // One database pass feeds both the list and the status-bar totals. The totals always
        // count the whole database, so a filter never makes it look as if accounts vanished.
        var allAccounts = _db.Accounts.Query()
            .OrderBy(x => x.Email)
            .ToList();
        UpdateAccountCounters(allAccounts);

        var groups = _db.AccountGroups.FindAll()
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var list = query.Length > 0
            ? allAccounts.Where(x => x.Email.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList()
            : allAccounts;
        var knownGroupIds = groups.Select(x => x.Id).ToHashSet();
        var counts = list
            .Where(x => x.GroupId.HasValue && knownGroupIds.Contains(x.GroupId.Value))
            .GroupBy(x => x.GroupId!.Value)
            .ToDictionary(x => x.Key, x => x.Count());
        var groupKeys = groups.ToDictionary(
            x => x.Id,
            x => new AccountGroupKey(
                x.Id,
                x.Name,
                x.IsExpanded,
                counts.GetValueOrDefault(x.Id)));
        var ungroupedCount = list.Count(x =>
            !x.GroupId.HasValue || !knownGroupIds.Contains(x.GroupId.Value));
        var ungrouped = new AccountGroupKey(null, "Ungrouped", true, ungroupedCount);

        foreach (var account in list)
        {
            account.GroupKey = account.GroupId.HasValue &&
                               groupKeys.TryGetValue(account.GroupId.Value, out var key)
                ? key
                : ungrouped;
        }

        // CollectionView does not create a group with zero items. One collapsed, non-
        // selectable row keeps an empty named section visible until accounts are moved in.
        if (query.Length == 0)
        {
            foreach (var group in groups.Where(x => counts.GetValueOrDefault(x.Id) == 0))
            {
                list.Add(new AccountEntity
                {
                    Id = -group.Id,
                    GroupId = group.Id,
                    GroupKey = groupKeys[group.Id],
                    IsGroupPlaceholder = true
                });
            }
        }

        list = list
            .OrderBy(x => x.GroupKey.Id.HasValue ? 0 : 1)
            .ThenBy(x => x.GroupKey.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var restored = selectedId.HasValue
            ? list.FirstOrDefault(x => x.Id == selectedId.Value)
            : null;

        // Swapping ItemsSource raises SelectionChanged twice (clear, then restore), which
        // would rebuild the folder and message panes for a selection that never changed.
        _suppressAccountSelection = true;
        try
        {
            var view = new ListCollectionView(list);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AccountEntity.GroupKey)));
            AccountsListBox.ItemsSource = view;
            AccountsListBox.SelectedItem = restored;

            foreach (var account in list.Where(x => selectedIds.Contains(x.Id) && x.Id != restored?.Id))
                AccountsListBox.SelectedItems.Add(account);
        }
        finally
        {
            _suppressAccountSelection = false;
        }

        if (restored != null)
        {
            _selectedAccount = restored;
            return;
        }

        // The previously selected account is gone (deleted, or filtered out), so its mail
        // must not stay on screen.
        _selectedAccount = null;
        _selectedFolder = null;
        ReloadFolders();
        ReloadMessages();
        ClearPreview();
        UpdateComposePanel();
    }

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? (int)elapsed.TotalMinutes + "m " + elapsed.Seconds + "s"
            : Math.Round(elapsed.TotalSeconds, 1) + "s";

    /// <summary>
    /// P6: where the time of a full run went, averaged over the accounts that finished in it.
    /// A large "token" share points at the OAuth endpoint, "connect" at the proxy, "folders"
    /// at the mailbox walk and "store" at the local database.
    /// </summary>
    private string AveragePhaseSummary(DateTime runStartedUtc)
    {
        long token = 0, connect = 0, folders = 0, store = 0;
        var count = 0;

        foreach (var account in _db.Accounts.Find(x => x.LastSyncAt != null && x.LastSyncAt >= runStartedUtc))
        {
            count++;
            token += account.LastTokenMs;
            connect += account.LastConnectMs;
            folders += account.LastFoldersMs;
            store += account.LastStoreMs;
        }

        if (count == 0)
            return "";

        return " Avg per account: token " + AccountEntity.Seconds((int)(token / count)) +
               " В· connect " + AccountEntity.Seconds((int)(connect / count)) +
               " В· folders " + AccountEntity.Seconds((int)(folders / count)) +
               " В· store " + AccountEntity.Seconds((int)(store / count)) + ".";
    }

    /// <summary>
    /// Status bar totals. These count the whole database, not the filtered list, so the
    /// account filter never makes it look as though accounts disappeared.
    /// </summary>
    private void UpdateAccountCounters() => UpdateAccountCounters(_db.Accounts.FindAll());

    private void UpdateAccountCounters(IEnumerable<AccountEntity> accounts)
    {
        var total = 0;
        var synced = 0;

        // One pass halves database lock pressure while many worker threads are updating
        // account states.
        foreach (var account in accounts)
        {
            total++;
            if (account.Status == AccountSyncStatus.Ready)
                synced++;
        }

        AccountsTotalTextBlock.Text = "Accounts: " + total;
        SyncCountTextBlock.Text = synced + " / " + total + " synced";
    }

    /// <summary>
    /// Spreads accounts across the proxy list deterministically, so the same account keeps
    /// the same starting proxy between runs and parallel syncs do not all start on proxy 0.
    /// </summary>
    internal static int ProxyStartIndex(AccountEntity account, IReadOnlyList<ProxySpec> proxies)
    {
        if (proxies.Count <= 1)
            return 0;

        if (!string.IsNullOrWhiteSpace(account.ActiveProxy))
        {
            for (var i = 0; i < proxies.Count; i++)
            {
                if (string.Equals(proxies[i].Display, account.ActiveProxy, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        var hash = 2166136261u;
        foreach (var ch in account.Email)
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;

        return (int)(hash % (uint)proxies.Count);
    }

    private void ReloadFolders()
    {
        if (_selectedAccount == null)
        {
            _selectedFolder = null;
            FoldersListBox.ItemsSource = null;
            return;
        }

        var selectedId = _selectedFolder?.Id;

        var list = _db.Folders
            .Find(x => x.AccountId == _selectedAccount.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x.DisplayName))
            .OrderBy(x => x.FullName)
            .ToList();
        FoldersListBox.ItemsSource = list;

        if (selectedId.HasValue)
        {
            var restored = list.FirstOrDefault(x => x.Id == selectedId.Value);
            if (restored != null)
            {
                FoldersListBox.SelectedItem = restored;
                _selectedFolder = restored;
                return;
            }
        }

        _selectedFolder = null;
        FoldersListBox.SelectedIndex = -1;
    }

    private void ReloadMessages()
    {
        _searchDebounce.Stop();

        var search = SearchTextBox.Text;
        if (search.Trim().Length > 0)
        {
            var found = MessageSearchService.SearchAll(_db, search, MessageListLimit);
            MessagesListView.ItemsSource = SortMessages(found);
            Dispatcher.BeginInvoke(new Action(() => FitMessageColumns()));
            MessagesScopeTextBlock.Text = found.Count + " in downloaded";
            return;
        }

        if (_selectedAccount == null)
        {
            MessagesListView.ItemsSource = null;
            MessagesScopeTextBlock.Text = "";
            return;
        }

        var folderId = _selectedFolder?.Id;
        var messages = MessageSearchService.Search(
            _db,
            _selectedAccount.Id,
            folderId,
            "",
            MessageListLimit);

        MessagesListView.ItemsSource = SortMessages(messages);
        Dispatcher.BeginInvoke(new Action(() => FitMessageColumns()));

        var scope = _selectedFolder == null ? "all folders" : _selectedFolder.DisplayName;
        MessagesScopeTextBlock.Text = messages.Count + " in " + scope;
    }

    private List<MessageEntity> SortMessages(List<MessageEntity> messages)
    {
        IEnumerable<MessageEntity> sorted = _messageSortColumn switch
        {
            "From" => _messageSortDescending
                ? messages.OrderByDescending(x => x.From, StringComparer.OrdinalIgnoreCase)
                : messages.OrderBy(x => x.From, StringComparer.OrdinalIgnoreCase),
            "Subject" => _messageSortDescending
                ? messages.OrderByDescending(x => x.Subject, StringComparer.OrdinalIgnoreCase)
                : messages.OrderBy(x => x.Subject, StringComparer.OrdinalIgnoreCase),
            "Attach" => _messageSortDescending
                ? messages.OrderByDescending(x => x.HasAttachments).ThenByDescending(x => x.DateUtc)
                : messages.OrderBy(x => x.HasAttachments).ThenByDescending(x => x.DateUtc),
            _ => _messageSortDescending
                ? messages.OrderByDescending(x => x.DateUtc)
                : messages.OrderBy(x => x.DateUtc)
        };

        return sorted.ToList();
    }

    private void MessagesColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header)
            return;

        var column = MessageColumnKey(header.Column);
        if (column is not ("From" or "Subject" or "Date" or "Attach"))
            return;

        // Same column toggles direction; a new column starts descending.
        if (column == _messageSortColumn)
            _messageSortDescending = !_messageSortDescending;
        else
            _messageSortDescending = true;

        _messageSortColumn = column;

        if (MessagesListView.ItemsSource is List<MessageEntity> current)
            MessagesListView.ItemsSource = SortMessages(current);
    }

    private void MessagesListView_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => FitMessageColumns()));

    private void MessagesColumnHeader_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var header = e.OriginalSource is DependencyObject source
            ? FindAncestor<GridViewColumnHeader>(source)
            : null;

        FitMessageColumns(MessageColumnKey(header?.Column));
        SaveMessageColumnWidths();
    }

    private void SaveMessageColumnWidths()
    {
        var settings = _db.GetOrCreateSettings();
        settings.MessageFromColumnWidth = FromColumn.Width;
        settings.MessageSubjectColumnWidth = SubjectColumn.Width;
        settings.MessageDateColumnWidth = DateColumn.Width;
        settings.MessageAttachmentColumnWidth = AttachmentColumn.Width;
        _db.SaveSettings(settings);
    }

    private void MessagesColumnHeader_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var header = e.OriginalSource is DependencyObject source
            ? FindAncestor<GridViewColumnHeader>(source)
            : null;
        var title = MessageColumnKey(header?.Column);

        if (title is "From" or "Subject" or "Date" or "Attach")
            FitMessageColumns(title);
    }

    private string MessageColumnKey(System.Windows.Controls.GridViewColumn? column) =>
        column == FromColumn ? "From" :
        column == SubjectColumn ? "Subject" :
        column == DateColumn ? "Date" :
        column == AttachmentColumn ? "Attach" :
        "";

    /// <summary>
    /// Keeps the table flush with the message pane. From and Subject share the remaining
    /// width equally; Date stays a compact timestamp column. WPF assigns the boundary
    /// between Subject and Date to Subject's right-hand gripper, so dragging that boundary
    /// keeps From fixed and changes Date in the opposite direction.
    /// </summary>
    private void FitMessageColumns(string? changedColumn = null)
    {
        if (_adjustingMessageColumns || MessagesListView.ActualWidth <= 0)
            return;

        var minFrom = HeaderMinimumWidth("From");
        var minSubject = HeaderMinimumWidth("Subject");
        var dateWidth = Math.Ceiling(HeaderMinimumWidth("2026-09-16 00:00") * 1.2);
        var minAttachment = 28;

        _adjustingMessageColumns = true;
        try
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(MessagesListView);
            var viewportWidth = scrollViewer is { ViewportWidth: > 0 }
                ? scrollViewer.ViewportWidth
                : MessagesListView.ActualWidth;
            var available = Math.Max(
                minFrom + minSubject + dateWidth + minAttachment,
                viewportWidth - 2);

            FromColumn.Width = Math.Max(minFrom, FromColumn.Width);
            DateColumn.Width = Math.Max(dateWidth, DateColumn.Width);
            AttachmentColumn.Width = Math.Max(minAttachment, AttachmentColumn.Width);

            switch (changedColumn)
            {
                case "From":
                    FromColumn.Width = Math.Clamp(
                        FromColumn.Width,
                        minFrom,
                        Math.Max(minFrom, available - minSubject - DateColumn.Width - AttachmentColumn.Width));
                    SubjectColumn.Width = available - FromColumn.Width - DateColumn.Width - AttachmentColumn.Width;
                    break;

                case "Date":
                    DateColumn.Width = Math.Clamp(
                        DateColumn.Width,
                        dateWidth,
                        Math.Max(dateWidth, available - minSubject - FromColumn.Width - AttachmentColumn.Width));
                    SubjectColumn.Width = available - FromColumn.Width - DateColumn.Width - AttachmentColumn.Width;
                    break;

                case "Subject":
                    SubjectColumn.Width = Math.Clamp(
                        SubjectColumn.Width,
                        minSubject,
                        Math.Max(minSubject, available - FromColumn.Width - dateWidth - AttachmentColumn.Width));
                    DateColumn.Width = available - FromColumn.Width - SubjectColumn.Width - AttachmentColumn.Width;
                    break;

                case "Attach":
                    AttachmentColumn.Width = Math.Clamp(
                        AttachmentColumn.Width,
                        minAttachment,
                        Math.Max(minAttachment, available - minFrom - minSubject - DateColumn.Width));
                    EqualizeFromAndSubject(available, minFrom, minSubject, dateWidth);
                    break;

                default:
                    AttachmentColumn.Width = Math.Min(
                        AttachmentColumn.Width,
                        available - minFrom - minSubject - dateWidth);
                    EqualizeFromAndSubject(available, minFrom, minSubject, dateWidth);
                    break;
            }
        }
        finally
        {
            _adjustingMessageColumns = false;
        }
    }

    private void EqualizeFromAndSubject(double available, double minFrom, double minSubject, double dateWidth)
    {
        DateColumn.Width = dateWidth;
        var rest = available - DateColumn.Width - AttachmentColumn.Width;
        var fromWidth = Math.Max(minFrom, Math.Floor(rest / 2));
        var subjectWidth = rest - fromWidth;
        if (subjectWidth < minSubject)
        {
            subjectWidth = minSubject;
            fromWidth = rest - subjectWidth;
        }

        FromColumn.Width = fromWidth;
        SubjectColumn.Width = subjectWidth;
    }

    private double HeaderMinimumWidth(string text)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(MessagesListView);
        var formatted = new System.Windows.Media.FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface("Segoe UI"),
            11,
            System.Windows.Media.Brushes.Transparent,
            dpi.PixelsPerDip);

        // Header padding plus the invisible eight-pixel drag target.
        return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 24;
    }

    private void ClearFolderSelection()
    {
        _selectedFolder = null;
        FoldersListBox.SelectedIndex = -1;
    }

    private void OpenAddressBook_Click(object sender, RoutedEventArgs e)
    {
        if (_addressBook is { IsLoaded: true })
        {
            _addressBook.Activate();
            return;
        }

        _addressBook = new AddressBookWindow(_db) { Owner = this };
        _addressBook.Closed += (_, _) => _addressBook = null;
        _addressBook.Show();
    }

    private List<int> SelectedAccountIds() =>
        AccountsListBox.SelectedItems
            .OfType<AccountEntity>()
            .Where(x => !x.IsGroupPlaceholder)
            .Select(x => x.Id)
            .Distinct()
            .ToList();

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var accountIds = SelectedAccountIds();

        try
        {
            var groups = _db.AccountGroups.FindAll().ToList();
            var dialog = new AccountGroupDialog(createMode: true, groups) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            var group = _db.CreateAccountGroup(dialog.GroupName);
            if (accountIds.Count > 0)
                _db.MoveAccountsToGroup(accountIds, group.Id);

            ReloadAccounts();
            StatusTextBlock.Text = accountIds.Count > 0
                ? "Created group " + group.Name + " with " + accountIds.Count + " account(s)."
                : "Created empty group " + group.Name + ".";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Create group",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void MoveSelectedAccounts_Click(object sender, RoutedEventArgs e)
    {
        var accountIds = SelectedAccountIds();
        if (accountIds.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Select one or more accounts first. Use Ctrl, Shift or Ctrl+A for multiple accounts.",
                "Move accounts",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var groups = _db.AccountGroups
            .Query()
            .OrderBy(x => x.Name)
            .ToList();
        var dialog = new AccountGroupDialog(createMode: false, groups) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _db.MoveAccountsToGroup(accountIds, dialog.SelectedGroupId);
        ReloadAccounts();
        StatusTextBlock.Text = "Moved " + accountIds.Count + " account(s).";
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_syncRunning)
            return;

        var groups = _db.AccountGroups
            .Query()
            .OrderBy(x => x.Name)
            .ToList();

        if (groups.Count == 0)
        {
            StatusTextBlock.Text = "There are no account groups to delete.";
            return;
        }

        var selectDialog = new AccountGroupDialog(
            createMode: false,
            groups,
            selectForDeletion: true)
        {
            Owner = this
        };

        if (selectDialog.ShowDialog() != true || !selectDialog.SelectedGroupId.HasValue)
            return;

        var group = groups.FirstOrDefault(x => x.Id == selectDialog.SelectedGroupId.Value);
        if (group != null)
            ConfirmDeleteGroup(new AccountGroupKey(group.Id, group.Name, group.IsExpanded, 0));
    }

    private void DeleteAccountGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_syncRunning)
        {
            System.Windows.MessageBox.Show(
                this,
                "Wait until sync finishes before deleting groups.",
                "Delete account group",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (sender is not FrameworkElement { Tag: AccountGroupKey { Id: int groupId } group })
            return;

        ConfirmDeleteGroup(group);
        e.Handled = true;
    }

    private void ConfirmDeleteGroup(AccountGroupKey group)
    {
        if (!group.Id.HasValue)
            return;

        var dialog = new DeleteAccountGroupDialog(group.Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Choice == DeleteAccountGroupChoice.Cancel)
            return;

        var deleteAccounts = dialog.Choice == DeleteAccountGroupChoice.DeleteWithAccounts;
        _db.DeleteAccountGroup(group.Id.Value, deleteAccounts);
        ReloadAccounts();
        ReloadFolders();
        ReloadMessages();
        ClearPreview();
        UpdateAccountCounters();
        StatusTextBlock.Text = deleteAccounts
            ? "Deleted group " + group.Name + " and its accounts."
            : "Deleted group " + group.Name + "; accounts moved to Ungrouped.";
    }

    private void AccountGroupToggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle)
            return;

        var content = FindGroupContent(toggle);
        if (content == null)
            return;

        var expanded = toggle.IsChecked == true;
        content.Height = expanded ? double.NaN : 0;
        content.Opacity = expanded ? 1 : 0;
    }

    private void AccountGroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle)
            return;

        var isExpanded = toggle.IsChecked == true;
        if (toggle.Tag is AccountGroupKey { Id: int groupId })
            _db.SetAccountGroupExpanded(groupId, isExpanded);

        var content = FindGroupContent(toggle);
        if (content == null)
            return;

        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);

        var easing = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = isExpanded
                ? System.Windows.Media.Animation.EasingMode.EaseOut
                : System.Windows.Media.Animation.EasingMode.EaseIn
        };

        if (isExpanded)
        {
            content.Height = double.NaN;
            content.Measure(new Size(
                Math.Max(1, content.ActualWidth),
                double.PositiveInfinity));
            var targetHeight = Math.Max(0, content.DesiredSize.Height);
            content.Height = 0;
            content.Opacity = 0;

            var height = new System.Windows.Media.Animation.DoubleAnimation(
                0,
                targetHeight,
                TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = easing
            };
            height.Completed += (_, _) =>
            {
                if (toggle.IsChecked == true)
                {
                    content.BeginAnimation(FrameworkElement.HeightProperty, null);
                    content.Height = double.NaN;
                }
            };

            content.BeginAnimation(FrameworkElement.HeightProperty, height);
            content.BeginAnimation(
                UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    0,
                    1,
                    TimeSpan.FromMilliseconds(120)));
        }
        else
        {
            var currentHeight = Math.Max(0, content.ActualHeight);
            content.Height = currentHeight;

            var height = new System.Windows.Media.Animation.DoubleAnimation(
                currentHeight,
                0,
                TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = easing
            };
            height.Completed += (_, _) =>
            {
                if (toggle.IsChecked != true)
                {
                    content.BeginAnimation(FrameworkElement.HeightProperty, null);
                    content.Height = 0;
                    content.Opacity = 0;
                }
            };

            content.BeginAnimation(FrameworkElement.HeightProperty, height);
            content.BeginAnimation(
                UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    content.Opacity,
                    0,
                    TimeSpan.FromMilliseconds(100)));
        }
    }

    private static Border? FindGroupContent(ToggleButton toggle)
    {
        var groupItem = FindAncestor<GroupItem>(toggle);
        return groupItem?.Template.FindName("GroupContent", groupItem) as Border;
    }

    private void AccountsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<ListBoxItem>(source) != null ||
            FindAncestor<ButtonBase>(source) != null ||
            FindAncestor<ScrollBar>(source) != null)
        {
            return;
        }

        AccountsListBox.UnselectAll();
    }

    private void AccountsListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var item = FindAncestor<ListBoxItem>(source);
        if (item == null || item.IsSelected)
            return;

        AccountsListBox.UnselectAll();
        item.IsSelected = true;
    }

    private void NewGroupFromContextMenu_Click(object sender, RoutedEventArgs e) =>
        NewGroup_Click(sender, e);

    private void AccountsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var selected = GetSelectedAccounts();
        var priorityItem = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(x => Equals(x.Tag, "PrioritySync"));
        if (priorityItem != null)
        {
            priorityItem.IsEnabled = selected.Count > 0;
            var on = selected.Count > 0 && selected.All(x => x.SyncPriority > 0);
            priorityItem.Header = on ? "Priority sync ✓" : "Priority sync";
        }

        var root = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(x => Equals(x.Tag, "MoveGroupRoot"));
        if (root == null)
            return;

        root.Items.Clear();
        root.IsEnabled = selected.Count > 0;
        if (!root.IsEnabled)
            return;

        AddMoveGroupMenuItem(root, new AccountGroupKey(null, "Ungrouped", true, 0));

        foreach (var group in _db.AccountGroups.Query().OrderBy(x => x.Name).ToList())
        {
            AddMoveGroupMenuItem(
                root,
                new AccountGroupKey(group.Id, group.Name, group.IsExpanded, 0));
        }
    }

    private void AddMoveGroupMenuItem(MenuItem root, AccountGroupKey destination)
    {
        var item = new MenuItem
        {
            Header = destination.Name,
            Tag = destination,
            Style = (Style)FindResource("DarkMenuItem")
        };
        item.Click += MoveSelectedToGroupMenu_Click;
        root.Items.Add(item);
    }

    private void PrioritySyncMenu_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedAccounts();
        if (selected.Count == 0)
            return;

        var enable = selected.Any(x => x.SyncPriority <= 0);
        foreach (var account in selected)
        {
            account.SyncPriority = enable ? 1 : 0;
            _db.Accounts.Update(account);
        }

        ReloadAccounts();
        StatusTextBlock.Text = enable
            ? "Priority sync on for " + selected.Count + " account(s)."
            : "Priority sync off for " + selected.Count + " account(s).";
    }

    private void MoveSelectedToGroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AccountGroupKey destination })
            return;

        var accountIds = SelectedAccountIds();
        if (accountIds.Count == 0)
            return;

        _db.MoveAccountsToGroup(accountIds, destination.Id);
        ReloadAccounts();
        StatusTextBlock.Text =
            "Moved " + accountIds.Count + " account(s) to " + destination.Name + ".";
    }

    private void CopyToClipboard(string value, string status)
    {
        if (string.IsNullOrEmpty(value))
            return;

        try
        {
            System.Windows.Clipboard.SetText(value);
            StatusTextBlock.Text = status;
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; that is not worth a dialog.
            StatusTextBlock.Text = "Copy failed: " + ex.Message;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SettingsWindow(
                _db,
                status => Dispatcher.BeginInvoke(() => StatusTextBlock.Text = status),
                syncRunning => Dispatcher.BeginInvoke(() => SetSyncProgressActive(syncRunning)),
                () => Dispatcher.BeginInvoke(() =>
                {
                    ReloadAccounts();
                    ReloadFolders();
                    ReloadMessages();
                    _addressBook?.RefreshFromDatabase();
                }))
            {
                Owner = this
            };

            dialog.ShowDialog();
            ApplyComposeVisibility();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Settings",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void AccountsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelection)
            return;

        _selectedAccount = e.AddedItems
            .OfType<AccountEntity>()
            .LastOrDefault(x => !x.IsGroupPlaceholder)
            ?? AccountsListBox.SelectedItems
                .OfType<AccountEntity>()
                .FirstOrDefault(x => !x.IsGroupPlaceholder);
        ClearFolderSelection();
        ReloadFolders();
        ReloadMessages();
        ClearPreview();
        UpdateComposePanel();

        if (_selectedAccount != null)
            _ = EnsureAccessTokenCurrentAsync(_selectedAccount);
    }

    /// <summary>
    /// Any use of an OAuth account refreshes the access token and syncs when the token
    /// is older than one hour. Returns false when the refresh did not succeed.
    /// </summary>
    private async Task<bool> EnsureAccessTokenCurrentAsync(AccountEntity account)
    {
        if (!AccessTokenOlderThanOneHour(account))
            return true;

        if (_tokenRefreshTasks.TryGetValue(account.Id, out var inFlight) && !inFlight.IsCompleted)
            return await inFlight;

        var refresh = RefreshAccessTokenAsync(account);
        _tokenRefreshTasks[account.Id] = refresh;
        try
        {
            return await refresh;
        }
        finally
        {
            _tokenRefreshTasks.Remove(account.Id);
        }
    }

    private readonly Dictionary<int, Task<bool>> _tokenRefreshTasks = new();
    private readonly TokenService _tokenService = new();

    /// <summary>
    /// Fetches a new access token through the account proxy. Only the token request is
    /// awaited; the mailbox sync that follows runs in the background.
    /// </summary>
    private async Task<bool> RefreshAccessTokenAsync(AccountEntity account)
    {
        ShowPreviewNotice("Access token is being refreshed for " + account.Email + "…");
        StatusTextBlock.Text = "Refreshing access token for " + account.Email + "...";

        try
        {
            var proxies = LoadProxies();
            if (proxies.Count == 0)
                throw new InvalidOperationException("Proxy file is empty.");

            var start = ProxyStartIndex(account, proxies);
            Exception? last = null;
            for (var attempt = 0; attempt < proxies.Count; attempt++)
            {
                var proxy = proxies[(start + attempt) % proxies.Count];
                try
                {
                    var token = await Task.Run(() =>
                        _tokenService.EnsureAccessTokenAsync(account, proxy, _lifetimeCancellation.Token, forceRefresh: true));
                    TokenService.ApplyToAccount(account, token.AccessToken, token.RefreshToken, token.ExpiresAt);
                    _db.Accounts.Update(account);
                    last = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (TokenEndpointException ex) when (ex.IsTerminal)
                {
                    last = ex;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            if (last != null)
                throw last;

            StatusTextBlock.Text = "Access token refreshed for " + account.Email + ".";
            HidePreviewNotice();
            _ = SyncAccountAsync(account);
            return true;
        }
        catch (Exception ex)
        {
            account.LastError = ex.Message;
            _db.Accounts.Update(account);
            ReloadAccounts();
            StatusTextBlock.Text = "Access token refresh failed for " + account.Email + ": " + ex.Message;
            HidePreviewNotice();
            return false;
        }
    }

    private void ShowPreviewNotice(string text)
    {
        PreviewNoticeText.Text = text;
        PreviewNoticeText.Visibility = Visibility.Visible;
        PreviewLoadPercentText.Visibility = Visibility.Collapsed;
        PreviewLoadArc.StrokeDashArray = null;
        PreviewLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HidePreviewNotice()
    {
        PreviewNoticeText.Visibility = Visibility.Collapsed;
        PreviewLoadPercentText.Visibility = Visibility.Visible;
        if (_bodyFetch == null || _bodyFetch.IsCompleted)
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// OAuth access tokens last about an hour. An account file token has no known age and
    /// is treated as expired. App-password accounts have nothing to refresh.
    /// </summary>
    private static bool AccessTokenOlderThanOneHour(AccountEntity account)
    {
        if (AuthRouter.IsAppPassword(account) || string.IsNullOrWhiteSpace(account.RefreshToken))
            return false;

        if (!account.AccessTokenRefreshedAt.HasValue)
            return true;

        return DateTime.UtcNow - account.AccessTokenRefreshedAt.Value >= TimeSpan.FromHours(1);
    }

    private void AccountsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.A || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        AccountsListBox.SelectAll();
        e.Handled = true;
    }

    private void FoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFolder = FoldersListBox.SelectedItem as FolderEntity;
        ReloadMessages();
        ClearPreview();
        if (_selectedAccount != null)
            _ = EnsureAccessTokenCurrentAsync(_selectedAccount);
    }

    private async void AccountSyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AccountEntity account })
            return;

        await SyncAccountAsync(account);
    }

    private async void SyncAllAccounts_Click(object sender, RoutedEventArgs e) =>
        await SyncAllAccountsAsync();

    private async Task SyncAllAccountsAsync()
    {
        if (_syncRunning)
            return;

        _syncCancellation?.Dispose();
        _syncCancellation = new CancellationTokenSource();
        SetSyncProgressActive(true);
        StatusTextBlock.Text = "Syncing all accounts...";
        _syncRunSuccess = 0;
        _syncRunTotal = _db.Accounts.Count();
        SyncCountTextBlock.Text = "0 / " + _syncRunTotal + " synced";

        try
        {
            var settings = _db.GetOrCreateSettings();
            var proxyPath = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);

            if (!File.Exists(proxyPath))
                throw new FileNotFoundException("Proxy file not found.", proxyPath);

            var proxies = ProxySpec.LoadFromFile(proxyPath);
            if (proxies.Count == 0)
                throw new InvalidOperationException("Proxy file is empty.");

            // Progress<T> is created on the UI thread and already posts callbacks through
            // its synchronization context. Do not add a nested synchronous Dispatcher call.
            var progress = new Progress<string>(HandleSyncAllProgress);

            var started = DateTime.UtcNow;
            var syncTask = Task.Run(
                () => _syncOrchestrator.SyncAllAsync(
                    _db,
                    proxies,
                    settings.Threads,
                    progress,
                    _syncCancellation.Token),
                _syncCancellation.Token);

            _activeSyncTask = syncTask;
            var result = await syncTask;

            StatusTextBlock.Text =
                "Sync finished in " + FormatDuration(DateTime.UtcNow - started) +
                ". OK: " + result.Success +
                ", failed: " + result.Failed +
                ", threads: " + result.EffectiveThreads + "." +
                AveragePhaseSummary(started);

            ClearFolderSelection();
            ReloadAccounts();
            ReloadFolders();
            ReloadMessages();
            _addressBook?.RefreshFromDatabase();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Sync cancelled.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Sync failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            StatusTextBlock.Text = "Sync failed.";
        }
        finally
        {
            _activeSyncTask = null;
            SetSyncProgressActive(false);
            _syncCancellation?.Dispose();
            _syncCancellation = null;
        }
    }

    private void HandleSyncAllProgress(string line)
    {
        StatusTextBlock.Text = line;
        if (TryHandlePriorityProgress(line))
            return;

        if (!line.StartsWith("OK ", StringComparison.Ordinal))
            return;

        _syncRunSuccess++;
        SyncCountTextBlock.Text = _syncRunSuccess + " / " + _syncRunTotal + " synced";
    }

    private void HandleSingleAccountSyncProgress(string line)
    {
        StatusTextBlock.Text = line;
        TryHandlePriorityProgress(line);
    }

    /// <summary>
    /// Inbox/Bulk/Spam are already in the database after a PRIORITY line. Reload so the
    /// operator can open those folders while the rest of the mailbox is still syncing.
    /// </summary>
    private bool TryHandlePriorityProgress(string line)
    {
        if (!line.StartsWith("PRIORITY ", StringComparison.Ordinal))
            return false;

        var email = line.Substring("PRIORITY ".Length).Trim();
        ReloadAccounts();
        if (_syncRunning && _syncRunTotal > 0)
            SyncCountTextBlock.Text = _syncRunSuccess + " / " + _syncRunTotal + " synced";

        if (_selectedAccount != null &&
            email.Length > 0 &&
            string.Equals(_selectedAccount.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            ReloadFolders();
            ReloadMessages();
        }

        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private bool IsAccountSyncing(int accountId) =>
        _accountSyncTasks.TryGetValue(accountId, out var task) && !task.IsCompleted;

    private void NoteSingleSync(bool starting)
    {
        _singleSyncCount = starting
            ? _singleSyncCount + 1
            : Math.Max(0, _singleSyncCount - 1);

        if (_syncRunning)
            return;

        var active = _singleSyncCount > 0;
        ProgressBar.IsIndeterminate = active;
        ProgressBarHost.Opacity = active ? 1 : 0.45;
        ProgressBar.Foreground = active
            ? (System.Windows.Media.Brush)FindResource("MatrixOk")
            : (System.Windows.Media.Brush)FindResource("CarbonGreen");
    }

    private async Task SyncAccountAsync(AccountEntity account)
    {
        if (IsAccountSyncing(account.Id))
            return;

        var cancellation = new CancellationTokenSource();
        _accountSyncCancellation[account.Id] = cancellation;
        NoteSingleSync(true);
        StatusTextBlock.Text = "Sync " + account.Email + "...";
        var started = DateTime.UtcNow;

        try
        {
            var settings = _db.GetOrCreateSettings();
            var proxyPath = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);

            if (!File.Exists(proxyPath))
                throw new FileNotFoundException("Proxy file not found.", proxyPath);

            var proxies = ProxySpec.LoadFromFile(proxyPath);
            if (proxies.Count == 0)
                throw new InvalidOperationException("Proxy file is empty.");

            var progress = new Progress<string>(HandleSingleAccountSyncProgress);

            var syncTask = Task.Run(
                () => _syncOrchestrator.SyncOneAsync(
                    _db,
                    account,
                    proxies,
                    ProxyStartIndex(account, proxies),
                    progress,
                    cancellation.Token),
                cancellation.Token);

            _accountSyncTasks[account.Id] = syncTask;
            var ok = await syncTask;
            var duration = FormatDuration(DateTime.UtcNow - started);
            var attempts = account.LastSyncAttempts > 1
                ? " after " + account.LastSyncAttempts + " attempts"
                : "";
            var phases = account.PhaseSummary;
            if (phases.Length > 0)
                attempts += " (" + phases + ")";

            StatusTextBlock.Text = ok
                ? "Sync finished in " + duration + attempts + ": " + account.Email
                : "Sync failed in " + duration + attempts + ": " + account.Email;

            ReloadAccounts();
            if (_selectedAccount?.Id == account.Id)
            {
                ClearFolderSelection();
                ReloadFolders();
                ReloadMessages();
            }

            _addressBook?.RefreshFromDatabase();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text =
                "Sync cancelled after " + FormatDuration(DateTime.UtcNow - started) + ".";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Sync failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            StatusTextBlock.Text =
                "Sync failed after " + FormatDuration(DateTime.UtcNow - started) + ".";
        }
        finally
        {
            _accountSyncTasks.Remove(account.Id);
            _accountSyncCancellation.Remove(account.Id);
            cancellation.Dispose();
            NoteSingleSync(false);
        }
    }

    /// <summary>
    /// Single owner of the "a sync is in flight" state. The settings window reports its own
    /// runs through here, otherwise the main window would still allow a second concurrent
    /// sync against the same database.
    /// </summary>
    private void SetSyncProgressActive(bool active)
    {
        _syncRunning = active;
        ProgressBar.IsIndeterminate = active;
        ProgressBarHost.Opacity = active ? 1 : 0.45;
        ProgressBar.Foreground = active
            ? (System.Windows.Media.Brush)FindResource("MatrixOk")
            : (System.Windows.Media.Brush)FindResource("CarbonGreen");

        SyncAllAccountsButton.IsEnabled = !active;
        AddAccountButton.IsEnabled = !active;
        DeleteAccountButton.IsEnabled = !active;
        SaveAccountsButton.IsEnabled = !active;
        SettingsButton.IsEnabled = !active;
        NewGroupButton.IsEnabled = !active;
        MoveAccountsButton.IsEnabled = !active;
        DeleteGroupButton.IsEnabled = !active;
        UpdateComposePanel();
    }

    private void UpdateComposePanel()
    {
        var canCompose = _composeVisible && _selectedAccount != null && !IsAccountSyncing(_selectedAccount.Id);
        ComposePanel.IsEnabled = canCompose;

        if (_selectedAccount == null)
        {
            ComposeFromTextBlock.Text = "Select an account to send mail.";
            return;
        }

        ComposeFromTextBlock.Text = "From: " + _selectedAccount.Email;
    }

    private async void SendMail_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAccount == null || IsAccountSyncing(_selectedAccount.Id) || _sendTask != null)
            return;

        if (!await EnsureAccessTokenCurrentAsync(_selectedAccount))
            return;

        var to = ComposeToTextBox.Text.Trim();
        var subject = ComposeSubjectTextBox.Text.Trim();
        var body = ComposeBodyTextBox.Text;

        if (to.Length == 0)
        {
            ComposeStatusTextBlock.Text = "Enter at least one recipient address.";
            return;
        }

        if (!SmtpSendService.TryParseRecipients(to, out var recipients, out var recipientError))
        {
            ComposeStatusTextBlock.Text = recipientError;
            return;
        }

        var missingFiles = ComposeAttachments.RemoveMissing();
        if (missingFiles.Count > 0)
        {
            ComposeStatusTextBlock.Text = "Removed, no longer on disk: " + string.Join(", ", missingFiles);
            return;
        }

        var account = _selectedAccount;
        var reply = _replyTarget;
        var attachments = ComposeAttachments.Paths;
        SendMailButton.IsEnabled = false;
        ComposeStatusTextBlock.Text = "Sending...";

        _sendCancellation = new CancellationTokenSource();
        var token = _sendCancellation.Token;

        try
        {
            var proxies = LoadProxies();
            if (proxies.Count == 0)
                throw new InvalidOperationException("Proxy file is empty.");

            var request = new OutgoingMail(
                recipients,
                subject,
                body,
                null,
                reply?.MessageId,
                reply?.ThreadReferences ?? Array.Empty<string>(),
                attachments);

            _sendTask = Task.Run(
                () => _smtpSendService.SendAsync(
                    _db,
                    account,
                    proxies,
                    ProxyStartIndex(account, proxies),
                    request,
                    token),
                token);
            await _sendTask;

            var summary = recipients.Count == 1
                ? recipients[0].Address
                : recipients.Count + " recipients";
            ComposeStatusTextBlock.Text = "Sent to " + summary + ".";
            StatusTextBlock.Text = "Mail sent from " + account.Email + " to " + summary + ".";
            ClearComposeForm();
        }
        catch (OperationCanceledException)
        {
            ComposeStatusTextBlock.Text = "Sending cancelled.";
            StatusTextBlock.Text = "Sending cancelled.";
        }
        catch (Exception ex)
        {
            ComposeStatusTextBlock.Text = "Send failed.";
            if (!_closeAfterSend)
            {
                System.Windows.MessageBox.Show(
                    this,
                    ex.Message,
                    "Send failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        finally
        {
            _sendTask = null;
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            SendMailButton.IsEnabled = _selectedAccount != null && !IsAccountSyncing(_selectedAccount.Id);

            if (_closeAfterSend)
                Close();
        }
    }

    private void ClearComposeForm()
    {
        ComposeToTextBox.Text = "";
        ComposeSubjectTextBox.Text = "";
        ComposeBodyTextBox.Text = "";
        ComposeAttachments.Clear();
        _replyTarget = null;
    }

    /// <summary>
    /// Opens the answer in its own window. The bottom compose pane stays for new mail only.
    /// </summary>
    private async void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMessage is not { } message || _selectedAccount == null)
            return;

        if (!await EnsureAccessTokenCurrentAsync(_selectedAccount))
            return;

        var replyAddress = SmtpSendService.ExtractAddress(message.From);
        if (replyAddress.Length == 0)
        {
            StatusTextBlock.Text = "The sender address of this message could not be read.";
            return;
        }

        var subject = (message.Subject ?? "").Trim();
        if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
            subject = "Re: " + subject;

        var quote = ReplyQuote.Build(
            _selectedAccount.Site,
            message,
            ResolvePreviewText(message, _db.LoadMessageBody(message.Id)),
            _selectedAccount.Email);

        var window = new ReplyWindow(
            _db,
            _selectedAccount,
            _smtpSendService,
            replyAddress,
            subject,
            "",
            quote,
            message.MessageId,
            message.ThreadReferences,
            status => StatusTextBlock.Text = status)
        {
            Owner = this
        };
        window.Show();
    }

    /// <summary>
    /// S6: closing during a send asks whether to wait. "Yes" keeps the window until the
    /// message is delivered and then closes it; "No" aborts the send and closes now.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_sendTask is { IsCompleted: false } && !_closeAfterSend)
        {
            var answer = System.Windows.MessageBox.Show(
                this,
                "A message is still being sent." + Environment.NewLine + Environment.NewLine +
                "Yes вЂ” wait for it to finish and then close." + Environment.NewLine +
                "No вЂ” cancel the send and close now." + Environment.NewLine +
                "Cancel вЂ” keep the program open.",
                "Message is being sent",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            switch (answer)
            {
                case System.Windows.MessageBoxResult.Yes:
                    _closeAfterSend = true;
                    StatusTextBlock.Text = "Finishing the send, the window closes afterwards...";
                    e.Cancel = true;
                    return;
                case System.Windows.MessageBoxResult.No:
                    _sendCancellation?.Cancel();
                    break;
                default:
                    e.Cancel = true;
                    return;
            }
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Drops the folder filter so the list shows the whole account again. Without this there
    /// is no way back from a folder selection.
    /// </summary>
    private void ShowAllMessages_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAccount == null)
            return;

        ClearFolderSelection();
        ReloadMessages();
        ClearPreview();
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) =>
        OpenSettings_Click(sender, e);

    private void DeleteAccount_Click(object sender, RoutedEventArgs e) =>
        DeleteSelectedAccount();

    private void SaveAccounts_Click(object sender, RoutedEventArgs e) =>
        SaveAllAccountsToFile();

    private void DeleteAccountMenu_Click(object sender, RoutedEventArgs e) =>
        DeleteSelectedAccount();

    private void CopyAccountEmailMenu_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsListBox.SelectedItem is AccountEntity account)
            CopyToClipboard(account.Email, "Copied " + account.Email);
    }

    private void SaveAllAccountsToFile()
    {
        if (_syncRunning)
        {
            System.Windows.MessageBox.Show(this,
                "Wait until sync finishes before saving accounts.",
                "Save accounts",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_db.Accounts.Count() == 0)
            {
                StatusTextBlock.Text = "There are no accounts to save.";
                return;
            }

            var settings = _db.GetOrCreateSettings();
            var sourcePath = AppPaths.ResolveAccountFilePath(settings.SourceFile);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save all accounts",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = Path.GetFileName(sourcePath),
                InitialDirectory = Path.GetDirectoryName(sourcePath)
            };

            if (dialog.ShowDialog(this) != true)
                return;

            var result = _accountExportService.SaveAll(
                _db,
                dialog.FileName,
                settings.SourceFormat);

            StatusTextBlock.Text =
                "Saved " + result.AccountCount + " account(s) to " +
                Path.GetFileName(result.FilePath) + ".";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                ex.Message,
                "Save accounts failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void DeleteSelectedAccount()
    {
        if (_syncRunning)
        {
            System.Windows.MessageBox.Show(this,
                "Wait until sync finishes before deleting accounts.",
                "Delete account",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (AccountsListBox.SelectedItem is not AccountEntity account)
        {
            System.Windows.MessageBox.Show(this,
                "Select an account to delete.",
                "Delete account",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            "Delete account " + account.Email + " and all its mail?",
            "Delete account",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        _db.DeleteAccount(account.Id);
        _selectedAccount = null;
        _selectedFolder = null;
        ReloadAccounts();
        ReloadFolders();
        ReloadMessages();
        ClearPreview();
        StatusTextBlock.Text = "Deleted account: " + account.Email;
    }

    private void AccountFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _accountFilterDebounce.Stop();
        _accountFilterDebounce.Start();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholderTextBlock.Visibility = SearchTextBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void MessagesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        OpenSelectedMessageWindow();

    private void MessagesListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(MessagesListView, e.OriginalSource as DependencyObject) as System.Windows.Controls.ListViewItem;
        if (item?.DataContext is not MessageEntity)
            return;

        if (!item.IsSelected)
            MessagesListView.SelectedItem = item.DataContext;
    }

    private void MessagesContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        DownloadAttachmentsMenuItem.Visibility = _previewMessage?.HasAttachments == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ViewSource_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMessage == null)
            return;

        MessageBody body;
        try
        {
            body = _db.LoadMessageBody(_previewMessage.Id);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "View source", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var source = !string.IsNullOrWhiteSpace(body.Html) ? body.Html : body.Text;
        if (string.IsNullOrWhiteSpace(source))
            source = "(This message has no source yet. Open it once so the body is downloaded.)";

        new MessageSourceWindow(_previewMessage.Subject ?? "Message", source) { Owner = this }.Show();
    }

    private void OpenMessage_Click(object sender, RoutedEventArgs e) => OpenSelectedMessageWindow();

    private async void OpenSelectedMessageWindow()
    {
        if (_previewMessage == null)
            return;

        var openAccount = _selectedAccount?.Id == _previewMessage.AccountId
            ? _selectedAccount
            : _db.Accounts.FindById(_previewMessage.AccountId);
        if (openAccount != null && !await EnsureAccessTokenCurrentAsync(openAccount))
            return;

        // Bodies are downloaded on demand, so the selected message may still be in flight.
        var pending = _bodyFetch;
        if (pending != null)
        {
            try
            {
                await pending;
            }
            catch
            {
                // The failure is already reported in the preview pane.
            }
        }

        if (_previewMessage == null)
            return;

        new MessageViewWindow(_db, _previewMessage) { Owner = this }.Show();
    }

    private async void MessagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // An async void handler is the last stop before the process dies: a malformed
        // message must show an error in the preview pane, never take the window down.
        try
        {
            StopNeighbourPrefetch();

            if (MessagesListView.SelectedItem is not MessageEntity message)
            {
                ClearPreview();
                return;
            }

            _previewMessage = message;
            EnsureAccountForMessage(message);

            var stored = _db.LoadMessageBody(message.Id);
            ShowPreview(message, stored);

            var previewAccount = _selectedAccount?.Id == message.AccountId
                ? _selectedAccount
                : _db.Accounts.FindById(message.AccountId);
            if (previewAccount != null && !await EnsureAccessTokenCurrentAsync(previewAccount))
                return;

            if ((message.BodyLoaded ||
                 !string.IsNullOrEmpty(stored.Text) ||
                 !string.IsNullOrEmpty(stored.Html)) &&
                message.AttachmentInfoKnown)
            {
                StartNeighbourPrefetch(message);
                return;
            }

            if (await FetchPreviewBodyAsync(message))
                StartNeighbourPrefetch(message);
        }
        catch (Exception ex)
        {
            AppLog.Error("preview", "Selecting a message failed", ex);
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewBodyTextBlock.Text = "Could not show this message: " + ex.Message;
        }
    }

    /// <summary>
    /// P7: while the user reads one message, the next few unread bodies in the same list are
    /// pulled through the already open IMAP session so the following clicks are instant.
    /// Only the queue is abandoned on a new selection; a fetch already on the wire is
    /// allowed to finish, because cancelling a MailKit command drops the connection.
    /// </summary>
    private void StartNeighbourPrefetch(MessageEntity anchor)
    {
        if (_syncRunning || IsAccountSyncing(anchor.AccountId))
            return;

        var generation = Interlocked.Increment(ref _prefetchGeneration);
        var items = MessagesListView.Items.OfType<MessageEntity>().ToList();
        var index = items.IndexOf(anchor);
        if (index < 0)
            return;

        var candidates = items
            .Skip(index + 1)
            .Where(m => m.AccountId == anchor.AccountId && (!m.BodyLoaded || !m.AttachmentInfoKnown))
            .Take(NeighbourPrefetchCount)
            .ToList();
        if (candidates.Count == 0)
            return;

        var account = _selectedAccount?.Id == anchor.AccountId
            ? _selectedAccount
            : _db.Accounts.FindById(anchor.AccountId);
        if (account == null || AccessTokenOlderThanOneHour(account))
            return;

        List<ProxySpec> proxies;
        try
        {
            proxies = LoadProxies();
        }
        catch
        {
            return;
        }

        var start = ProxyStartIndex(account, proxies);
        var token = _lifetimeCancellation.Token;

        _prefetchTask = Task.Run(async () =>
        {
            foreach (var candidate in candidates)
            {
                if (generation != Volatile.Read(ref _prefetchGeneration) || token.IsCancellationRequested)
                    return;

                try
                {
                    await _bodyLoader.LoadAsync(_db, account, candidate, proxies, start, token);
                }
                catch
                {
                    // Prefetch is best effort; the click path reports real errors.
                    return;
                }
            }
        }, token);
    }

    private void StopNeighbourPrefetch() => Interlocked.Increment(ref _prefetchGeneration);

    private void ShowPreview(MessageEntity message, MessageBody stored)
    {
        var subject = message.Subject ?? "";
        var from = message.From ?? "";
        var body = ResolvePreviewText(message, stored) ?? "";

        PreviewSubjectTextBlock.Text = subject.Length > 0 ? subject : "(no subject)";
        PreviewMetaTextBlock.Text = from + "   ·   " +
                                    message.DateUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";

        if (!string.IsNullOrWhiteSpace(stored.Html))
        {
            PreviewTextHost.Visibility = Visibility.Collapsed;
            PreviewWebView.Visibility = Visibility.Visible;
            _ = ShowPreviewHtmlAsync(message, stored);
        }
        else
        {
            _previewAwaitingNavigation = false;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewWebView.Visibility = Visibility.Collapsed;
            PreviewTextHost.Visibility = Visibility.Visible;
            PreviewBodyTextBlock.Text = body.Length > PreviewBodyLimit
                ? body[..PreviewBodyLimit] + Environment.NewLine + "[...]"
                : body;
        }

        CopySenderButton.IsEnabled = true;
        OpenMessageButton.IsEnabled = true;
        ReplyButton.IsEnabled = _selectedAccount != null;
        DownloadAttachmentsButton.Visibility = message.HasAttachments
            ? Visibility.Visible
            : Visibility.Collapsed;

        _previewCode = VerificationCode.Find(subject, body);
        if (_previewCode != null)
        {
            CopyCodeButton.Content = "Copy " + _previewCode;
            CopyCodeButton.Visibility = Visibility.Visible;
        }
        else
        {
            CopyCodeButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowPreviewHtmlAsync(MessageEntity message, MessageBody stored)
    {
        PreviewLoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            await EnsurePreviewWebAsync();
            if (!ReferenceEquals(_previewMessage, message))
                return;

            _previewAwaitingNavigation = true;
            PreviewWebView.NavigateToString(MessageBodyDocument.Build(message, stored));
        }
        catch (Exception ex)
        {
            _previewAwaitingNavigation = false;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewWebView.Visibility = Visibility.Collapsed;
            PreviewTextHost.Visibility = Visibility.Visible;
            var plain = ResolvePreviewText(message, stored) ?? "";
            PreviewBodyTextBlock.Text = plain.Length > PreviewBodyLimit
                ? plain[..PreviewBodyLimit] + Environment.NewLine + "[...]"
                : plain;
            AppLog.Error("preview", "Could not render message HTML", ex);
        }
    }

    private async Task EnsurePreviewWebAsync()
    {
        if (PreviewWebView.CoreWebView2 != null)
            return;

        var environment = await WebView2Host.GetEnvironmentAsync();
        await PreviewWebView.EnsureCoreWebView2Async(environment);
        if (_previewWebSealed)
            return;

        _previewWebSealed = true;
        var core = PreviewWebView.CoreWebView2;
        if (core == null)
            return;

        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsReputationCheckingRequired = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!WebViewGuard.IsLocalUri(e.Request.Uri))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        // Clicked links open in the in-app browser behind the account proxy; the preview
        // itself never navigates away.
        core.NavigationStarting += (_, e) => WebViewGuard.HandleMessageNavigation(e, OpenPreviewLink);
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!WebViewGuard.IsLocalUri(e.Uri))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => WebViewGuard.HandleMessageNewWindow(e, OpenPreviewLink);
        WebViewContextMenu.Attach(PreviewWebView, OpenPreviewLink);
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationCompleted += (_, _) =>
        {
            if (!_previewAwaitingNavigation)
                return;

            _previewAwaitingNavigation = false;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
        };
    }

    private void OpenPreviewLink(string uri)
    {
        if (_previewMessage == null)
            return;

        StatusTextBlock.Text = MessageLinks.Open(this, _db, _previewMessage.AccountId, uri, out _);
    }

    private static string ResolvePreviewText(MessageEntity message, MessageBody stored)
    {
        if (!string.IsNullOrWhiteSpace(stored.Text))
            return stored.Text;

        if (!string.IsNullOrWhiteSpace(stored.Html))
            return MessageBodyDocument.StripHtml(stored.Html);

        return message.Preview ?? "";
    }

    /// <summary>
    /// Downloads a body the sync deliberately skipped. Selecting another message cancels it,
    /// so scrolling the list does not queue up connections.
    /// </summary>
    private async Task<bool> FetchPreviewBodyAsync(MessageEntity message)
    {
        _bodyFetchCancellation?.Cancel();
        _bodyFetchCancellation?.Dispose();
        _bodyFetchCancellation = new CancellationTokenSource();

        var token = _bodyFetchCancellation.Token;
        _previewLoadPercent = -1;
        SetPreviewLoadPercent(0);
        PreviewLoadingOverlay.Visibility = Visibility.Visible;
        Task<MessageBody>? fetch = null;

        try
        {
            var account = _selectedAccount?.Id == message.AccountId
                ? _selectedAccount
                : _db.Accounts.FindById(message.AccountId);

            if (account == null)
                return false;

            if (!await EnsureAccessTokenCurrentAsync(account))
                return false;

            var proxies = LoadProxies();
            var start = ProxyStartIndex(account, proxies);

            var progress = new Progress<int>(SetPreviewLoadPercent);
            fetch = Task.Run(
                () => _bodyLoader.LoadAsync(_db, account, message, proxies, start, token, progress),
                token);

            _bodyFetch = fetch;
            var body = await fetch;

            if (!ReferenceEquals(_previewMessage, message))
                return false;

            ShowPreview(message, body);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Another message was selected.
        }
        catch (Exception ex)
        {
            AppLog.Error("body.fetch", "Preview download failed for uid=" + message.Uid, ex);

            if (ReferenceEquals(_previewMessage, message))
            {
                _previewAwaitingNavigation = false;
                PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
                PreviewWebView.Visibility = Visibility.Collapsed;
                PreviewTextHost.Visibility = Visibility.Visible;
                PreviewBodyTextBlock.Text = "Could not download this message: " + ex.Message;
            }
        }
        finally
        {
            // A cancelled fetch may finish after the next selection has already started its
            // own task. It must not clear that newer task from under the Open button.
            if (ReferenceEquals(_bodyFetch, fetch))
                _bodyFetch = null;
        }

        return false;
    }

    /// <summary>Reads the proxy list the same way every network action in the window does.</summary>
    private List<ProxySpec> LoadProxies() => ProxyFile.Load(_db);

    private int _previewLoadPercent = -1;

    private void SetPreviewLoadPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent == _previewLoadPercent && percent != 0)
            return;

        _previewLoadPercent = percent;
        PreviewLoadPercentText.Text = percent + "%";

        const double diameter = 64;
        const double thickness = 3;
        var full = Math.PI * (diameter - thickness) / thickness;
        var drawn = full * percent / 100.0;
        PreviewLoadArc.StrokeDashArray = new System.Windows.Media.DoubleCollection { drawn, Math.Max(0, full - drawn) };
    }

    private void ClearPreview()
    {
        _bodyFetchCancellation?.Cancel();
        StopNeighbourPrefetch();
        _previewMessage = null;
        _previewCode = null;
        _previewAwaitingNavigation = false;
        PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
        PreviewWebView.Visibility = Visibility.Collapsed;
        PreviewTextHost.Visibility = Visibility.Visible;
        PreviewSubjectTextBlock.Text = "Select a message";
        PreviewMetaTextBlock.Text = "";
        PreviewBodyTextBlock.Text = "";
        CopyCodeButton.Visibility = Visibility.Collapsed;
        DownloadAttachmentsButton.Visibility = Visibility.Collapsed;
        CopySenderButton.IsEnabled = false;
        OpenMessageButton.IsEnabled = false;
        ReplyButton.IsEnabled = false;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_previewCode != null)
            CopyToClipboard(_previewCode, "Copied code " + _previewCode + ".");
    }

    private void CopySender_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMessage == null)
            return;

        var address = MailAddressText.ExtractFirst(_previewMessage.From);
        CopyToClipboard(address, "Copied " + address);
    }

    private async void DownloadAttachments_Click(object sender, RoutedEventArgs e)
    {
        var message = _previewMessage;
        if (message == null || !message.HasAttachments)
            return;

        var account = _selectedAccount?.Id == message.AccountId
            ? _selectedAccount
            : _db.Accounts.FindById(message.AccountId);
        if (account == null)
            return;

        if (!await EnsureAccessTokenCurrentAsync(account))
            return;

        var destination = AttachmentDownloadService.GetAccountDirectory(account);

        _attachmentDownloadCancellation?.Cancel();
        _attachmentDownloadCancellation?.Dispose();
        _attachmentDownloadCancellation = new CancellationTokenSource();
        DownloadAttachmentsButton.IsEnabled = false;
        StatusTextBlock.Text = "Downloading attachments from " + message.Subject + "...";

        try
        {
            var proxies = LoadProxies();
            _attachmentDownload = _attachmentDownloader.DownloadAsync(
                _db,
                account,
                message,
                proxies,
                ProxyStartIndex(account, proxies),
                destination,
                _attachmentDownloadCancellation.Token);
            var result = await _attachmentDownload;

            StatusTextBlock.Text =
                "Downloaded " + result.Count + " attachment(s) to " + destination + ".";
            System.Windows.MessageBox.Show(
                this,
                result.Count + " attachment(s) saved to:" + Environment.NewLine + destination,
                "Attachments downloaded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Attachment download cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Could not download attachments.";
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Attachment download failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            DownloadAttachmentsButton.IsEnabled = true;
            _attachmentDownload = null;
            _attachmentDownloadCancellation?.Dispose();
            _attachmentDownloadCancellation = null;
        }
    }

    private void CopyAccountEmailButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AccountEntity account })
            CopyToClipboard(account.Email, "Copied " + account.Email);
    }

    private void AccountNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AccountEntity account })
            OpenAccountCard(account);
    }

    private void AccountsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsClickOnButton(e.OriginalSource as DependencyObject))
            return;

        if (AccountsListBox.SelectedItem is not AccountEntity account || account.IsGroupPlaceholder)
            return;

        OpenAccountCard(account);
    }

    private static bool IsClickOnButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
                return true;

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OpenAccountCard(AccountEntity account)
    {
        List<ProxySpec> proxies;
        try
        {
            proxies = LoadProxies();
        }
        catch
        {
            proxies = [];
        }

        var lines = proxies.Select(p => p.Display).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current = account.ActiveProxy;
        if (string.IsNullOrWhiteSpace(current) && proxies.Count > 0)
            current = proxies[ProxyStartIndex(account, proxies)].Display;

        var dialog = new AccountNoteWindow(account.Email, account.Note, lines, current) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        account.Note = dialog.Note;

        var chosen = dialog.NewProxy.Length > 0 ? dialog.NewProxy : dialog.SelectedProxy;
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            try
            {
                var parsed = ProxySpec.Parse(chosen);
                account.ActiveProxy = parsed.Display;
                AppendProxyLine(chosen);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "Account", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        _db.Accounts.Update(account);
        AccountsListBox.Items.Refresh();
        StatusTextBlock.Text = "Saved " + account.Email + ".";
    }

    private void AppendProxyLine(string line)
    {
        var settings = _db.GetOrCreateSettings();
        var path = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Any(item =>
                item.Trim().Length > 0 &&
                !item.TrimStart().StartsWith('#') &&
                string.Equals(item.Trim(), line.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        File.AppendAllText(path, Environment.NewLine + line.Trim() + Environment.NewLine);
    }

    private void CopyComposeFrom_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAccount != null)
            CopyToClipboard(_selectedAccount.Email, "Copied " + _selectedAccount.Email);
    }

    private List<AccountEntity> GetSelectedAccounts() =>
        AccountsListBox.SelectedItems
            .OfType<AccountEntity>()
            .Where(x => !x.IsGroupPlaceholder)
            .ToList();

    private async void SyncSelectedAccounts_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedAccounts();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Select one or more accounts first.",
                "Sync selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await SyncSelectedAccountsAsync(selected);
    }

    private async Task SyncSelectedAccountsAsync(IReadOnlyList<AccountEntity> selected)
    {
        if (_syncRunning)
            return;

        _syncCancellation?.Dispose();
        _syncCancellation = new CancellationTokenSource();
        SetSyncProgressActive(true);
        StatusTextBlock.Text = "Syncing " + selected.Count + " selected account(s)...";
        _syncRunSuccess = 0;
        _syncRunTotal = selected.Count;
        SyncCountTextBlock.Text = "0 / " + _syncRunTotal + " synced";

        try
        {
            var settings = _db.GetOrCreateSettings();
            var proxies = LoadProxies();
            var progress = new Progress<string>(HandleSyncAllProgress);
            var started = DateTime.UtcNow;
            var syncTask = Task.Run(
                () => _syncOrchestrator.SyncAccountsAsync(
                    _db,
                    selected,
                    proxies,
                    settings.Threads,
                    progress,
                    _syncCancellation.Token),
                _syncCancellation.Token);

            _activeSyncTask = syncTask;
            var result = await syncTask;
            StatusTextBlock.Text =
                "Selected sync finished in " + FormatDuration(DateTime.UtcNow - started) +
                ". OK: " + result.Success +
                ", failed: " + result.Failed + ".";
            ClearFolderSelection();
            ReloadAccounts();
            ReloadFolders();
            ReloadMessages();
            _addressBook?.RefreshFromDatabase();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Sync cancelled.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Sync selected failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Sync failed.";
        }
        finally
        {
            _activeSyncTask = null;
            SetSyncProgressActive(false);
            _syncCancellation?.Dispose();
            _syncCancellation = null;
        }
    }

    private void ChangeSelectedProxy_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedAccounts();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Select one or more accounts first.",
                "Change proxy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new TextPromptDialog(
            "Change proxy",
            "Proxy line for the selected accounts. Same formats as data\\proxy.txt.",
            selected[0].ActiveProxy)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        ProxySpec parsed;
        try
        {
            parsed = ProxySpec.Parse(dialog.Value);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Change proxy", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        foreach (var account in selected)
        {
            account.ActiveProxy = parsed.Display;
            _db.Accounts.Update(account);
        }

        try
        {
            var settings = _db.GetOrCreateSettings();
            var path = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);
            var existing = File.Exists(path)
                ? File.ReadAllLines(path)
                : [];
            if (!existing.Any(line =>
                    line.Trim().Length > 0 &&
                    !line.TrimStart().StartsWith('#') &&
                    string.Equals(line.Trim(), dialog.Value, StringComparison.OrdinalIgnoreCase)))
            {
                File.AppendAllText(path, Environment.NewLine + dialog.Value + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Proxy assigned, but the proxy file was not updated: " + ex.Message;
            return;
        }

        StatusTextBlock.Text = "Proxy assigned to " + selected.Count + " account(s).";
    }

    private void ExportSelectedAccounts_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedAccounts();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Select one or more accounts first.",
                "Export selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var settings = _db.GetOrCreateSettings();
            var sourcePath = AppPaths.ResolveAccountFilePath(settings.SourceFile);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export selected accounts",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "accounts-selected.txt",
                InitialDirectory = Path.GetDirectoryName(sourcePath)
            };

            if (dialog.ShowDialog(this) != true)
                return;

            var result = _accountExportService.SaveAccounts(selected, dialog.FileName, settings.SourceFormat);
            StatusTextBlock.Text =
                "Exported " + result.AccountCount + " account(s) to " +
                Path.GetFileName(result.FilePath) + ".";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export selected failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EnsureAccountForMessage(MessageEntity message)
    {
        if (_selectedAccount?.Id == message.AccountId)
            return;

        var account = _db.Accounts.FindById(message.AccountId);
        if (account == null)
            return;

        _suppressAccountSelection = true;
        try
        {
            _selectedAccount = account;
            foreach (var item in AccountsListBox.Items.OfType<AccountEntity>())
            {
                if (item.Id != account.Id)
                    continue;

                AccountsListBox.SelectedItem = item;
                break;
            }

            ReloadFolders();
            UpdateComposePanel();
        }
        finally
        {
            _suppressAccountSelection = false;
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox
            or System.Windows.Controls.ComboBoxItem)
            return;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control &&
            MessagesListView.IsKeyboardFocusWithin)
        {
            CopySender_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            var selected = GetSelectedAccounts();
            if (selected.Count > 0)
                _ = SyncSelectedAccountsAsync(selected);
            else
                SyncAllAccounts_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (MessagesListView.IsKeyboardFocusWithin)
            {
                MessagesListView.SelectAll();
                e.Handled = true;
            }

            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.J:
                MoveMessageSelection(1);
                e.Handled = true;
                break;
            case Key.K:
                MoveMessageSelection(-1);
                e.Handled = true;
                break;
            case Key.R:
                Reply_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Enter:
                if (MessagesListView.IsKeyboardFocusWithin)
                {
                    OpenSelectedMessageWindow();
                    e.Handled = true;
                }
                break;
        }
    }

    private void MoveMessageSelection(int delta)
    {
        if (MessagesListView.Items.Count == 0)
            return;

        var index = MessagesListView.SelectedIndex;
        if (index < 0)
            index = delta > 0 ? -1 : 0;

        var next = Math.Clamp(index + delta, 0, MessagesListView.Items.Count - 1);
        MessagesListView.SelectedIndex = next;
        MessagesListView.ScrollIntoView(MessagesListView.SelectedItem);
        MessagesListView.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchDebounce.Stop();
        _accountFilterDebounce.Stop();
        _syncCancellation?.Cancel();
        foreach (var cancellation in _accountSyncCancellation.Values)
            cancellation.Cancel();
        _bodyFetchCancellation?.Cancel();
        _attachmentDownloadCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        SaveMainColumnWidths();
        SaveMessageColumnWidths();

        // Network work still writing to the database would fault on a disposed instance, so
        // give cancelled sync and body-fetch tasks a short window to unwind first.
        try
        {
            var pending = new Task?[] { _activeSyncTask, _bodyFetch, _attachmentDownload, _prefetchTask, _sendTask }
                .Concat(_accountSyncTasks.Values)
                .Where(task => task != null)
                .Cast<Task>()
                .ToArray();

            if (pending.Length > 0)
                Task.WaitAll(pending, TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Cancellation and network failures are already reported by their callers.
        }

        _syncCancellation?.Dispose();
        _bodyFetchCancellation?.Dispose();
        _attachmentDownloadCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        _bodyLoader.Dispose();
        try
        {
            PreviewWebView.Dispose();
        }
        catch
        {
            // Never initialised.
        }

        _db.Dispose();
        base.OnClosed(e);
    }
}
