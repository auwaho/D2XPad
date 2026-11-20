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
    private string? profileProblem, previewTarget, pollProblem;
    private string? chosenProfile;
    private ProfileEditSnapshot? selection;
    private DeviceLayout layout = DeviceLayout.Bluetooth85HF;
    private DeviceLayout? sessionLayout;
    private PadBindings? selectedBindings;
    private DeviceLayout? testerLayout;
    private PadBindings? testerBindings;
    private bool calibrating;
    private string? savedMessage;
    private long savedMessageUntil;

    public MainWindow()
    {
        InitializeComponent();
        WindowCaption.UseDark(this);
        KeyboardFocusCues.Track(this);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += Refresh;
        chosenProfile = preferences.ReadProfile(preferences.LayoutId);
        StartMinimized.IsChecked = preferences.StartMinimized;
        RememberProfile.IsChecked = preferences.RememberProfile;
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
        catch (Exception ex) { ShowError(ex.Message); }
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
            ?? (preferred == null && preferences.RememberProfile && target?.Id == DeviceLayout.Bluetooth85HF.Id ? Find("85HF") : null)
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
            catch (Exception ex) { profileProblem = ex.Message; }
        }
        DeviceTitle.Text = layout.DisplayName;
        DeleteButton.ToolTip = selection?.CanReplace == true
            ? "Delete the selected profile from the catalog."
            : "Only saved or imported profiles can be deleted here.";
        UpdateTesterLayout(layout);
        if (profileProblem != null) ShowError(profileProblem);
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
            if (IsVisible && !exiting) Draw();
            if (statusKnown && pollProblem != null)
            {
                if (ErrorText.Text == pollProblem) ShowError(profileProblem);
                pollProblem = null;
            }
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            nextStatus = Environment.TickCount64 + 900;
            pollProblem = ex.Message;
            ShowError(pollProblem);
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
        if (engine.TakeRuntimeFailure() is { } failure) ShowError(failure);
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
            catch (Exception ex) { ShowError(ex.Message); }
            if (next != null) TrySavePreference(() => preferences.SaveProfile(next.Profile, next.LayoutId ?? layout.Id));
        }
        if (next != null) sessionLayout ??= GetDisplayLayout();
    }

    private void TrySavePreference(Action save)
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save preferences. {ex.Message}");
        }
    }

    private void SetDevices(IReadOnlyList<DeviceChoice> fresh)
    {
        if (devices.SequenceEqual(fresh)) return;
        var prior = DevicePicker.SelectedItem as DeviceChoice;
        devices = fresh;
        var selected = devices.FirstOrDefault(d => string.Equals(d.Path, frame?.DevicePath ?? prior?.Path ?? preferences.DevicePath,
            StringComparison.OrdinalIgnoreCase));
        if (selected == null && frame == null && preferences.LayoutId is { } remembered)
        {
            var matching = devices.Where(d => d.LayoutId == remembered).ToArray();
            if (matching.Length == 1) selected = matching[0];
        }
        if (selected == null && devices.Count == 1) selected = devices[0];
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
            SelectDeviceProfile(selected);
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || !initialized) return;
        if (frame == null && !busy && DevicePicker.SelectedItem is DeviceChoice device)
        {
            try { SelectDeviceProfile(device); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        Draw();
    }

    private void SelectDeviceProfile(DeviceChoice device)
    {
        var target = device.Layout ?? DeviceLayout.Resolve(device.LayoutId);
        if (selectedBindings?.Layout.Id != device.LayoutId)
            LoadProfiles(null, target);
        TrySavePreference(() =>
        {
            preferences.SaveDevice(device.Path, device.LayoutId);
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
            await RunAction("Loading calibrated controller…", async () =>
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
                    if (current != null) preferences.SaveDevice(current.Path, saved.Id);
                    if (chosenProfile != null) preferences.SaveProfile(chosenProfile, saved.Id);
                }
                else LoadProfiles(frame.Profile);
                RefreshControllerLibrary(saved.Id);
                savedMessage = frame == null ? $"Calibrated {saved.DisplayName}. The selected profile is ready."
                    : $"Calibrated {saved.DisplayName}. Select it after stopping the current converter.";
                savedMessageUntil = Environment.TickCount64 + 9000;
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
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
            catch (Exception ex) { ShowError(ex.Message); }
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
        bool waitingForController = running && !connected;
        ActivityText.Text = !statusKnown ? "Checking session" : suspended ? "Suspended" :
            waitingForController ? "Waiting for controller" : running ? "Running" : "Stopped";
        bool attention = statusKnown && (suspended || waitingForController);
        ActivityDot.Fill = (Brush)FindResource(attention ? "Warning" : running ? "Accent" : "Stopped");
        ActivityText.Foreground = (Brush)FindResource(attention ? "Warning" : "SecondaryText");
        PowerButton.Content = running ? "Stop conversion" : "Start conversion";
        PowerButton.Background = running ? (Brush)FindResource("StopSurface") : (Brush)FindResource("Accent");
        PowerButton.Foreground = running ? Brushes.WhiteSmoke : (Brush)FindResource("StartInk");
        bool compatible = device != null && device.LayoutId == layout.Id && selectedBindings != null;
        PowerButton.IsEnabled = !busy && !exiting && statusKnown && (running || compatible && profileProblem == null);
        ProfilePicker.IsEnabled = ImportButton.IsEnabled = !busy && !exiting && statusKnown;
        EditButton.IsEnabled = !busy && !exiting && chosenProfile != null && profileProblem == null;
        DeleteButton.IsEnabled = !busy && !exiting && statusKnown && selection?.CanReplace == true;
        DevicePicker.IsEnabled = !busy && !exiting && !running;
        CalibrateButton.IsEnabled = !busy && !exiting && !calibrating;
        string? guidance = !running && statusKnown && devices.Count == 0 ? $"Connect your {layout.ShortName} via {layout.Transport}."
            : !running && devices.Count > 1 && device == null ? "Select a controller to start."
            : !running && device != null && device.LayoutId != layout.Id ? $"Choose a profile for {DeviceTitle.Text}." : null;
        if (!busy)
        {
            bool recentlySaved = Environment.TickCount64 < savedMessageUntil;
            SetWork(recentlySaved ? savedMessage : guidance, attention: !recentlySaved && guidance != null);
        }
        var physical = connected && !suspended ? frame?.Physical ?? (preview != null ? engine.Preview : null) : null;
        string missing = suspended ? "Physical input is suspended."
            : !running && device == null && devices.Count > 0 ? "Select a physical controller."
            : !connected ? $"Waiting for the {shortName}."
            : running && frame?.DevicePath == null && devices.Count > 1 ? "Restart the converter to identify its physical device."
            : "Move a stick or press and release a button.";
        Tester.Update(physical, frame?.Output, missing, running ? "Waiting for the XInput slot."
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
                    savedMessage = frame == null ? $"Saved “{profile.Name}” for the next start."
                        : $"Applied “{profile.Name}” on XInput slot {frame.Slot}. Release the controls, then try your mapping.";
                    savedMessageUntil = Environment.TickCount64 + 7000;
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
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || !initialized || ProfilePicker.SelectedItem is not ProfileChoice selected) return;
        await RunAction("Applying profile…", () => ApplyProfile(selected.Name));
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
        await RunAction("Importing profile…", () => ImportProfileFrom(dialog.FileName));
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
        savedMessage = $"Imported “{imported.Name}” for {imported.Bindings.Layout.DisplayName}.";
        savedMessageUntil = Environment.TickCount64 + 7000;
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
        await RunAction("Deleting profile…", async () =>
        {
            string? active = await engine.DeleteProfileAsync(selected, lifetime.Token);
            LoadProfiles(active ?? fallback);
            preferences.SaveProfile(chosenProfile!, layout.Id);
            savedMessage = $"Deleted “{name}”. Selected profile: {chosenProfile}.";
            savedMessageUntil = Environment.TickCount64 + 7000;
        });
    }

    private async void ToggleConverter(object sender, RoutedEventArgs e)
    {
        bool stop = frame != null;
        var selected = chosenProfile;
        var device = DevicePicker.SelectedItem as DeviceChoice;
        await RunAction(stop ? "Releasing the virtual controller…" : "Starting HIDMaestro. Approve the Windows prompt if shown.", async () =>
        {
            if (stop) await engine.StopAsync(lifetime.Token);
            else if (selected != null && device != null)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                starting = attempt;
                try { await engine.StartAsync(selected, device, attempt.Token, preferences.VibrationEnabled(device.LayoutId)); }
                finally { starting = null; }
                TrySavePreference(() => preferences.SaveProfile(selected, device.LayoutId));
            }
        });
    }

    private async Task RunAction(string message, Func<Task> action)
    {
        if (busy || exiting) return;
        busy = true;
        savedMessageUntil = 0;
        ShowError(null);
        SetWork(message);
        PowerButton.IsEnabled = ProfilePicker.IsEnabled = DevicePicker.IsEnabled = ImportButton.IsEnabled =
            EditButton.IsEnabled = DeleteButton.IsEnabled = false;
        UpdateControllerWorkspace();
        await sessionLock.WaitAsync();
        try
        {
            if (exiting) return;
            await action();
            await ReadSession();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            try { await ReadSession(); }
            catch { statusKnown = false; frame = null; }
            try { LoadProfiles(frame?.Profile ?? chosenProfile); }
            catch (Exception listError) { ShowError($"{ex.Message} {listError.Message}"); }
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

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatusBar();
    }

    private void SetWork(string? message, bool attention = false)
    {
        WorkMessage.Text = message ?? "";
        WorkMessage.Foreground = (Brush)FindResource(attention ? "Warning" : "PrimaryText");
        WorkMessage.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatusBar();
    }

    private void UpdateStatusBar() => StatusBar.Visibility =
        WorkMessage.Visibility == Visibility.Visible || ErrorText.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
}
