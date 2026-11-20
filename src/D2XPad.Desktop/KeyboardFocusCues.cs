using System.Windows;
using System.Windows.Input;

namespace D2XPad.Desktop;

public static class KeyboardFocusCues
{
    public static readonly DependencyProperty VisibleProperty = DependencyProperty.RegisterAttached(
        "Visible", typeof(bool), typeof(KeyboardFocusCues), new PropertyMetadata(true));

    public static bool GetVisible(DependencyObject element) => (bool)element.GetValue(VisibleProperty);

    internal static void Track(Window window)
    {
        // WPF keeps an existing adorner if a click doesn't move keyboard focus.
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) =>
            window.SetCurrentValue(VisibleProperty, SystemParameters.KeyboardCues)), handledEventsToo: true);
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, _) =>
            window.SetCurrentValue(VisibleProperty, true)), handledEventsToo: true);
    }
}
