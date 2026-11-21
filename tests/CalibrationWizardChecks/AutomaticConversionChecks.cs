using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class AutomaticConversionChecks
{
    private const string SettingsVariable = "D2XPAD_DESKTOP_SETTINGS";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Action<MainWindow, EngineFrame?> UpdateSession = typeof(MainWindow)
        .GetMethod("UpdateSession", PrivateInstance)!.CreateDelegate<Action<MainWindow, EngineFrame?>>();
    private static readonly Action<MainWindow, IReadOnlyList<DeviceChoice>> SetDevices = typeof(MainWindow)
        .GetMethod("SetDevices", PrivateInstance)!.CreateDelegate<Action<MainWindow, IReadOnlyList<DeviceChoice>>>();
    private static readonly Action<MainWindow> Draw = typeof(MainWindow)
        .GetMethod("Draw", PrivateInstance)!.CreateDelegate<Action<MainWindow>>();

    public static void Run(string directory)
    {
        string? previousSettings = Environment.GetEnvironmentVariable(SettingsVariable);
        string settings = Path.Combine(directory, "automatic-conversion.json");
        Environment.SetEnvironmentVariable(SettingsVariable, settings);
        try
        {
            MatchSavedInstance();
            CheckSettings(settings);
            StopPendingConversion();
            Console.WriteLine("Passed automatic conversion opt-in, saved instance matching and stopping a hidden wait without a virtual device.");
        }
        finally { Environment.SetEnvironmentVariable(SettingsVariable, previousSettings); }
    }

    private static void MatchSavedInstance()
    {
        var saved = new AutomaticConversionTarget("fixture-a", "layout-a", "container-a");
        var original = new DeviceChoice("FIXTURE-A", "Selected controller", "layout-a", ContainerId: "CONTAINER-A");
        var sameModel = original with { Path = "fixture-b", ContainerId = "container-b" };
        var moved = original with { Path = "fixture-new" };
        var otherLayout = moved with { LayoutId = "layout-b" };
        var misleadingPath = original with { ContainerId = "container-b" };

        Expect(ReferenceEquals(saved.Select([sameModel, original]), original), "The exact saved interface must take precedence.");
        Expect(saved.Select([sameModel, misleadingPath]) == null, "A matching model or conflicting container must never replace the selected device.");
        Expect(ReferenceEquals(saved.Select([misleadingPath, otherLayout, moved]), moved),
            "A changed interface can reattach when its saved container and layout uniquely match.");
        Expect(saved.Select([moved, moved with { Path = "fixture-ambiguous" }]) == null,
            "An ambiguous container match must wait for the exact interface.");
        Expect(saved.Select([original with { LayoutId = "layout-b" }]) == null,
            "An exact path and container cannot override a different layout.");
        var legacy = saved with { ContainerId = null };
        Expect(ReferenceEquals(legacy.Select([sameModel, original]), original) && legacy.Select([moved]) == null,
            "A legacy saved device with no container ID must use its exact interface path.");
    }

    private static void CheckSettings(string file)
    {
        var layout = DeviceLayout.Bluetooth85HF;
        File.WriteAllText(file, $$"""{"DevicePath":"fixture-a","LayoutId":"{{layout.Id}}","Profile":"southpaw-buttons","StartMinimized":true}""");
        var preferences = new DesktopPreferences();
        Expect(!preferences.StartAutomatically && preferences.ContainerId == null && preferences.StartMinimized &&
            preferences.DevicePath == "fixture-a" && preferences.ReadProfile(layout.Id) == "southpaw-buttons",
            "Older preferences must preserve the last input and profile while defaulting automatic conversion to off.");

        preferences.SaveDevice("fixture-a", layout.Id, "container-a");
        preferences.SaveStartAutomatically(true);
        preferences.SaveDevice("fixture-a", layout.Id);
        var reopened = new DesktopPreferences();
        Expect(reopened.StartAutomatically && reopened.ContainerId == "container-a" && reopened.StartMinimized &&
            reopened.ReadProfile(layout.Id) == "southpaw-buttons", "Persisting automatic conversion must preserve existing settings and device identity.");
    }

    private static void StopPendingConversion()
    {
        var window = new MainWindow();
        try
        {
            Expect(!window.IsVisible && window.StartAutomatically.IsChecked == true &&
                Equals(window.StartAutomatically.Content, "Start conversion automatically"),
                "The automatic conversion preference and its concise label must load before the window is shown.");
            UpdateSession(window, null);
            window.BeginAutomaticConversion();
            var layout = DeviceLayout.Bluetooth85HF;
            SetDevices(window, [new("fixture-b", "Other controller", layout.Id, layout, "container-b")]);
            Draw(window);
            Expect(window.ActivityText.Text == "Waiting for controller" && Equals(window.PowerButton.Content, "Stop conversion") &&
                window.PowerButton.IsEnabled && window.VirtualStatus.Visibility == Visibility.Hidden,
                "Waiting must offer Stop conversion while leaving the Xbox slot unassigned.");
            Expect(new DesktopPreferences().DevicePath == "fixture-a", "Enumerating another controller must not replace the automatic input.");

            window.PowerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(window.ActivityText.Text == "Stopped" && Equals(window.PowerButton.Content, "Start conversion") &&
                new DesktopPreferences().StartAutomatically,
                "Stopping the wait must take effect immediately without changing the next-launch preference.");

            SetDevices(window, []);
            Draw(window);
            Expect(window.PowerButton.IsEnabled, "A saved input must allow manual Start while disconnected.");
            window.PowerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(window.ActivityText.Text == "Waiting for controller" && Equals(window.PowerButton.Content, "Stop conversion") &&
                window.VirtualStatus.Visibility == Visibility.Hidden,
                "Starting while disconnected must rearm the wait without allocating an Xbox slot.");
        }
        finally { window.Close(); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
