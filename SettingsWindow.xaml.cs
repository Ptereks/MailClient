using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using ZennoVibe.MailClient.Core;
using ZennoVibe.MailClient.Data;
using ZennoVibe.MailClient.Services;

namespace ZennoVibe.MailClient;

public partial class SettingsWindow : FluentWindow
{
    private const string TagDragFormat = "ZennoVibe.FormatTag";

    private readonly MailDatabase _db;
    private readonly AccountImportService _importService = new();
    private readonly SyncOrchestrator _syncOrchestrator = new();
    private readonly Action<string>? _statusReporter;
    private readonly Action<bool>? _syncStateReporter;
    private readonly Action? _onDataChanged;
    private readonly ObservableCollection<FormatFieldItem> _availableTags = new();
    private readonly ObservableCollection<FormatFieldItem> _additionalTags = new();
    private readonly ObservableCollection<FormatFieldItem> _sequenceTags = new();
    private CancellationTokenSource? _syncCancellation;
    private TagDragPayload? _dragPayload;
    private Point _dragStart;
    private bool _dataChanged;
    private bool _closeNotified;

    internal SettingsWindow(
        MailDatabase db,
        Action<string>? statusReporter,
        Action<bool>? syncStateReporter,
        Action? onDataChanged)
    {
        _db = db;
        _statusReporter = statusReporter;
        _syncStateReporter = syncStateReporter;
        _onDataChanged = onDataChanged;
        InitializeComponent();
        WindowGlassChrome.Apply(this);
        AvailableTagsPanel.ItemsSource = _availableTags;
        AdditionalTagsPanel.ItemsSource = _additionalTags;
        FormatSequencePanel.ItemsSource = _sequenceTags;
        LoadSettings();
        SourceInitialized += (_, _) => Activate();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CancelRunningSync();
        NotifyDataChangedOnce();
    }

    private void NotifyDataChangedOnce()
    {
        if (_closeNotified)
            return;

        _closeNotified = true;
        if (_dataChanged)
            _onDataChanged?.Invoke();
    }

    /// <summary>
    /// The sync runs against the shared database, so it must not outlive this window: the
    /// main window clears its "sync in flight" state as soon as the dialog closes.
    /// </summary>
    private void CancelRunningSync()
    {
        try
        {
            _syncCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished and disposed its token source.
        }
    }

    private void LoadSettings()
    {
        var settings = _db.GetOrCreateSettings();
        ProxyFileTextBox.Text = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);
        AccountFileTextBox.Text = AppPaths.ResolveAccountFilePath(settings.SourceFile);
        ThreadsTextBox.Text = settings.Threads.ToString();
        MessageLimitTextBox.Text = settings.MessageLimit.ToString();
        ComposeCheckBox.IsChecked = settings.ComposeVisible;
        LoadMediaCheckBox.IsChecked = settings.LoadMedia;
        LoadFormatTags(settings.SourceFormat, settings.ExtraFormatTags);
    }

    private void LoadFormatTags(string? format, string? extraTags)
    {
        _sequenceTags.Clear();
        _availableTags.Clear();
        _additionalTags.Clear();

        foreach (var field in AccountFormat.ParseFormatSequence(format))
            _sequenceTags.Add(field);

        foreach (var key in AccountFormat.CoreKeys)
        {
            if (_sequenceTags.All(x => !x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                _availableTags.Add(AccountFormat.CreateItem(key));
        }

        var extraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AccountFormat.BuiltInExtraKeys)
            extraKeys.Add(key);
        foreach (var key in AccountFormat.ParseExtraTagKeys(extraTags))
            extraKeys.Add(key);

        foreach (var key in extraKeys)
        {
            if (_sequenceTags.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                continue;

            _additionalTags.Add(AccountFormat.CreateItem(key));
        }

        SortAvailableTags();
        SortAdditionalTags();
        UpdateFormatPreview();
    }

    private void SortAvailableTags() =>
        SortByKnownOrder(_availableTags, AccountFormat.CoreKeys);

    private void SortAdditionalTags() =>
        SortByKnownOrder(_additionalTags, AccountFormat.BuiltInExtraKeys);

    private static void SortByKnownOrder(ObservableCollection<FormatFieldItem> tags, string[] order)
    {
        var sorted = tags
            .OrderBy(x => RankOf(x.Key, order))
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        tags.Clear();
        foreach (var item in sorted)
            tags.Add(item);
    }

    private static int RankOf(string key, string[] order)
    {
        var index = Array.FindIndex(order, x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? order.Length : index;
    }

    private void SaveSettings()
    {
        var settings = _db.GetOrCreateSettings();
        settings.ProxyFile = AppPaths.NormalizeSavedDataFilePath(
            ProxyFileTextBox.Text.Trim(),
            AppPaths.DefaultProxyFile);
        settings.SourceFile = AppPaths.NormalizeSavedDataFilePath(
            AccountFileTextBox.Text.Trim(),
            AppPaths.DefaultAccountFile);
        settings.SourceFormat = BuildFormatString();
        settings.ExtraFormatTags = BuildExtraTagsString();
        settings.Threads = ParseThreads();
        settings.MessageLimit = ParseMessageLimit();
        settings.ComposeVisible = ComposeCheckBox.IsChecked == true;
        settings.LoadMedia = LoadMediaCheckBox.IsChecked == true;
        _db.SaveSettings(settings);
    }

    private string BuildFormatString()
    {
        if (_sequenceTags.Count == 0)
            return AccountFormat.Default;

        var keys = _sequenceTags.Select(x => x.Key).ToList();
        foreach (var key in AccountFormat.CoreKeys)
        {
            if (!keys.Any(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)))
                keys.Add(key);
        }

        return AccountFormat.ToFormatString(keys);
    }

    private string BuildExtraTagsString()
    {
        var custom = _additionalTags.Select(x => x.Key)
            .Concat(_sequenceTags.Select(x => x.Key))
            .Where(AccountFormat.IsExtraKey)
            .Where(key => !AccountFormat.IsBuiltInExtra(key))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return AccountFormat.ToExtraTagsString(custom);
    }

    private void UpdateFormatPreview()
    {
        FormatDropHintTextBlock.Visibility = _sequenceTags.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        FormatPreviewTextBlock.Text = _sequenceTags.Count == 0
            ? "Format: (empty — drop tags into the format box)"
            : "Format: " + AccountFormat.ToFormatString(_sequenceTags);
    }

    private int ParseThreads()
    {
        if (int.TryParse(ThreadsTextBox.Text.Trim(), out var value))
            return Math.Clamp(value, 1, 500);

        return 50;
    }

    private int ParseMessageLimit()
    {
        if (int.TryParse(MessageLimitTextBox.Text.Trim(), out var value))
            return Math.Clamp(value, 10, 2000);

        return 200;
    }

    private void BrowseProxy_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseTextFile(ProxyFileTextBox.Text, "proxy.txt");
        if (path != null)
            ProxyFileTextBox.Text = path;
    }

    private void BrowseAccount_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseTextFile(AccountFileTextBox.Text, "account.txt");
        if (path != null)
            AccountFileTextBox.Text = path;
    }

    private static string? BrowseTextFile(string current, string defaultName)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(current) ? defaultName : Path.GetFileName(current),
            InitialDirectory = string.IsNullOrWhiteSpace(current)
                ? AppPaths.DataDirectory
                : Path.GetDirectoryName(current) ?? AppPaths.DataDirectory
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void FormatTag_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FormatFieldItem item)
            return;

        _dragStart = e.GetPosition(null);
        _dragPayload = new TagDragPayload(item, ResolveTagSource(element));
    }

    private void FormatTag_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragPayload == null)
            return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(TagDragFormat, _dragPayload);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        _dragPayload = null;
    }

    private void FormatDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TagDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void FormatDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TagDragFormat) is not TagDragPayload payload)
            return;

        var insertIndex = GetSequenceInsertIndex(e.GetPosition(FormatSequencePanel));

        if (payload.Source == TagDragSource.Sequence)
        {
            var oldIndex = _sequenceTags.IndexOf(payload.Item);
            if (oldIndex < 0)
                return;

            insertIndex = Math.Clamp(insertIndex, 0, _sequenceTags.Count);
            if (insertIndex > oldIndex)
                insertIndex--;

            if (oldIndex != insertIndex)
                _sequenceTags.Move(oldIndex, insertIndex);
        }
        else
        {
            if (_sequenceTags.Any(x => x.Key.Equals(payload.Item.Key, StringComparison.OrdinalIgnoreCase)))
                return;

            RemoveFromPalette(payload);
            insertIndex = Math.Clamp(insertIndex, 0, _sequenceTags.Count);
            _sequenceTags.Insert(insertIndex, payload.Item);
        }

        UpdateFormatPreview();
        e.Handled = true;
    }

    private void PaletteTags_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TagDragFormat) is TagDragPayload payload && payload.Source == TagDragSource.Sequence)
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;

        e.Handled = true;
    }

    private void AvailableTags_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TagDragFormat) is not TagDragPayload payload || payload.Source != TagDragSource.Sequence)
            return;

        if (!AccountFormat.IsCoreKey(payload.Item.Key))
            return;

        MoveTagOutOfSequence(payload.Item);
        e.Handled = true;
    }

    private void AdditionalTags_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TagDragFormat) is not TagDragPayload payload || payload.Source != TagDragSource.Sequence)
            return;

        if (!AccountFormat.IsExtraKey(payload.Item.Key))
            return;

        MoveTagOutOfSequence(payload.Item);
        e.Handled = true;
    }

    private void RemoveFormatTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FormatFieldItem item)
            MoveTagOutOfSequence(item);

        e.Handled = true;
    }

    private void NewTagTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddExtraTagFromInput();
            e.Handled = true;
        }
    }

    private void AddExtraTagFromInput()
    {
        var key = AccountFormat.NormalizeCustomTag(NewTagTextBox.Text);
        if (key == null)
        {
            System.Windows.MessageBox.Show(this,
                "Tag name must contain only letters, numbers, and underscores.",
                "Invalid tag",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (TagExists(key))
        {
            System.Windows.MessageBox.Show(this,
                "This tag already exists.",
                "Duplicate tag",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _additionalTags.Add(AccountFormat.CreateItem(key));
        SortAdditionalTags();
        NewTagTextBox.Clear();
    }

    private bool TagExists(string key) =>
        _availableTags.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
        _additionalTags.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
        _sequenceTags.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private void MoveTagOutOfSequence(FormatFieldItem item)
    {
        if (!_sequenceTags.Remove(item))
            return;

        if (AccountFormat.IsCoreKey(item.Key))
        {
            if (!_availableTags.Any(x => x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _availableTags.Add(item);
                SortAvailableTags();
            }
        }
        else if (!_additionalTags.Any(x => x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
        {
            _additionalTags.Add(item);
            SortAdditionalTags();
        }

        UpdateFormatPreview();
    }

    private void RemoveFromPalette(TagDragPayload payload)
    {
        switch (payload.Source)
        {
            case TagDragSource.Available:
                _availableTags.Remove(payload.Item);
                break;
            case TagDragSource.Additional:
                _additionalTags.Remove(payload.Item);
                break;
        }
    }

    private int GetSequenceInsertIndex(Point point)
    {
        for (var i = 0; i < _sequenceTags.Count; i++)
        {
            if (FormatSequencePanel.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var topLeft = container.TransformToAncestor(FormatSequencePanel).Transform(new Point(0, 0));
            var rect = new Rect(topLeft, container.RenderSize);
            if (point.X < rect.Left + rect.Width / 2)
                return i;
        }

        return _sequenceTags.Count;
    }

    private static TagDragSource ResolveTagSource(FrameworkElement element)
    {
        var tag = element.Tag as string;
        return tag switch
        {
            "Sequence" => TagDragSource.Sequence,
            "Additional" => TagDragSource.Additional,
            _ => TagDragSource.Available
        };
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();
            var settings = _db.GetOrCreateSettings();
            var accountPath = AppPaths.ResolveAccountFilePath(settings.SourceFile);
            var result = _importService.ImportFromFile(_db, accountPath, settings.SourceFormat);
            _dataChanged = true;
            ActionStatusTextBlock.Text =
                $"Import done. New: {result.Imported}, updated: {result.Updated}, failed: {result.Failed}." +
                $" XOAUTH2: {result.XoauthCount}, app password: {result.AppPasswordCount}.";
            _statusReporter?.Invoke(ActionStatusTextBlock.Text);
        }
        catch (Exception ex)
        {
            AppLog.Error("import", "Import failed.", ex);

            // Part of the file may already be in the database, so the account list still
            // has to be refreshed when the window closes.
            _dataChanged = true;

            System.Windows.MessageBox.Show(this, ex.Message, "Import failed", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_syncCancellation != null)
            return;

        try
        {
            SaveSettings();
            var settings = _db.GetOrCreateSettings();
            var proxyPath = AppPaths.ResolveDataFilePath(settings.ProxyFile, AppPaths.DefaultProxyFile);

            if (!File.Exists(proxyPath))
                throw new FileNotFoundException("Proxy file not found.", proxyPath);

            var proxies = ProxySpec.LoadFromFile(proxyPath);
            if (proxies.Count == 0)
                throw new InvalidOperationException("Proxy file is empty.");

            SetSyncRunning(true);
            _syncStateReporter?.Invoke(true);
            _syncCancellation = new CancellationTokenSource();

            var progress = new Progress<string>(line =>
            {
                ActionStatusTextBlock.Text = line;
                _statusReporter?.Invoke(line);
                if (line.StartsWith("PRIORITY ", StringComparison.Ordinal))
                    _onDataChanged?.Invoke();
            });

            var result = await Task.Run(
                () => _syncOrchestrator.SyncAllAsync(
                    _db,
                    proxies,
                    settings.Threads,
                    progress,
                    _syncCancellation.Token),
                _syncCancellation.Token);

            _dataChanged = true;
            ActionStatusTextBlock.Text =
                $"Sync finished. OK: {result.Success}, failed: {result.Failed}, threads: {result.EffectiveThreads}.";
            _statusReporter?.Invoke(ActionStatusTextBlock.Text);
        }
        catch (OperationCanceledException)
        {
            ActionStatusTextBlock.Text = "Sync cancelled.";
            _statusReporter?.Invoke(ActionStatusTextBlock.Text);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Sync failed", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
            ActionStatusTextBlock.Text = "Sync failed.";
            _statusReporter?.Invoke(ActionStatusTextBlock.Text);
        }
        finally
        {
            _syncCancellation?.Dispose();
            _syncCancellation = null;
            SetSyncRunning(false);
            _syncStateReporter?.Invoke(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRunningSync();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
        Activate();
        Focus();
    }

    private void Documentation_Click(object sender, RoutedEventArgs e)
    {
        new DocumentationWindow { Owner = this }.Show();
        Activate();
        Focus();
    }

    private void ExportDatabase_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            this,
            "The export is NOT encrypted: it contains every password, refresh token and message. " +
            "Keep the file private." + Environment.NewLine + Environment.NewLine + "Continue?",
            "Export database",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export database (unencrypted)",
            Filter = "LiteDB database (*.db)|*.db|All files (*.*)|*.*",
            DefaultExt = ".db",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "mailclient-export-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".db",
            InitialDirectory = AppPaths.DataDirectory
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _db.ExportUnencrypted(dialog.FileName);
            ActionStatusTextBlock.Text = "Database exported to " + dialog.FileName;
            _statusReporter?.Invoke(ActionStatusTextBlock.Text);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Export failed",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetSyncRunning(bool running)
    {
        ImportButton.IsEnabled = !running;
        SyncButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        AboutButton.IsEnabled = !running;
        DocumentationButton.IsEnabled = !running;
        ExportDatabaseButton.IsEnabled = !running;
        ProxyFileTextBox.IsEnabled = !running;
        AccountFileTextBox.IsEnabled = !running;
        AvailableTagsPanel.IsEnabled = !running;
        AdditionalTagsDropZone.IsEnabled = !running;
        FormatDropZone.IsEnabled = !running;
        NewTagTextBox.IsEnabled = !running;
        ThreadsTextBox.IsEnabled = !running;
        MessageLimitTextBox.IsEnabled = !running;
        ComposeCheckBox.IsEnabled = !running;
        LoadMediaCheckBox.IsEnabled = !running;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DialogResult != null)
            return;

        CancelRunningSync();
        SaveSettings();
        NotifyDataChangedOnce();
        DialogResult = true;
    }

    private void CloseCancel_Click(object sender, RoutedEventArgs e)
    {
        if (DialogResult != null)
            return;

        CancelRunningSync();
        NotifyDataChangedOnce();
        DialogResult = false;
    }

    private enum TagDragSource
    {
        Available,
        Additional,
        Sequence
    }

    private sealed record TagDragPayload(FormatFieldItem Item, TagDragSource Source);
}
