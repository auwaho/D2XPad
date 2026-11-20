using System.Windows;
using System.Windows.Controls;
using D2XPad.Live;

namespace D2XPad.Desktop;

public partial class MainWindow
{
    private sealed record ControllerRow(ControllerEntry Entry, string Status)
    {
        public string Name => Entry.Layout.DisplayName;
    }

    private IReadOnlyList<ControllerEntry> controllerLibrary = [];
    private ControllerRow[] controllerRows = [];
    private DeviceChoice[] controllerInstances = [];
    private bool updatingControllers;
    private ControllerEntry? SelectedController => (ControllerList.SelectedItem as ControllerRow)?.Entry;
    private DeviceChoice? SelectedInstance => ControllerInstancePicker.SelectedItem as DeviceChoice;
    private bool LibraryEditable => statusKnown && frame == null && !busy && !exiting && !calibrating;

    private void WorkspaceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || exiting || !ReferenceEquals(e.Source, WorkspaceTabs)) return;
        if (WorkspaceTabs.SelectedItem == ControllersTab)
        {
            try { RefreshControllerLibrary(); }
            catch (Exception ex) { ShowError(ex.Message); }
            nextDevices = 0;
        }
        Draw();
    }

    private void RefreshControllerLibrary(string? selectedId = null)
    {
        controllerLibrary = LayoutStore.Library();
        UpdateControllerWorkspace(selectedId);
    }

    private void UpdateControllerWorkspace(string? selectedId = null)
    {
        if (WorkspaceTabs.SelectedItem == SettingsTab)
            SettingsController.Text = DeviceTitle.Text;
        if (WorkspaceTabs.SelectedItem != ControllersTab) return;

        string currentLayout = frame?.LayoutId ?? (DevicePicker.SelectedItem as DeviceChoice)?.LayoutId ?? layout.Id;
        string? target = selectedId ?? SelectedController?.Layout.Id ?? currentLayout;
        var rows = controllerLibrary.Select(entry =>
        {
            string state = entry.IsArchived ? "Archived"
                : frame?.LayoutId == entry.Layout.Id && frame.Connected ? "In use"
                : devices.Any(d => d.LayoutId == entry.Layout.Id) ? "Connected" : "Offline";
            return new ControllerRow(entry, $"{entry.Layout.Transport} · {state}");
        }).ToArray();
        updatingControllers = true;
        try
        {
            if (!controllerRows.SequenceEqual(rows))
            {
                controllerRows = rows;
                ControllerList.ItemsSource = rows;
            }
            ControllerList.SelectedItem = controllerRows.FirstOrDefault(row => row.Entry.Layout.Id == target)
                ?? controllerRows.FirstOrDefault();
        }
        finally { updatingControllers = false; }
        UpdateControllerDetails();
    }

    private void ControllerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !updatingControllers && !exiting) UpdateControllerDetails();
    }

    private void UpdateControllerDetails()
    {
        var entry = SelectedController;
        var deviceLayout = entry?.Layout;
        var instances = entry is { IsArchived: false }
            ? devices.Where(d => d.LayoutId == entry.Layout.Id).ToArray() : [];
        if (!controllerInstances.SequenceEqual(instances))
        {
            var prior = SelectedInstance?.Path ?? (DevicePicker.SelectedItem as DeviceChoice)?.Path;
            controllerInstances = instances;
            ControllerInstancePicker.ItemsSource = instances;
            ControllerInstancePicker.SelectedItem = instances.FirstOrDefault(d => d.Path == prior) ?? instances.FirstOrDefault();
        }
        if (frame?.LayoutId == deviceLayout?.Id && frame?.DevicePath is { } runningPath &&
            !string.Equals(SelectedInstance?.Path, runningPath, StringComparison.OrdinalIgnoreCase))
            ControllerInstancePicker.SelectedItem = instances.FirstOrDefault(d =>
                string.Equals(d.Path, runningPath, StringComparison.OrdinalIgnoreCase));
        ControllerInstancePicker.Visibility = instances.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ControllerInstancePicker.IsEnabled = LibraryEditable;
        CalibrationOrigin.Text = entry switch
        {
            null => "",
            { IsArchived: true } => "Archived calibration",
            { IsBuiltIn: true, IsCustomized: true } => "Custom calibration",
            { IsBuiltIn: true } => "Default calibration",
            _ => "Saved calibration"
        };
        UseControllerButton.Visibility = RecalibrateButton.Visibility = entry is { IsArchived: false }
            ? Visibility.Visible : Visibility.Collapsed;
        UseControllerButton.IsEnabled = RecalibrateButton.IsEnabled = LibraryEditable && instances.Length > 0;
        RestoreCalibrationButton.Visibility = entry is { IsArchived: true } or { IsBuiltIn: true, IsCustomized: true }
            ? Visibility.Visible : Visibility.Collapsed;
        RestoreCalibrationButton.Content = entry?.IsArchived == true ? "Restore controller" : "Restore default";
        RestoreCalibrationButton.IsEnabled = LibraryEditable;
        ForgetButton.Visibility = entry is { IsBuiltIn: false, IsArchived: false } ? Visibility.Visible : Visibility.Collapsed;
        ForgetButton.IsEnabled = LibraryEditable;
        CalibrationHelp.Text = entry == null ? "Choose a saved controller or add one."
            : frame != null ? "Stop conversion to change the controller or calibration."
            : !statusKnown ? "Checking converter status…"
            : entry.IsArchived ? "Restore this controller to use its saved profiles."
            : instances.Length == 0 ? "Connect this controller to recalibrate."
            : entry.IsBuiltIn ? "You can adjust the sticks and triggers and restore the built-in calibration at any time." : "";
        CalibrationHelp.Visibility = CalibrationHelp.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool supported = entry is { IsArchived: false } && DesktopPreferences.SupportsVibration(deviceLayout?.Id);
        var active = supported && frame?.LayoutId == deviceLayout!.Id ? frame : null;
        bool enabled = supported && (active?.VibrationEnabled ?? preferences.VibrationEnabled(deviceLayout!.Id));
        GameVibration.Visibility = TestVibrationButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        GameVibration.IsEnabled = supported && statusKnown && !busy && !calibrating && !exiting;
        GameVibration.IsChecked = enabled;
        VibrationStatus.Text = !supported ? "Vibration is unavailable for this controller and connection."
            : active?.VibrationProblem is { Length: > 0 } problem ? problem
            : active != null ? "Stop conversion to run a motor test."
            : instances.Length == 0 ? "Connect this controller to test vibration."
            : frame != null ? "This setting applies when you start conversion with this controller."
            : "Send a short motor test.";
        TestVibrationButton.IsEnabled = supported && LibraryEditable && instances.Length > 0;
    }

    private async void GameVibrationChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || !GameVibration.IsEnabled || busy || exiting ||
            SelectedController is not { IsArchived: false } entry || !DesktopPreferences.SupportsVibration(entry.Layout.Id)) return;

        string id = entry.Layout.Id;
        bool enabled = GameVibration.IsChecked == true;
        var active = frame?.LayoutId == id ? frame : null;
        if (enabled == (active?.VibrationEnabled ?? preferences.VibrationEnabled(id))) return;

        await RunAction("Updating vibration…", async () =>
        {
            if (active != null)
            {
                await ReadSession();
                if (frame?.SessionId != active.SessionId || frame.LayoutId != id)
                    throw new InvalidOperationException("The active controller changed. Select it and try again.");
                await engine.SetVibrationAsync(active.SessionId, enabled, lifetime.Token);
            }
            preferences.SaveVibration(id, enabled);
        });
    }

    private async void UseController(object sender, RoutedEventArgs e)
    {
        if (!LibraryEditable || SelectedInstance is not { } device) return;
        await RunAction("Selecting controller…", async () =>
        {
            await ReadSession();
            if (frame != null) throw new InvalidOperationException("Stop the converter before switching controllers.");
            SetDevices(await engine.DevicesAsync(lifetime.Token));
            var current = devices.FirstOrDefault(d => string.Equals(d.Path, device.Path, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("The selected controller disconnected.");
            syncing = true;
            try { DevicePicker.SelectedItem = current; }
            finally { syncing = false; }
            SelectDeviceProfile(current);
            WorkspaceTabs.SelectedItem = ConverterTab;
        });
    }

    private async void RecalibrateController(object sender, RoutedEventArgs e)
    {
        if (!LibraryEditable || SelectedController is not { IsArchived: false } entry || SelectedInstance is not { } device) return;
        await OpenCalibration(entry, device.Path);
    }

    private async void TestVibration(object sender, RoutedEventArgs e)
    {
        if (!LibraryEditable || SelectedInstance is not { } device || device.LayoutId != DeviceLayout.Bluetooth85HF.Id) return;
        await RunAction("Testing vibration…", async () =>
        {
            await Task.Run(() => RumbleSurvey.PulseAsync(lifetime.Token, device.Path), lifetime.Token);
            savedMessage = "The Bluetooth test pulse and stop command were sent.";
            savedMessageUntil = Environment.TickCount64 + 5000;
        });
    }

    private async void RestoreCalibration(object sender, RoutedEventArgs e)
    {
        if (!LibraryEditable || SelectedController is not { } entry) return;
        string prompt = entry.IsArchived ? $"Restore {entry.Layout.DisplayName} and make its saved profiles available again?"
            : $"Restore the built-in calibration for {entry.Layout.DisplayName}? A copy of the current calibration will be kept.";
        if (MessageBox.Show(this, prompt, "Restore calibration", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ChangeSavedController(entry, entry.IsArchived ? LayoutStore.RestoreArchived : LayoutStore.RestoreDefault, "Restoring controller…");
    }

    private async void ForgetController(object sender, RoutedEventArgs e)
    {
        if (!LibraryEditable || SelectedController is not { IsBuiltIn: false, IsArchived: false } entry) return;
        var answer = MessageBox.Show(this,
            $"Archive {entry.Layout.DisplayName}? Its profiles will be available again when you restore it.",
            "Forget controller", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await ChangeSavedController(entry, LayoutStore.Forget, "Archiving controller…");
    }

    private Task ChangeSavedController(ControllerEntry entry, Action<ControllerEntry> mutate, string message) =>
        RunAction(message, async () =>
        {
            ReleasePreview();
            using (EngineClient.ReserveStoppedConverter()) mutate(entry);
            SetDevices(await engine.DevicesAsync(lifetime.Token));
            if (DevicePicker.SelectedItem is DeviceChoice device && device.LayoutId != layout.Id) SelectDeviceProfile(device);
            else
            {
                var activeLayout = DeviceLayout.Supported.FirstOrDefault(d => d.Id == layout.Id) ?? DeviceLayout.Resolve(DeviceLayout.Bluetooth85HF.Id);
                LoadProfiles(activeLayout.Id == layout.Id ? chosenProfile : null, activeLayout);
            }
            RefreshControllerLibrary(entry.Layout.Id);
            savedMessage = $"Updated {entry.Layout.DisplayName}.";
            savedMessageUntil = Environment.TickCount64 + 5000;
        });

    private void PreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || exiting) return;
        try
        {
            preferences.SaveOptions(StartMinimized.IsChecked == true, RememberProfile.IsChecked == true);
            if (preferences.RememberProfile && chosenProfile != null) preferences.SaveProfile(chosenProfile, layout.Id);
        }
        catch (Exception ex)
        {
            StartMinimized.IsChecked = preferences.StartMinimized;
            RememberProfile.IsChecked = preferences.RememberProfile;
            ShowError(ex.Message);
        }
    }

    private void GoToControllers(object sender, RoutedEventArgs e)
    {
        WorkspaceTabs.SelectedItem = ControllersTab;
        UpdateControllerWorkspace(frame?.LayoutId ?? (DevicePicker.SelectedItem as DeviceChoice)?.LayoutId ?? layout.Id);
        ControllersTab.Focus();
    }
}
