using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZennoVibe.MailClient;

/// <summary>
/// Close (and optionally maximize) buttons placed in <c>TitleBar.TrailingContent</c>.
/// They are ordinary client-area buttons, so a click is a normal WPF click: nothing depends
/// on the non-client hit-test hook that WPF-UI uses for its built-in caption buttons.
/// </summary>
internal partial class CaptionButtons : UserControl
{
    private const string MaximizeGlyphData = "M0.5,0.5 H9.5 V9.5 H0.5 Z";
    private const string RestoreGlyphData = "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z";

    public static readonly DependencyProperty ShowMaximizeProperty = DependencyProperty.Register(
        nameof(ShowMaximize),
        typeof(bool),
        typeof(CaptionButtons),
        new PropertyMetadata(false, (d, e) => ((CaptionButtons)d).ApplyShowMaximize()));

    private Window? _window;

    public CaptionButtons()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool ShowMaximize
    {
        get => (bool)GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window != null)
            _window.StateChanged += Window_StateChanged;

        ApplyShowMaximize();
        UpdateMaximizeGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null)
            _window.StateChanged -= Window_StateChanged;

        _window = null;
    }

    private void ApplyShowMaximize() =>
        MaximizeButton.Visibility = ShowMaximize ? Visibility.Visible : Visibility.Collapsed;

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? RestoreGlyphData : MaximizeGlyphData);
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        var window = _window ?? Window.GetWindow(this);
        if (window == null)
            return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        var window = _window ?? Window.GetWindow(this);
        if (window == null)
            return;

        try
        {
            // Close() is enough for modal windows too: ShowDialog returns null, which every
            // caller already treats as "cancelled". Each window's Closing handler does its
            // own cleanup, so nothing else is needed here.
            window.Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing.
        }
    }
}
