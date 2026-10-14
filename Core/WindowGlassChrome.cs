using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ZennoVibe.MailClient.Core;

/// <summary>
/// Window glass without third-party acrylic libraries.
/// Windows 11 uses FluentWindow SystemBackdrop; Windows 10 uses DWM blur-behind.
/// </summary>
internal static class WindowGlassChrome
{
    private const uint DwmBbEnable = 0x1;
    private static readonly Brush GlassBackground = new SolidColorBrush(Color.FromArgb(0xB3, 0x16, 0x16, 0x16));

    public static void Apply(FluentWindow window)
    {
        window.Loaded += (_, _) => ApplyNow(window);
        if (window.IsLoaded)
            ApplyNow(window);
    }

    private static void ApplyNow(FluentWindow window)
    {
        // Glass is decoration: whatever goes wrong here must never take the window down.
        try
        {
            if (IsWindows11())
            {
                // Not through WindowBackdropType: changing that property after load makes
                // FluentWindow re-set its WindowChrome, and on the second window that throws
                // ("The provided DependencyObject is not a context for this Freezable").
                // The DWM backdrop itself is applied directly.
                if (WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Acrylic))
                    WindowBackdrop.RemoveBackground(window);

                window.Background = GlassBackground;
                return;
            }

            window.Background = GlassBackground;
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
            EnableBlurBehind(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warning("window.glass", "Glass effect not applied to " + window.GetType().Name, ex);
        }
    }

    private static bool IsWindows11()
    {
        var version = Environment.OSVersion.Version;
        return version.Major >= 10 && version.Build >= 22000;
    }

    private static void EnableBlurBehind(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var blur = new DwmBlurBehind
        {
            DwFlags = DwmBbEnable,
            FEnable = true,
            HRgnBlur = IntPtr.Zero,
            FTransitionOnMaximized = false
        };

        _ = DwmEnableBlurBehindWindow(hwnd, ref blur);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DwmBlurBehind pBlurBehind);

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint DwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool FEnable;
        public IntPtr HRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool FTransitionOnMaximized;
    }
}
