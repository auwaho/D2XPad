using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace D2XPad.Desktop;

public partial class ProfileEditor
{
    private Size? preferredMinimum;

    private void FitToWorkArea(object? sender, EventArgs e)
    {
        // CenterOwner starts the editor on the owner's monitor. Subsequent DPI
        // changes apply the work area of the monitor now hosting the editor.
        var monitorWindow = e is DpiChangedEventArgs ? this : Owner ?? this;
        var handle = new WindowInteropHelper(monitorWindow).Handle;
        if (handle == IntPtr.Zero) return;
        var workArea = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = e is DpiChangedEventArgs changed ? changed.NewDpi : VisualTreeHelper.GetDpi(monitorWindow);
        double width = workArea.Width / dpi.DpiScaleX;
        double height = workArea.Height / dpi.DpiScaleY;
        preferredMinimum ??= new(MinWidth, MinHeight);
        MinWidth = Math.Min(preferredMinimum.Value.Width, width);
        MinHeight = Math.Min(preferredMinimum.Value.Height, height);
        MaxWidth = width;
        MaxHeight = height;
    }
}
