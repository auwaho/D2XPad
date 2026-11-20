using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using D2XPad.Capture;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class Program
{
    private sealed class Reports : ICalibrationInput
    {
        private byte[] latest = [7, 128, 0, 0, 0];
        private long version = 1;
        public bool Closed { get; private set; }
        public void Send(byte x = 128, byte lt = 0, byte extra = 0, byte metadata = 255)
        {
            latest = [7, x, lt, extra, metadata];
            version++;
        }
        public (long Version, byte[] Report) Read() => (version, latest);
        public Task<IReadOnlyList<byte[]>> ReadRestSamplesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<byte[]>>([latest, [.. latest[..4], 1], [.. latest[..4], 2]]);
        }
        public void Dispose() => Closed = true;
    }

    [STAThread]
    public static void Main()
    {
        string dir = Directory.CreateTempSubdirectory("D2XPad-wizard-check-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", Path.Combine(dir, "layouts"));
            Environment.SetEnvironmentVariable("D2XPAD_PROFILE_DIR", Path.Combine(dir, "profiles"));
            string settings = Path.Combine(dir, "desktop.json");
            Environment.SetEnvironmentVariable("D2XPAD_DESKTOP_SETTINGS", settings);
            CheckPreferences(settings);
            Run();
            AssignmentDraftChecks.Run();
            ProfileSelectionChecks.Run(dir);
            MappingLayoutChecks.Run();
            DesktopReliabilityChecks.Run(settings, Path.Combine(dir, "layouts"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void Run()
    {
        var source = new Reports();
        var collection = new HidCollection(1, "test-gamepad", 0xDE01, 0xBE01, 1, 5, "USB", "test-container", 5, [7],
            [new(7, 1, 0x30, 8, 8, 0, 255), new(7, 1, 0x32, 16, 8, 0, 255)]);
        var window = new CalibrationWizard(() => [collection], _ => source)
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        Exception? failure = null;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                window.DeviceName.Text = "Fixture controller";
                Click(window.NextButton);
                Click(window.NextButton);
                Expect(!window.NextButton.IsEnabled, "A second neutral sample must come from a fresh report.");
                source.Send();
                window.UpdateSignal();
                Click(window.NextButton);
                void Pose(byte x = 128, byte lt = 0, byte extra = 0)
                {
                    source.Send();
                    window.UpdateSignal();
                    source.Send(x, lt, extra);
                    window.UpdateSignal();
                    Click(window.CaptureButton);
                    source.Send(x, lt, extra, metadata: 170);
                    window.UpdateSignal();
                    Expect(!window.CaptureButton.IsEnabled, "A held pose must be released before recording again.");
                }
                Pose(x: 0); Pose(x: 255); Pose(x: 0); Pose(x: 255);
                window.ControlPicker.SelectedItem = window.ControlPicker.Items.Cast<CalibrationStep>().Single(s => s.Source == "LT");
                Pose(lt: 106); Pose(lt: 255); Pose(lt: 117); Pose(lt: 255);
                Expect(!window.ControlPicker.Items.Cast<CalibrationStep>().Any(s => s.Kind == CaptureKind.Extra),
                    "A new calibration must start without extra buttons.");
                Click(window.NextButton);
                Expect(window.ReviewPage.Visibility == Visibility.Visible, "A layout without extra buttons must pass review.");
                Click(window.BackButton);
                window.ExtraName.Text = "A";
                Click(window.AddExtraButton);
                Expect(window.Problem.Visibility == Visibility.Visible, "Reserved extra name was accepted.");
                window.ExtraName.Text = "P1";
                Click(window.AddExtraButton);
                Pose(extra: 1); Pose(extra: 1);
                Click(window.NextButton);
                Expect(window.ReviewPage.Visibility == Visibility.Visible, "The tester did not open.");
                source.Send(x: 0, lt: 125, extra: 1);
                window.UpdateSignal();
                Click(window.BackButton);
                Click(window.NextButton);
                Click(window.NextButton);
                Expect(window.SavedLayout != null, $"Save failed: {window.Problem.Text}");
            }
            catch (Exception ex) { failure = ex; window.Close(); }
        });
        bool? saved = window.ShowDialog();
        if (failure != null) throw new InvalidOperationException("Wizard interaction failed.", failure);
        Expect(saved == true && window.SavedLayout != null && source.Closed, "Save must close the wizard and release its reader.");
        var layout = PhysicalSession.LayoutFor(collection);
        Expect(layout?.DisplayName == "Fixture controller", "A fresh lookup must identify the saved device.");
        var match = new ProfileCatalog().OpenEditor(layout!.DefaultProfileName);
        Expect(!match.CanReplace && match.Profile.Bindings.Sources.Any(s => s.Name == "P1") &&
            layout.ExtraSources.Select(s => s.Name).SequenceEqual(["P1"]),
            "The saved layout needs a protected default profile and only the extra added by the user.");
        Expect(match.Profile.Bindings.TryDecode([7, 0, 125, 1, 255], out var state, out var extras) &&
            state.Lx == 0 && state.Lt == 125 && extras != 0, "Reloaded controls must preserve the recorded values.");
        Console.WriteLine("WPF wizard passed resting telemetry, release gating, repeated controls, review, save and runtime reload.");
        Recalibrate(collection, LayoutStore.Library().Single(e => e.Layout.Id == layout.Id));
    }

    private static void Recalibrate(HidCollection collection, ControllerEntry original)
    {
        var source = new Reports();
        source.Send(x: 140);
        var window = new CalibrationWizard(() => [collection], _ => source, original)
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        Exception? failure = null;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                Expect(window.DeviceName.Text == original.Layout.DisplayName, "Recalibration lost the saved name.");
                Click(window.NextButton);
                Click(window.NextButton);
                source.Send(x: 140);
                window.UpdateSignal();
                Click(window.NextButton);
                var paddle = window.ControlPicker.Items.Cast<CalibrationStep>().Single(s => s.Source == "P1");
                window.ControlPicker.SelectedItem = paddle;
                Expect(!window.RemoveExtraButton.IsEnabled, "Recalibration must preserve the saved extra identity.");
                window.ControlPicker.SelectedItem = window.ControlPicker.Items.Cast<CalibrationStep>().Single(s => s.Source == "LX");
                foreach (byte position in new byte[] { 10, 240, 10, 240 })
                {
                    source.Send(x: 140);
                    window.UpdateSignal();
                    source.Send(x: position);
                    window.UpdateSignal();
                    Click(window.CaptureButton);
                }
                Click(window.NextButton);
                Expect(window.ReviewPage.Visibility == Visibility.Visible, $"Recalibration preview failed: {window.Problem.Text}");
                Click(window.NextButton);
                Expect(window.SavedLayout != null, $"Recalibration failed to save: {window.Problem.Text}");
            }
            catch (Exception ex) { failure = ex; window.Close(); }
        });
        bool? saved = window.ShowDialog();
        if (failure != null) throw new InvalidOperationException("Recalibration interaction failed.", failure);
        var recalibrated = window.SavedLayout;
        Expect(saved == true && recalibrated?.Id == original.Layout.Id && source.Closed, "Recalibration changed the identity or kept the reader.");
        Expect(recalibrated!.TryDecodePhysical([7, 140, 125, 1, 255], out var state, out var extras) &&
            state.Lx == 128 && state.Lt == 125 && extras != 0, "Unrecorded controls must keep their calibration.");
        Console.WriteLine("WPF recalibration passed name retention, saved extra protection, preview, save and compatible decode.");
    }

    private static void CheckPreferences(string path)
    {
        File.WriteAllText(path, """{"Profile":"legacy"}""");
        var preferences = new DesktopPreferences();
        Expect(preferences.ReadProfile() == "legacy" && preferences.RememberProfile && !preferences.StartMinimized,
            "Legacy desktop settings must keep their previous defaults.");
        preferences.SaveDevice("known-path", "first");
        preferences.SaveProfile("profile-a", "first");
        preferences.SaveProfile("profile-b", "second");
        preferences.SaveOptions(startMinimized: true, rememberProfile: false);
        var reopened = new DesktopPreferences();
        Expect(reopened.StartMinimized && reopened.ReadProfile("first") == null && reopened.DevicePath == "known-path",
            "Reopened settings must retain launch behavior and ignore remembered profiles when disabled.");
        reopened.SaveOptions(startMinimized: true, rememberProfile: true);
        Expect(reopened.ReadProfile("first") == "profile-a" && reopened.ReadProfile("second") == "profile-b",
            "Remembered profiles must be selected per layout.");
    }

    private static void Click(Button button)
    {
        Expect(button.IsEnabled && button.Visibility == Visibility.Visible, $"Disabled or hidden button: {button.Name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
