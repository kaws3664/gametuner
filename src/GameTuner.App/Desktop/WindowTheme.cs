using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameTuner.App.Desktop;

public static class WindowTheme
{
    public static readonly DependencyProperty CaptionBackgroundProperty = RegisterBrush("CaptionBackground");
    public static readonly DependencyProperty CaptionForegroundProperty = RegisterBrush("CaptionForeground");
    public static readonly DependencyProperty FrameBorderProperty = RegisterBrush("FrameBorder");

    public static SolidColorBrush? GetCaptionBackground(DependencyObject window) =>
        (SolidColorBrush?)window.GetValue(CaptionBackgroundProperty);
    public static void SetCaptionBackground(DependencyObject window, SolidColorBrush value) =>
        window.SetValue(CaptionBackgroundProperty, value);

    public static SolidColorBrush? GetCaptionForeground(DependencyObject window) =>
        (SolidColorBrush?)window.GetValue(CaptionForegroundProperty);
    public static void SetCaptionForeground(DependencyObject window, SolidColorBrush value) =>
        window.SetValue(CaptionForegroundProperty, value);

    public static SolidColorBrush? GetFrameBorder(DependencyObject window) =>
        (SolidColorBrush?)window.GetValue(FrameBorderProperty);
    public static void SetFrameBorder(DependencyObject window, SolidColorBrush value) =>
        window.SetValue(FrameBorderProperty, value);

    private static DependencyProperty RegisterBrush(string name) =>
        DependencyProperty.RegisterAttached(name, typeof(SolidColorBrush), typeof(WindowTheme),
            new PropertyMetadata(null, OnBrushChanged));

    private static void OnBrushChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window) return;
        if (new WindowInteropHelper(window).Handle == nint.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }
        UpdateFrame(window);
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        UpdateFrame(window);
    }

    private static void UpdateFrame(Window window)
    {
        if (GetCaptionBackground(window) is not { } background
            || GetCaptionForeground(window) is not { } foreground
            || GetFrameBorder(window) is not { } border
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        var dark = System.Windows.Application.Current is App app && app.IsDarkTheme ? 1 : 0;
        SetAttribute(handle, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) ? 20 : 19, dark);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            SetAttribute(handle, 35, ColorRef(background.Color));
            SetAttribute(handle, 36, ColorRef(foreground.Color));
            SetAttribute(handle, 34, ColorRef(border.Color));
        }
    }

    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static void SetAttribute(nint handle, int attribute, int value) =>
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
