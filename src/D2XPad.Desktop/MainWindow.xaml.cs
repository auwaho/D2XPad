using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using D2XPad.Live;
using Microsoft.Win32;

namespace D2XPad.Desktop;

public partial class MainWindow : Window
{
    private sealed record ProfileChoice(string Name, string Caption)
    {
        public override string ToString() => Caption;
    }
    private readonly EngineClient engine = new();
    private readonly ProfileCatalog catalog = new();
    private readonly SemaphoreSlim sessionLock = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? starting;
    private readonly DispatcherTimer timer;
    private readonly DesktopPreferences preferences = new();
    private IReadOnlyList<DeviceChoice> devices = [];
    private EngineFrame? frame;
    private long nextStatus, nextDevices, nextPreview;
    private bool syncing, initialized, busy, exiting, statusKnown;
    private StatusNotice? profileProblem, pollProblem;
    private string? previewTarget;
    private string? chosenProfile;
    private ProfileEditSnapshot? selection;
    private DeviceLayout layout = DeviceLayout.Bluetooth85HF;
    private DeviceLayout? sessionLayout;
    private PadBindings? selectedBindings;
    private DeviceLayout? testerLayout;
    private PadBindings? testerBindings;
    private bool calibrating;

    public MainWindow()
    {
        InitializeComponent();
        WindowCaption.UseDark(this);
        KeyboardFocusCues.Track(this);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += Refresh;
        chosenProfile = preferences.ReadProfile(preferences.LayoutId);
        StartMinimized.IsChecked = preferences.StartMinimized;
        StartAutomatically.IsChecked = preferences.StartAutomatically;
        StartWithWindows.Loaded += (_, _) => LoadWindowsStartup();
        Loaded += (_, _) =>
        {
            initialized = true;
            ResumeDisplay();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!initialized || exiting) return;
            if (IsVisible) ResumeDisplay();
            else
            {
                timer.Stop();
                ReleasePreview();
            }
        };
    }

    internal bool ShouldStartMinimized => preferences.StartMinimized;

    private void ResumeDisplay()
    {
        nextDevices = nextStatus = 0;
        try { LoadProfiles(frame?.Profile ?? chosenProfile); }
        catch (Exception ex) { ShowError("Couldn't load profiles.", ex.ToString()); }
        timer.Start();
        Refresh(this, EventArgs.Empty);
    }

    private void LoadProfiles(string? preferred, DeviceLayout? target = null)
    {
        if (target == null)
        {
            string id = frame?.LayoutId ?? (DevicePicker.SelectedItem as DeviceChoice)?.LayoutId ?? preferences.LayoutId ?? layout.Id;
            var display = GetDisplayLayout();
            target = display?.Id == id ? display : DeviceLayout.Supported.FirstOrDefault(candidate => candidate.Id == id);
            if (target == null && frame == null)
                target = DeviceLayout.Resolve(DeviceLayout.Bluetooth85HF.Id);
        }

        List<string> names = target == null ? [] : catalog.NamesForLayout(target).ToList();
        string? active = frame?.Profile;
        if (active != null && !names.Contains(active, StringComparer.OrdinalIgnoreCase)) names.Add(active);
        if (target != null && preferred != null && Path.IsPathFullyQualified(preferred) &&
            !names.Contains(preferred, StringComparer.OrdinalIgnoreCase) && catalog.IsCompatible(preferred, target))
            names.Add(preferred);
        string? Find(string? name) => names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        string? selected = Find(preferred) ?? Find(active) ?? Find(target == null ? null : preferences.ReadProfile(target.Id))
            ?? (preferred == null && target?.Id == DeviceLayout.Bluetooth85HF.Id ? Find("85HF") : null)
            ?? Find(target?.DefaultProfileName) ?? names.FirstOrDefault();
        var choices = names.Select(n => new ProfileChoice(n, target != null && target.Id != DeviceLayout.Bluetooth85HF.Id &&
            n == target.DefaultProfileName ? $"{target.DisplayName} · default" : n)).ToArray();
        syncing = true;
        try
        {
            ProfilePicker.ItemsSource = choices;
            ProfilePicker.SelectedItem = choices.FirstOrDefault(c => c.Name == selected);
            chosenProfile = selected;
        }
        finally { syncing = false; }
        if (target != null) layout = target;
        ShowAssignments(selected);
    }

    private void ShowAssignments(string? profile)
    {
        profileProblem = null;
        selection = null;
        selectedBindings = null;
        if (profile != null)
        {
            try
            {
                selection = catalog.OpenEditor(profile);
                var bindings = selection.Profile.Bindings;
                layout = bindings.Layout;
                selectedBindings = bindings;
            }
            catch (Exception ex) { profileProblem = new("Couldn't load the selected profile.", StatusSeverity.Error, ex.ToString()); }
        }
        DeviceTitle.Text = layout.DisplayName;
        DeleteButton.ToolTip = selection?.CanReplace == true
            ? "Delete the selected profile from the catalog."
            : "Only saved or imported profiles can be deleted here.";
        UpdateTesterLayout(layout);
        UpdateStatusBar();
    }

    private void UpdateTesterLayout(DeviceLayout? deviceLayout)
    {
        var compatible = selectedBindings?.Layout.Id == deviceLayout?.Id ? selectedBindings : null;
        if (testerLayout == deviceLayout && testerBindings == compatible) return;
        testerLayout = deviceLayout;
        testerBindings = compatible;
        Tester.SetProfile(deviceLayout, compatible ?? (deviceLayout == null ? null : PadBindings.Create(deviceLayout, [])));
    }

    private async void Refresh(object? sender, EventArgs e)
    {
        if (exiting || !IsVisible || busy || !sessionLock.Wait(0)) return;
        try
        {
            long now = Environment.TickCount64;
            if (now >= nextStatus)
            {
                await ReadSession();
                nextStatus = now + (frame == null ? 900 : 0);
            }
            if (now >= nextDevices && IsVisible && !exiting)
            {
                nextDevices = now + 1800;
                if (frame?.DevicePath == null || devices.Count == 0 || WorkspaceTabs.SelectedItem == ControllersTab)
                    SetDevices(await engine.DevicesAsync(lifetime.Token));
            }
            if (statusKnown) pollProblem = null;
            if (IsVisible && !exiting) Draw();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            nextStatus = Environment.TickCount64 + 900;
            pollProblem = new("Couldn't refresh controller status.", StatusSeverity.Error, ex.ToString());
            if (IsVisible && !exiting) Draw();
        }
        finally { sessionLock.Release(); }
    }

    private async Task ReadSession()
    {
        EngineFrame? next;
        try { next = await engine.ReadAsync(lifetime.Token); }
        catch
        {
            // Only a session read failure invalidates the displayed ownership state.
            statusKnown = false;
            frame = null;
            throw;
        }
        UpdateSession(next);
        if (engine.TakeRuntimeFailure() is { } failure) ShowError("Conversion stopped unexpectedly.", failure);
    }

    private void UpdateSession(EngineFrame? next)
    {
        var previous = frame;
        frame = next;
        statusKnown = true;
        bool sessionChanged = previous?.SessionId != next?.SessionId;
        if (sessionChanged)
        {
            nextDevices = 0;
            sessionLayout = null;
        }
        if (sessionChanged || next != null && (previous?.ProfileRevision != next.ProfileRevision
            || !string.Equals(previous.Profile, next.Profile, StringComparison.OrdinalIgnoreCase)))
        {
            try { LoadProfiles(next?.Profile ?? chosenProfile); }
            catch (Exception ex) { ShowError("Couldn't load the active profile.", ex.ToString()); }
            if (next != null) TrySavePreference(() => preferences.SaveProfile(next.Profile, next.LayoutId ?? layout.Id));
        }
        if (next != null) sessionLayout ??= GetDisplayLayout();
    }

    private void TrySavePreference(Action save)
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsSaveError(ex);
        }
    }

    private void SetDevices(IReadOnlyList<DeviceChoice> fresh)
    {
        var automaticTarget = preferences.StartAutomatically && frame == null ? RememberedAutomaticTarget : null;
        var prior = DevicePicker.SelectedItem as DeviceChoice;
        var selected = automaticTarget?.Select(fresh);
        if (automaticTarget == null)
        {
            selected = fresh.FirstOrDefault(d => string.Equals(d.Path, frame?.DevicePath ?? prior?.Path ?? preferences.DevicePath,
                StringComparison.OrdinalIgnoreCase));
            if (selected == null && frame == null && preferences.LayoutId is { } remembered)
            {
                var matching = fresh.Where(d => d.LayoutId == remembered).ToArray();
                if (matching.Length == 1) selected = matching[0];
            }
            if (selected == null && fresh.Count == 1) selected = fresh[0];
        }
        if (devices.SequenceEqual(fresh) && Equals(prior, selected)) return;
        devices = fresh;
        syncing = true;
        try
        {
            DevicePicker.ItemsSource = devices;
            DevicePicker.SelectedItem = selected;
            DevicePicker.Visibility = devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { syncing = false; }
        if (!busy && !calibrating && frame == null && selected != null &&
            (selected.LayoutId != layout.Id || !string.Equals(prior?.Path, selected.Path, StringComparison.OrdinalIgnoreCase)))
            SelectDeviceProfile(selected, remember: !preferences.StartAutomatically);
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || !initialized) return;
        if (frame == null && !busy && DevicePicker.SelectedItem is DeviceChoice device)
        {
            try { SelectDeviceProfile(device); }
            catch (Exception ex) { ShowError("Couldn't select the controller.", ex.ToString()); }
        }
        Draw();
    }

    private void SelectDeviceProfile(DeviceChoice device, bool remember = true)
    {
        var target = device.Layout ?? DeviceLayout.Resolve(device.LayoutId);
        if (selectedBindings?.Layout.Id != device.LayoutId)
            LoadProfiles(null, target);
        if (!remember) return;
        TrySavePreference(() =>
        {
            preferences.SaveDevice(device.Path, device.LayoutId, device.ContainerId);
            if (chosenProfile != null) preferences.SaveProfile(chosenProfile, device.LayoutId);
        });
    }

    private async void CalibrateController(object sender, RoutedEventArgs e) => await OpenCalibration();

    private async Task OpenCalibration(ControllerEntry? original = null, string? devicePath = null)
    {
        if (calibrating || busy || exiting) return;
        calibrating = true;
        try
        {
            ReleasePreview();
            var wizard = new CalibrationWizard(original, devicePath) { Owner = this };
            if (wizard.ShowDialog() != true || wizard.SavedLayout is not { } saved) return;
            await RunAction("Loading calibrated controller…", "Couldn't load the calibrated controller.", async () =>
            {
                await ReadSession();
                SetDevices(await engine.DevicesAsync(lifetime.Token));
                if (frame == null)
                {
                    syncing = true;
                    var current = devices.FirstOrDefault(d => string.Equals(d.Path, wizard.SelectedPath, StringComparison.OrdinalIgnoreCase));
                    try { DevicePicker.SelectedItem = current; }
                    finally { syncing = false; }
                    string? chosen = original != null && layout.Id == saved.Id ? chosenProfile : null;
                    LoadProfiles(chosen, saved);
                    if (current != null) preferences.SaveDevice(current.Path, saved.Id, current.ContainerId);
                    if (chosenProfile != null) preferences.SaveProfile(chosenProfile, saved.Id);
                }
                else LoadProfiles(frame.Profile);
                RefreshControllerLibrary(saved.Id);
                ShowSuccess(frame == null ? "Calibration saved." : "Calibration saved. Stop conversion to select it.");
            });
        }
        catch (Exception ex) { ShowError("Couldn't open controller calibration.", ex.ToString()); }
        finally
        {
            calibrating = false;
            nextDevices = nextPreview = 0;
            if (!exiting) Draw();
        }
    }

    private void ReleasePreview()
    {
        engine.SetPreviewDevice(null);
        previewTarget = null;
        nextPreview = 0;
    }

    private DeviceLayout? GetDisplayLayout()
    {
        var selected = DevicePicker.SelectedItem as DeviceChoice;
        string id = frame?.LayoutId ?? selected?.LayoutId ?? layout.Id;
        if (frame != null && sessionLayout?.Id == id) return sessionLayout;
        if (selected?.Layout?.Id == id) return selected.Layout;
        if (layout.Id == id) return layout;
        return id == DeviceLayout.Bluetooth85HF.Id ? DeviceLayout.Bluetooth85HF : null;
    }

    private void Draw()
    {
        bool running = frame != null;
        var device = DevicePicker.SelectedItem as DeviceChoice;
        bool legacy = frame is { DevicePath: null, Connected: true, Suspended: false } && devices.Count == 1;
        var preview = IsVisible && !exiting && !calibrating && WorkspaceTabs.SelectedItem == ConverterTab && (!running || legacy) ? device : null;
        if (!string.Equals(previewTarget, preview?.Path, StringComparison.OrdinalIgnoreCase) || Environment.TickCount64 >= nextPreview)
        {
            previewTarget = preview?.Path;
            nextPreview = Environment.TickCount64 + 1800;
            try { engine.SetPreviewDevice(preview); }
            catch (Exception ex) { ShowError("Couldn't read physical input.", ex.ToString()); }
        }
        bool connected = frame?.Connected ?? (device != null);
        bool suspended = frame?.Suspended == true;
        var deviceLayout = GetDisplayLayout();
        string shortName = deviceLayout?.ShortName ?? "physical controller";
        DeviceTitle.Text = deviceLayout?.DisplayName ?? "Physical controller";
        UpdateTesterLayout(deviceLayout);
        string connection = suspended ? "Suspended"
            : !running && device == null && devices.Count > 0 ? "Select a controller"
            : connected ? "Connected" : "Disconnected";
        PhysicalStatus.Text = deviceLayout == null ? connection : $"{deviceLayout.Transport} · {connection}";
        PhysicalStatus.Foreground = (Brush)FindResource(!connected || suspended ? "Warning" : "SecondaryText");
        PhysicalGuidance.Visibility = frame is { Connected: true, Suspended: false, WaitingForNeutral: true, Physical: not null }
            ? Visibility.Visible : Visibility.Hidden;
        VirtualStatus.Text = frame is { } f ? $"XInput slot {f.Slot} · {(f.Output == null ? "Unavailable" : "Connected")}"
            : statusKnown ? "" : "Checking XInput…";
        VirtualStatus.Visibility = VirtualStatus.Text.Length == 0 ? Visibility.Hidden : Visibility.Visible;
        bool waitingForController = automaticWaiting || running && !connected;
        bool conversionFailed = !running && automaticProblem?.Severity == StatusSeverity.Error;
        ActivityText.Text = !statusKnown ? "Checking session" : suspended ? "Suspended" :
            waitingForController ? "Waiting for controller" : running ? "Running" : conversionFailed ? "Error" : "Stopped";
        bool attention = statusKnown && (suspended || waitingForController);
        ActivityDot.Fill = (Brush)FindResource(attention ? "Warning" : running ? "Accent" : conversionFailed ? "Error" : "Stopped");
        ActivityText.Foreground = (Brush)FindResource(attention ? "Warning" : conversionFailed ? "Error" : "SecondaryText");
        bool conversionRequested = running || automaticWaiting;
        PowerButton.Content = conversionRequested ? "Stop conversion" : "Start conversion";
        PowerButton.Background = conversionRequested ? (Brush)FindResource("StopSurface") : (Brush)FindResource("Accent");
        PowerButton.Foreground = conversionRequested ? Brushes.WhiteSmoke : (Brush)FindResource("StartInk");
        bool compatible = device != null && device.LayoutId == layout.Id && selectedBindings != null;
        bool canWait = preferences.StartAutomatically && RememberedAutomaticTarget != null;
        PowerButton.IsEnabled = !busy && !exiting && statusKnown && (conversionRequested || canWait || compatible && profileProblem == null);
        StartAutomatically.IsEnabled = !busy && !exiting;
        ProfilePicker.IsEnabled = ImportButton.IsEnabled = !busy && !exiting && statusKnown;
        EditButton.IsEnabled = !busy && !exiting && chosenProfile != null && profileProblem == null;
        DeleteButton.IsEnabled = !busy && !exiting && statusKnown && selection?.CanReplace == true;
        DevicePicker.IsEnabled = !busy && !exiting && !running;
        CalibrateButton.IsEnabled = !busy && !exiting && !calibrating;
        bool reconnect = running && waitingForController && !suspended;
        string? guidance = automaticWaiting ? (RememberedAutomaticTarget == null
                ? "Set a controller as input to start automatic conversion."
                : automaticServiceDeadline != null ? "Waiting for the conversion service." : $"Waiting for your {shortName}.")
            : reconnect ? "Reconnect the controller to resume."
            : !running && statusKnown && devices.Count == 0 ? $"Connect your {layout.ShortName} via {layout.Transport}."
            : !running && devices.Count > 1 && device == null ? "Select a controller to start."
            : !running && device != null && device.LayoutId != layout.Id ? "Choose a profile for this controller." : null;
        if (!busy) SetStatusContext(guidance, warning: reconnect);
        var physical = connected && !suspended ? frame?.Physical ?? (preview != null ? engine.Preview : null) : null;
        string missing = suspended ? "Physical input is suspended."
            : !running && device == null && devices.Count > 0 ? "Select a physical controller."
            : !connected ? $"Waiting for the {shortName}."
            : running && frame?.DevicePath == null && devices.Count > 1 ? "Restart the converter to identify its physical device."
            : "Move a stick or press and release a button.";
        Tester.Update(physical, frame?.Output, missing, running ? "Waiting for the XInput slot."
            : automaticWaiting ? "Xbox output starts when the selected controller connects."
            : "Start conversion to test the virtual Xbox controller.");
        UpdateControllerWorkspace();
    }

    private void EditAssignments(object sender, RoutedEventArgs e)
    {
        if (chosenProfile == null || busy || exiting) return;
        try
        {
            var original = catalog.OpenEditor(chosenProfile);
            ProfileEditSnapshot? written = null;
            async Task SaveAndApply(string name, IReadOnlyList<InputMapping> assignments, bool replace)
            {
                bool sameAsOriginal = string.Equals(name, original.Profile.Name, StringComparison.OrdinalIgnoreCase);
                bool retry = written != null && string.Equals(name, written.Profile.Name, StringComparison.OrdinalIgnoreCase);
                if (sameAsOriginal && !replace && !retry)
                    throw new InvalidDataException("Enter a new name to save a copy.");
                await sessionLock.WaitAsync(lifetime.Token);
                busy = true;
                try
                {
                    ShowError(null);
                    var profile = catalog.SaveMappings(retry ? written! : original, name, assignments);
                    written = catalog.OpenEditor(profile.Name);
                    try
                    {
                        var active = await engine.ReadAsync(lifetime.Token);
                        // An edit to the active profile retains its name and still needs a fresh decoder.
                        if (active != null) await engine.SwitchAsync(profile.Name, lifetime.Token);
                        await ReadSession();
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Saved “{profile.Name}”. Could not confirm the live mapping. {ex.Message} You can retry.", ex);
                    }
                    LoadProfiles(frame?.Profile ?? profile.Name);
                    TrySavePreference(() => preferences.SaveProfile(chosenProfile!, layout.Id));
                    if (frame != null) Tester.ShowXboxOutput();
                    ShowSuccess(frame == null ? "Profile saved." : "Profile saved and applied.");
                }
                finally
                {
                    busy = false;
                    sessionLock.Release();
                    if (!exiting) Draw();
                }
            }
            new BindingEditor(original.Profile, original.CanReplace, SaveAndApply) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("Couldn't open input mappings.", ex.ToString()); }
    }

    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || !initialized || ProfilePicker.SelectedItem is not ProfileChoice selected) return;
        await RunAction("Applying profile…", "Couldn't apply the profile.", () => ApplyProfile(selected.Name));
    }

    private async Task ApplyProfile(string name)
    {
        var candidate = catalog.Resolve(name);
        string layoutId = frame?.LayoutId ?? (DevicePicker.SelectedItem as DeviceChoice)?.LayoutId ?? layout.Id;
        if (candidate.Bindings.Layout.Id != layoutId)
            throw new InvalidDataException("Choose a profile for the selected physical controller.");
        if (frame != null && !string.Equals(frame.Profile, name, StringComparison.OrdinalIgnoreCase))
            await engine.SwitchAsync(name, lifetime.Token);
        chosenProfile = name;
        ShowAssignments(name);
        TrySavePreference(() => preferences.SaveProfile(name, layoutId));
    }

    private async void ImportProfile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import controller profile", Filter = "JSON profiles (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAction("Importing profile…", "Couldn't complete the profile import.", () => ImportProfileFrom(dialog.FileName));
    }

    private async Task ImportProfileFrom(string path)
    {
        var imported = catalog.Import(path);
        string layoutId = frame?.LayoutId ?? (DevicePicker.SelectedItem as DeviceChoice)?.LayoutId ?? layout.Id;
        if (imported.Bindings.Layout.Id == layoutId)
        {
            try
            {
                await ApplyProfile(imported.Name);
                LoadProfiles(imported.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Imported “{imported.Name}”. Could not select it. {ex.Message}", ex);
            }
        }
        ShowSuccess("Profile imported.");
    }

    private async void DeleteProfile(object sender, RoutedEventArgs e)
    {
        if (busy || exiting || selection is not { CanReplace: true } selected) return;
        string name = selected.Profile.Name;
        string fallback = selected.Profile.Bindings.Layout.DefaultProfileName;
        var answer = MessageBox.Show(this,
            $"Delete “{name}” from your profiles? A backup will be kept.\n\nIf the profile is in use, the converter will switch to “{fallback}” on the same XInput slot.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await RunAction("Deleting profile…", "Couldn't finish deleting the profile.", async () =>
        {
            string? active = await engine.DeleteProfileAsync(selected, lifetime.Token);
            LoadProfiles(active ?? fallback);
            TrySavePreference(() => preferences.SaveProfile(chosenProfile!, layout.Id));
            ShowSuccess("Profile deleted.");
        });
    }

    private async void ToggleConverter(object sender, RoutedEventArgs e)
    {
        if (busy || exiting) return;
        if (automaticWaiting && frame == null)
        {
            CancelAutomaticConversion();
            Draw();
            return;
        }
        bool stop = frame != null;
        var selected = chosenProfile;
        var device = DevicePicker.SelectedItem as DeviceChoice;
        if (!stop && (selected == null || device == null) && preferences.StartAutomatically && RememberedAutomaticTarget != null)
        {
            ArmAutomaticConversion();
            Draw();
            return;
        }
        CancelAutomaticConversion();
        string progress = stop ? "Stopping conversion…"
            : ConversionService.Status() == ConversionServiceStatus.Missing
                ? "Starting conversion. Approve the Windows prompt if shown."
                : "Starting conversion…";
        await RunAction(progress,
            stop ? "Couldn't stop conversion." : "Couldn't start conversion.", async () =>
        {
            if (stop) await engine.StopAsync(lifetime.Token);
            else if (selected != null && device != null)
                await StartConverterAsync(selected, device, LiveOutputMode.Auto);
        });
    }

    private async Task StartConverterAsync(string profile, DeviceChoice device, LiveOutputMode outputMode)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        starting = attempt;
        try { await engine.StartAsync(profile, device, attempt.Token, preferences.VibrationEnabled(device.LayoutId), outputMode); }
        finally { starting = null; }
        TrySavePreference(() =>
        {
            preferences.SaveDevice(device.Path, device.LayoutId, device.ContainerId);
            preferences.SaveProfile(profile, device.LayoutId);
        });
    }

    private async Task RunAction(string message, string failure, Func<Task> action)
    {
        if (busy || exiting) return;
        busy = true;
        statusSuccess = null;
        pollProblem = null;
        SetStatusIssue(null);
        SetStatusContext(message);
        PowerButton.IsEnabled = ProfilePicker.IsEnabled = DevicePicker.IsEnabled = ImportButton.IsEnabled =
            EditButton.IsEnabled = DeleteButton.IsEnabled = StartAutomatically.IsEnabled = false;
        UpdateControllerWorkspace();
        await sessionLock.WaitAsync();
        try
        {
            if (exiting) return;
            await action();
            await ReadSession();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (DriverApprovalCanceledException)
        {
            ShowWarning("Windows approval was canceled. Start conversion again.");
        }
        catch (Exception ex)
        {
            ShowError(failure, ex.ToString());
            try { await ReadSession(); }
            catch { statusKnown = false; frame = null; }
            try { LoadProfiles(frame?.Profile ?? chosenProfile); }
            catch (Exception listError) { ShowError(failure, $"{ex}{Environment.NewLine}{Environment.NewLine}{listError}"); }
        }
        finally
        {
            busy = false;
            nextStatus = nextDevices = 0;
            sessionLock.Release();
            if (!exiting) Draw();
        }
    }

    internal async Task StopForExitAsync()
    {
        exiting = true;
        CancelAutomaticConversion();
        timer.Stop();
        starting?.Cancel();
        await sessionLock.WaitAsync();
        try
        {
            await engine.StopAsync(CancellationToken.None);
            engine.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
        }
        catch
        {
            exiting = false;
            if (IsVisible) timer.Start();
            throw;
        }
        finally { sessionLock.Release(); }
    }

}
