using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace D2XPad.Desktop;

internal static class WindowCaption
{
    private const uint UseImmersiveDarkMode = 20;
    private const uint CaptionColor = 35;
    private const uint TextColor = 36;

    internal static void UseDark(Window window) => window.SourceInitialized += ApplyDark;

    private static void ApplyDark(object? sender, EventArgs e)
    {
        if (sender is not Window window || SystemParameters.HighContrast) return;
        var handle = new WindowInteropHelper(window).Handle;
        SetAttribute(handle, UseImmersiveDarkMode, 1);

        // Windows 11 supports explicit caption colors while retaining the system-drawn frame.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        SetColor(window, handle, CaptionColor, "HeaderBackground");
        SetColor(window, handle, TextColor, "PrimaryText");
    }

    private static void SetColor(Window window, nint handle, uint attribute, string resource)
    {
        if (window.TryFindResource(resource) is not SolidColorBrush brush) return;
        var color = brush.Color;
        uint colorRef = (uint)(color.R | color.G << 8 | color.B << 16);
        SetAttribute(handle, attribute, colorRef);
    }

    private static void SetAttribute(nint handle, uint attribute, uint value) =>
        DwmSetWindowAttribute(handle, attribute, in value, sizeof(uint));

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in uint value, uint size);
}
