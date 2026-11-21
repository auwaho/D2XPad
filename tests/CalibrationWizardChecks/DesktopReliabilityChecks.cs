using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using D2XPad.Capture;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class DesktopReliabilityChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Action<MainWindow, EngineFrame?> UpdateSession = typeof(MainWindow)
        .GetMethod("UpdateSession", PrivateInstance)!.CreateDelegate<Action<MainWindow, EngineFrame?>>();
    private static readonly Action<MainWindow> Draw = typeof(MainWindow).GetMethod("Draw", PrivateInstance)!
        .CreateDelegate<Action<MainWindow>>();
    private static readonly Action<MainWindow, IReadOnlyList<DeviceChoice>> SetDevices = typeof(MainWindow)
        .GetMethod("SetDevices", PrivateInstance)!.CreateDelegate<Action<MainWindow, IReadOnlyList<DeviceChoice>>>();

    public static void Run(string settings, string layouts)
    {
        LockedPreferences(settings);
        MissingLayout(layouts);
        ChildExit();
        Console.WriteLine("Passed desktop session display with locked settings, removed layout and unexpected converter exit.");
        TesterWhileReconnecting();
        Console.WriteLine("Passed connection and release guidance, independent physical and Xbox readings, and quiet extras.");
        TesterSourceAfterReconnect();
        Console.WriteLine("Passed tester source selection across calibration and reconnect with conversion stopped.");
        ConverterGeometry(DeviceLayout.Bluetooth85HF, hasExtras: true);
        ConverterGeometry(SaveSingleStickLayout(0xBE04, "Layout with no extras"), hasExtras: false);
        Console.WriteLine("Passed converter card alignment and anchored action at both viewport sizes with and without extras.");
        SingleLineStatusBar();
        Console.WriteLine("Passed fixed-height status layout, message priorities and diagnostic action visibility.");
        KeyboardFocusStyles();
        Console.WriteLine("Passed shared native focus styles for tabs, the mint conversion button, selectors and tester toggles.");
    }

    private static EngineFrame Session(DeviceLayout layout, string profile) =>
        new("desktop-check", profile, 0, true, false, false, 1, null, null, LayoutId: layout.Id);

    private static void LockedPreferences(string settings)
    {
        var window = new MainWindow();
        try
        {
            using var locked = File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.Read);
            var active = Session(DeviceLayout.Bluetooth85HF, "southpaw-buttons");
            UpdateSession(window, active);
            Draw(window);
            Expect(window.StatusMessage.Text.StartsWith("Couldn't save settings.", StringComparison.Ordinal) &&
                window.CopyStatusDetailsButton.Visibility == Visibility.Visible &&
                !string.IsNullOrWhiteSpace(window.StatusTooltipText.Text),
                "The failed preference write must show a short summary and offer diagnostic details.");
            ExpectRunning(window);
            UpdateSession(window, active);
            Draw(window);
            ExpectRunning(window);
            UpdateSession(window, null);
            Draw(window);
            Expect(window.ActivityText.Text == "Stopped", "The window must accept a stopped frame with preferences locked.");
        }
        finally { window.Close(); }
    }

    private static void MissingLayout(string layouts)
    {
        var layout = SaveSingleStickLayout(0xBE03, "Snapshot controller");
        var active = Session(layout, layout.DefaultProfileName);
        var alreadyAttached = new MainWindow();
        var fresh = new MainWindow();
        try
        {
            UpdateSession(alreadyAttached, active);
            File.Move(Path.Combine(layouts, layout.Id + ".json"), Path.Combine(layouts, layout.Id + ".removed"));
            Directory.SetLastWriteTimeUtc(layouts, DateTime.UtcNow.AddSeconds(1));
            Expect(DeviceLayout.Supported.All(item => item.Id != layout.Id), "The test layout must be absent from the catalog.");
            Draw(alreadyAttached);
            Expect(alreadyAttached.DeviceTitle.Text == layout.DisplayName, "The active layout snapshot was lost.");
            ExpectRunning(alreadyAttached);

            UpdateSession(fresh, active);
            Draw(fresh);
            Expect(fresh.DeviceTitle.Text == "Physical controller", "A fresh attach should display a generic controller when its layout is missing.");
            ExpectRunning(fresh);
            UpdateSession(alreadyAttached, null);
            Draw(alreadyAttached);
            Expect(alreadyAttached.ActivityText.Text == "Stopped", "Stopping must release the cached live layout.");
        }
        finally
        {
            alreadyAttached.Close();
            fresh.Close();
        }
    }

    private static DeviceLayout SaveSingleStickLayout(ushort pid, string name)
    {
        var pad = new HidCollection(1, "snapshot-fixture", 0xDE03, pid, 1, 5, "USB", "test-container", 3, [7],
            [new(7, 1, 0x30, 8, 8, 0, 255)]);
        var calibration = new ControllerCalibration(pad, name);
        calibration.AddNeutral([7, 128, 0]);
        calibration.AddNeutral([7, 128, 0]);
        calibration.BindAxis("LX", [7, 0, 0], [7, 0, 0], [7, 255, 0], [7, 255, 0]);
        return calibration.Save();
    }

    private static void ChildExit()
    {
        using var client = new EngineClient();
        void OwnExit(int code, bool ready)
        {
            var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"echo Fixture stderr. 1>&2 & exit /b {code}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true
            })!;
            var launch = typeof(EngineClient).GetNestedType("DesktopLaunch", BindingFlags.NonPublic)!;
            var instance = Activator.CreateInstance(launch, process)!;
            launch.GetProperty("Ready")!.SetValue(instance, ready);
            typeof(EngineClient).GetField("owned", PrivateInstance)!.SetValue(client, instance);
            ((Task)launch.GetProperty("Exit")!.GetValue(instance)!).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        OwnExit(23, ready: true);
        Expect(client.TakeRuntimeFailure() == "Fixture stderr.", "The converter's runtime failure must be shown.");
        Expect(client.TakeRuntimeFailure() == null, "A runtime failure must be consumed once.");
        OwnExit(0, ready: true);
        Expect(client.TakeRuntimeFailure() == null, "A normal exit can include stderr without being a runtime failure.");
        OwnExit(23, ready: false);
        Expect(client.TakeRuntimeFailure() == null, "The startup path handles an exit before readiness.");
    }

    private static void TesterWhileReconnecting()
    {
        var window = new MainWindow();
        try
        {
            var tester = window.Tester;
            var neutral = PadReading.FromPhysical(LiveState.Neutral, ExtraButtons.None);
            var held = PadReading.FromPhysical(LiveState.Neutral with { Lt = 173, Ly = 0, Buttons = 0x1000 }, ExtraButtons.PL);
            var waiting = Session(DeviceLayout.Bluetooth85HF, "default") with
            {
                WaitingForNeutral = true, Received = 17, Output = new(0, 0, 0, 0, 0, 0, 0, 0), DevicePath = "85hf-test-interface"
            };
            void Render(EngineFrame? next)
            {
                UpdateSession(window, next);
                Draw(window);
            }
            static string KeyStatus(Panel keys, string id) => AutomationProperties.GetItemStatus(
                keys.Children.OfType<Border>().Select(chip => chip.Child)
                    .Single(caption => AutomationProperties.GetAutomationId(caption) == id));

            Render(waiting with { Connected = false });
            Expect(window.ActivityText.Text == "Waiting for controller" && window.PhysicalStatus.Text == "Bluetooth · Disconnected",
                "A disconnected physical device needs a waiting header and an explicit connection status.");
            Expect(window.VirtualStatus.Text == "XInput slot 0 · Connected" && window.PowerButton.IsEnabled &&
                Equals(window.PowerButton.Content, "Stop conversion"), "A Bluetooth disconnect must keep the virtual slot and stop action visible.");
            Expect(tester.ExtraSection.Visibility == Visibility.Visible && tester.FindName("RearUnavailable") == null &&
                KeyStatus(tester.RearPanel, "PL") == "No reading", "Known extra buttons must remain quiet and accessible without a reading.");
            Expect(window.PhysicalGuidance.Visibility == Visibility.Hidden, "The disconnected device must not prompt to release controls.");

            Render(waiting);
            ExpectRunning(window);
            Expect(window.PhysicalStatus.Text == "Bluetooth · Connected" && window.PhysicalGuidance.Visibility == Visibility.Hidden &&
                tester.SourceStatus.Text == "Move a stick or press and release a button.",
                "Reconnect without a fresh report needs the first-report hint and no release guidance.");

            Render(waiting with { Physical = held });
            Expect(window.PhysicalStatus.Text == "Bluetooth · Connected" && window.PhysicalGuidance.Visibility == Visibility.Visible &&
                window.PhysicalGuidance.Text == "Release all controls to continue.",
                "A held report needs explicit release guidance without changing the connected status.");
            Expect(tester.LtValue.Text == "173 / 255" && tester.LeftValues.Text.Contains("Y +1.00", StringComparison.Ordinal) &&
                KeyStatus(tester.ButtonsPanel, "A") == "Pressed" && KeyStatus(tester.RearPanel, "PL") == "Pressed",
                "Physical input must still show the actual controls held during the release gate.");
            tester.ShowXboxOutput();
            Expect(tester.LtValue.Text == "0 / 255" && tester.LeftValues.Text.Contains("Y 0.00", StringComparison.Ordinal) &&
                KeyStatus(tester.ButtonsPanel, "A") == "Released", "The virtual output must stay neutral while the gate is active.");

            Render(waiting with { WaitingForNeutral = false, Physical = neutral });
            Expect(window.PhysicalGuidance.Visibility == Visibility.Hidden && KeyStatus(tester.RearPanel, "PL") == "Released",
                "The release guidance must clear when the gate opens and physical extras must update.");
            Render(waiting with { WaitingForNeutral = false, Physical = held, Output = held with { Extras = 0 } });
            Expect(tester.LtValue.Text == "173 / 255" && KeyStatus(tester.ButtonsPanel, "A") == "Pressed",
                "Fresh input must appear in the virtual view after the gate opens.");

            Render(waiting with { Connected = false, Suspended = true });
            Expect(window.ActivityText.Text == "Suspended" && window.PhysicalGuidance.Visibility == Visibility.Hidden,
                "Sleep must take priority over the disconnect and release guidance.");
            Render(null);
            Expect(window.ActivityText.Text == "Stopped", "An ended session must show that conversion is stopped.");
        }
        finally { window.Close(); }
    }

    private static void TesterSourceAfterReconnect()
    {
        var window = new MainWindow();
        try
        {
            UpdateSession(window, null);
            var layout = DeviceLayout.Bluetooth85HF;
            var device = new DeviceChoice("tester-fixture", layout.ShortName, layout.Id, layout);
            var tester = window.Tester;
            foreach (bool xbox in new[] { false, true })
            {
                if (xbox) tester.ShowXboxOutput();
                window.ControllersTab.IsSelected = true;
                var wizard = new CalibrationWizard(() => [], _ => throw new InvalidOperationException("No calibration device."));
                try
                {
                    Expect(wizard.ReviewTester.PhysicalSwitch.IsChecked == true &&
                        wizard.ReviewTester.OutputSwitch.IsChecked == false, "Calibration must review physical input.");
                }
                finally { wizard.Close(); }
                window.ConverterTab.IsSelected = true;

                foreach (bool connected in new[] { true, false, true })
                {
                    SetDevices(window, connected ? [device] : []);
                    Draw(window);
                    Expect(tester.PhysicalSwitch.IsChecked == !xbox && tester.OutputSwitch.IsChecked == xbox,
                        "The Converter tester must retain its source through calibration and reconnect.");
                    string status = xbox ? "Start conversion to test the virtual Xbox controller."
                        : connected ? "Move a stick or press and release a button." : $"Waiting for the {layout.ShortName}.";
                    Expect(tester.SourceStatus.Text == status, "The tester must describe the selected source without a new reading.");
                }
            }
        }
        finally { window.Close(); }
    }

    private static void ConverterGeometry(DeviceLayout layout, bool hasExtras)
    {
        var window = new MainWindow();
        try
        {
            const string path = "geometry-test-interface";
            SetDevices(window, [new(path, layout.ShortName, layout.Id, layout)]);
            var root = (FrameworkElement)window.Content;
            var neutral = PadReading.FromPhysical(LiveState.Neutral, ExtraButtons.None);
            var connected = Session(layout, layout.DefaultProfileName) with { DevicePath = path, Physical = neutral, Output = neutral };
            var holding = connected with
            {
                WaitingForNeutral = true, Physical = neutral with { Lx = -1 }
            };
            var states = new (string Label, EngineFrame? Frame)[]
            {
                ("connected", connected),
                ("release guidance", holding),
                ("disconnected", connected with { Connected = false, WaitingForNeutral = true, Physical = null }),
                ("just reconnected", connected with { WaitingForNeutral = true, Physical = null }),
                ("stopped", null)
            };
            foreach (var viewport in new[] { new Size(1014, 558), new Size(812, 472) })
            {
                (double Top, double Bottom, double PowerTop)? reference = null;
                foreach (var (label, frame) in states)
                {
                    UpdateSession(window, frame);
                    Draw(window);
                    root.Measure(viewport);
                    root.Arrange(new Rect(viewport));
                    root.UpdateLayout();

                    var device = window.DeviceCard;
                    var buttons = window.Tester.ButtonsCard;
                    var power = window.PowerButton;
                    string context = $"{layout.DisplayName}, {viewport.Width} DIP, {label}";
                    Expect(root.IsAncestorOf(device) && root.IsAncestorOf(buttons) && device.ActualHeight > 0 && buttons.ActualHeight > 0,
                        $"Converter cards were not laid out ({context}).");
                    Expect(window.Tester.ExtraSection.Visibility == (hasExtras ? Visibility.Visible : Visibility.Collapsed),
                        $"Unexpected extra-button section visibility ({context}).");
                    double Top(FrameworkElement element) => element.TranslatePoint(new Point(), root).Y;
                    double Bottom(FrameworkElement element) => Top(element) + element.ActualHeight;
                    var geometry = (Top: Top(device), Bottom: Bottom(device), PowerTop: Top(power));
                    double powerInset = device.Padding.Bottom + device.BorderThickness.Bottom;
                    Near(geometry.Bottom, Bottom(buttons), $"The card bottoms must align ({context}).");
                    Near(Bottom(power) + powerInset, geometry.Bottom, $"The conversion button must stay at the bottom of the device card ({context}).");
                    if (reference is { } original)
                    {
                        Near(geometry.Top, original.Top, $"The device card moved ({context}).");
                        Near(geometry.Bottom, original.Bottom, $"The device card changed height ({context}).");
                        Near(geometry.PowerTop, original.PowerTop, $"The conversion button moved ({context}).");
                    }
                    reference ??= geometry;
                }
            }
        }
        finally { window.Close(); }
    }

    private static void Near(double actual, double expected, string problem) =>
        Expect(Math.Abs(actual - expected) <= 1, $"{problem} Expected {expected:F1} DIP, got {actual:F1} DIP.");

    private static void SingleLineStatusBar()
    {
        var setContext = typeof(MainWindow).GetMethod("SetStatusContext", PrivateInstance)!
            .CreateDelegate<Action<MainWindow, string?, bool>>();
        var showError = typeof(MainWindow).GetMethod("ShowError", PrivateInstance)!
            .CreateDelegate<Action<MainWindow, string?, string?>>();
        var showWarning = typeof(MainWindow).GetMethod("ShowWarning", PrivateInstance)!
            .CreateDelegate<Action<MainWindow, string>>();
        var showSuccess = typeof(MainWindow).GetMethod("ShowSuccess", PrivateInstance)!
            .CreateDelegate<Action<MainWindow, string, int>>();
        var window = new MainWindow();
        try
        {
            var root = (FrameworkElement)window.Content;
            void CheckStatus(string message, bool hasDetails = false)
            {
                Expect(window.StatusMessage.Text == message, $"Expected the status message '{message}'.");
                Expect(window.CopyStatusDetailsButton.Visibility == (hasDetails ? Visibility.Visible : Visibility.Collapsed),
                    "Copy details is only available when an error has diagnostic text.");
                if (message.Length > 0)
                    Expect(window.StatusIcon.Visibility == Visibility.Visible && window.StatusIcon.Data != null,
                        "Every visible message must have a severity icon.");
                foreach (var viewport in new[] { new Size(812, 472), new Size(1014, 564) })
                {
                    root.Measure(viewport);
                    root.Arrange(new Rect(viewport));
                    root.UpdateLayout();
                    Near(window.StatusBar.ActualHeight, 30, "The status bar must always reserve 30 DIP.");
                }
            }

            const string guidance = "Connect your 85HF via Bluetooth.";
            const string saved = "Profile saved.";
            const string warning = "Windows approval was canceled. Start conversion again.";
            const string failure = "Couldn't save settings. File is in use.";
            const string reconnect = "Reconnect the controller to resume.";

            CheckStatus("");
            setContext(window, guidance, false);
            CheckStatus(guidance);
            showSuccess(window, saved, 60_000);
            CheckStatus(saved);
            setContext(window, reconnect, true);
            CheckStatus(reconnect);
            showWarning(window, warning);
            CheckStatus(warning);

            string detail = "Unable to replace the file.\r\n" + new string('X', 300);
            showError(window, failure, detail);
            CheckStatus(failure, hasDetails: true);
            Expect(window.StatusTooltipText.Text.Contains(detail, StringComparison.Ordinal) &&
                window.CopyStatusDetailsLabel.Text == "Copy details" && window.CopyStatusDetailsButton.Focusable,
                "A detailed error retains its full diagnostic text and offers a focusable copy action.");
            Expect(window.StatusMessage.TextWrapping == TextWrapping.NoWrap &&
                window.StatusMessage.TextTrimming == TextTrimming.CharacterEllipsis &&
                AutomationProperties.GetLiveSetting(window.StatusMessage) == AutomationLiveSetting.Assertive,
                "The error summary must be single-line, visually trimmed and assertive.");

            showError(window, "Couldn't start conversion.\r\nTry again.", null);
            CheckStatus("Couldn't start conversion. Try again.");
            showError(window, null, null);
            CheckStatus(reconnect);
            setContext(window, guidance, false);
            CheckStatus(saved);
            showSuccess(window, saved, 0);
            CheckStatus(guidance);
            Expect(AutomationProperties.GetLiveSetting(window.StatusMessage) == AutomationLiveSetting.Polite,
                "Informational messages must restore polite announcements.");
            setContext(window, null, false);
            CheckStatus("");
        }
        finally { window.Close(); }
    }

    private static void KeyboardFocusStyles()
    {
        var window = new MainWindow();
        try
        {
            var layout = DeviceLayout.Bluetooth85HF;
            SetDevices(window, [new("focus-test-interface", layout.ShortName, layout.Id, layout)]);
            UpdateSession(window, null);
            Draw(window);
            var root = (FrameworkElement)window.Content;
            var viewport = new Size(1014, 558);
            root.Measure(viewport);
            root.Arrange(new Rect(viewport));
            root.UpdateLayout();

            Expect(window.PowerButton.IsEnabled && Equals(window.PowerButton.Content, "Start conversion") &&
                ReferenceEquals(window.PowerButton.Background, window.FindResource("Accent")),
                "The focus fixture must show the enabled mint conversion action.");
            Control[] controls = [window.ConverterTab, window.ControllersTab, window.SettingsTab, window.PowerButton,
                window.CalibrateButton, window.ProfilePicker, window.Tester.PhysicalSwitch, window.Tester.OutputSwitch];
            foreach (var control in controls)
            {
                var focus = (Style)control.FindResource("KeyboardFocus");
                Expect(ReferenceEquals(control.FocusVisualStyle, focus) && focus.TargetType == typeof(Control) && control.Focusable,
                    $"{control.Name} needs the shared native focus adorner and must accept keyboard focus.");
                Expect(!control.Template.Triggers.Cast<TriggerBase>().Any(HasUnconditionalFocus),
                    $"{control.Name} must not paint its native keyboard focus again in the control template.");
            }

            var focusStyle = window.PowerButton.FocusVisualStyle;
            var focusTemplate = (ControlTemplate)focusStyle.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
            var outside = (Border)focusTemplate.LoadContent();
            var inside = (Border)outside.Child;
            Expect(!outside.IsHitTestVisible && outside.BorderThickness.Left >= 1 && inside.BorderThickness.Left >= 1 &&
                ((SolidColorBrush)outside.BorderBrush).Color == ((SolidColorBrush)window.FindResource("PrimaryText")).Color &&
                ((SolidColorBrush)inside.BorderBrush).Color == ((SolidColorBrush)window.FindResource("StartInk")).Color,
                "The native adorner needs contrasting strokes for both dark and mint controls without intercepting clicks.");
        }
        finally { window.Close(); }

        static bool IsFocus(DependencyProperty property) =>
            property == UIElement.IsKeyboardFocusedProperty || property == UIElement.IsKeyboardFocusWithinProperty;
        static bool HasUnconditionalFocus(TriggerBase trigger) => trigger switch
        {
            Trigger single => IsFocus(single.Property),
            MultiTrigger combined => combined.Conditions.Any(c => IsFocus(c.Property)),
            _ => false
        };
    }

    private static void ExpectRunning(MainWindow window) => Expect(window.ActivityText.Text == "Running" &&
        window.PowerButton.IsEnabled && Equals(window.PowerButton.Content, "Stop conversion"),
        "A valid live frame must keep the stop control available.");

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
