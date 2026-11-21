using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace D2XPad.Desktop;

public partial class App : Application
{
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? showListener;
    private MainWindow? window;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? menu;
    private Stream? iconStream;
    private Icon? trayIcon;
    private Stream? stoppedIconStream;
    private Icon? stoppedIcon;
    private DispatcherTimer? trayTimer;
    private bool readingOutput;
    private bool stopping, closing;
    private const string OutputOff = "D2XPad · Xbox output off";
    private const string OutputOn = "D2XPad · Xbox output on";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            showRequest = new EventWaitHandle(false, EventResetMode.AutoReset,
                $@"Local\D2XPad.Desktop.Show.{identity.User!.Value}", out bool first);
            if (!first)
            {
                AllowExistingWindowToActivate();
                showRequest.Set();
                Shutdown();
                return;
            }

            var iconUri = new Uri("pack://application:,,,/Assets/D2X.ico");
            var stoppedIconUri = new Uri("pack://application:,,,/Assets/D2X-stopped.ico");
            window = new MainWindow { Icon = BitmapFrame.Create(iconUri) };
            MainWindow = window;
            window.Closing += WindowClosing;
            iconStream = GetResourceStream(iconUri).Stream;
            trayIcon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);
            stoppedIconStream = GetResourceStream(stoppedIconUri).Stream;
            stoppedIcon = new Icon(stoppedIconStream, Forms.SystemInformation.SmallIconSize);
            menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open D2XPad", null, (_, _) => ShowWindow());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit D2XPad", null, async (_, _) => await ExitFromTrayAsync());
            tray = new Forms.NotifyIcon { Icon = stoppedIcon, Text = OutputOff, ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowWindow(); };
            showListener = ThreadPool.RegisterWaitForSingleObject(showRequest,
                (_, _) => Dispatcher.InvokeAsync(ShowWindow), null, Timeout.Infinite, false);
            window.BeginAutomaticConversion();
            if (!window.ShouldStartMinimized) window.Show();
            trayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            trayTimer.Tick += RefreshTray;
            trayTimer.Start();
        }
        catch (Exception ex)
        {
            closing = true;
            MessageBox.Show($"D2XPad could not open.\n\n{ex.Message}", "D2XPad", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void RefreshTray(object? sender, EventArgs e)
    {
        if (readingOutput || closing || stopping || window is null || tray is null) return;
        readingOutput = true;
        try
        {
            await window.PollAutomaticConversionAsync();
            if (closing || stopping) return;
            bool? active = await window.ReadTrayOutputAsync();
            if (active is null || closing || stopping) return;
            var next = active.Value ? trayIcon : stoppedIcon;
            if (tray.Icon == next) return;
            tray.Icon = next;
            tray.Text = active.Value ? OutputOn : OutputOff;
        }
        finally { readingOutput = false; }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        window!.Hide();
    }

    private void ShowWindow()
    {
        if (closing || window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async Task ExitFromTrayAsync()
    {
        if (stopping || window is null) return;
        stopping = true;
        try
        {
            await window.StopForExitAsync();
            closing = true;
            Shutdown();
        }
        catch (Exception ex)
        {
            stopping = false;
            ShowWindow();
            MessageBox.Show(window, $"Could not stop the converter.\n\n{ex.Message}", "D2XPad",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel) closing = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        closing = true;
        trayTimer?.Stop();
        showListener?.Unregister(null);
        showRequest?.Dispose();
        if (tray is not null) tray.Visible = false;
        tray?.Dispose();
        menu?.Dispose();
        trayIcon?.Dispose();
        stoppedIcon?.Dispose();
        iconStream?.Dispose();
        stoppedIconStream?.Dispose();
        base.OnExit(e);
    }

    // A second launch from Explorer can transfer its foreground activation rights to the open window.
    private static void AllowExistingWindowToActivate()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                try
                {
                    if (other.Id != current.Id && other.SessionId == current.SessionId &&
                        string.Equals(other.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        AllowSetForegroundWindow(other.Id);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
