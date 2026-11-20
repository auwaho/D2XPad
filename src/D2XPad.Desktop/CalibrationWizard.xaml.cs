using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using D2XPad.Capture;
using D2XPad.Live;

namespace D2XPad.Desktop;

public partial class CalibrationWizard : Window
{
    private enum Page { Device, Neutral, Capture, Review }
    private sealed record Candidate(HidCollection Device, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Func<IReadOnlyList<HidCollection>> enumerate;
    private readonly Func<HidCollection, ICalibrationInput> observe;
    private readonly ControllerEntry? original;
    private readonly DispatcherTimer timer;
    private readonly List<CalibrationStep> steps = CalibrationStep.Standard();
    private readonly List<IReadOnlyList<byte[]>> neutral = [];
    private readonly List<byte[]> poses = [];
    private Page page;
    private HidCollection? device;
    private ICalibrationInput? input;
    private ControllerCalibration? calibration;
    private DeviceLayout? preview;
    private CalibrationStep? step;
    private long firstNeutralVersion;
    private bool waitingForRelease, selecting;
    private CancellationTokenSource? observingRest;
    internal DeviceLayout? SavedLayout { get; private set; }
    internal string? SelectedPath => device?.Path;

    public CalibrationWizard() : this(() => CalibrationInput.Devices(), d => new CalibrationInput(d)) { }
    internal CalibrationWizard(ControllerEntry? original, string? path)
        : this(() => CalibrationInput.Devices(original?.Layout.Id).Where(d => path == null ||
            string.Equals(path, d.Path, StringComparison.OrdinalIgnoreCase)).ToArray(), d => new CalibrationInput(d), original)
    {
    }

    internal CalibrationWizard(Func<IReadOnlyList<HidCollection>> enumerate, Func<HidCollection, ICalibrationInput> observe,
        ControllerEntry? original = null)
    {
        this.enumerate = enumerate;
        this.observe = observe;
        this.original = original;
        InitializeComponent();
        WindowCaption.UseDark(this);
        KeyboardFocusCues.Track(this);
        if (original != null)
        {
            Title = "Recalibrate controller";
            CandidateLabel.Text = "Saved HID controller";
            System.Windows.Automation.AutomationProperties.SetName(CandidatePicker, CandidateLabel.Text);
            ExtraHelp.Text = original.IsBuiltIn
                ? "L4, R4, PL and PR use the verified physical fields."
                : "Existing extra buttons keep their names and profile assignments. Add a new input to extend the layout.";
            if (original.IsBuiltIn)
            {
                DeviceName.IsReadOnly = true;
                ExtraName.Visibility = AddExtraButton.Visibility = RemoveExtraButton.Visibility = Visibility.Collapsed;
            }
            ResetSteps();
        }
        ReviewTester.ShowPhysicalOnly();
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        timer.Tick += (_, _) => UpdateSignal();
        Loaded += (_, _) => { ChangePage(Page.Device); ListDevices(); timer.Start(); };
        Closed += (_, _) =>
        {
            observingRest?.Cancel();
            timer.Stop();
            input?.Dispose();
            input = null;
        };
    }

    private void ResetSteps()
    {
        steps.Clear();
        steps.AddRange(CalibrationStep.Standard());
        if (original != null)
            foreach (var extra in original.Layout.ExtraSources) steps.Add(CalibrationStep.Extra(extra.Name));
    }

    private void ListDevices()
    {
        Error(null);
        try
        {
            var prior = (CandidatePicker.SelectedItem as Candidate)?.Device.Path;
            var choices = enumerate().Select((d, i) => new Candidate(d,
                $"{i + 1}. {d.Transport} · {d.VendorId:X4}:{d.ProductId:X4} · {(d.Usage == 4 ? "Joystick" : "Gamepad")}")).ToArray();
            CandidatePicker.ItemsSource = choices;
            CandidatePicker.SelectedItem = choices.FirstOrDefault(c => c.Device.Path == prior) ?? choices.FirstOrDefault();
            CandidateHint.Text = original != null
                ? choices.Length == 0
                    ? $"Reconnect {original.Layout.DisplayName} via {original.Layout.Transport} and refresh."
                    : "Record only the inputs you want to adjust. Skipped inputs keep their saved calibration."
                : choices.Length == 0
                ? "No new HID gamepads found. Connect a controller in DirectInput mode and refresh. The Bluetooth 85HF already has a built-in layout."
                : "Choose the physical controller you want to set up. Already calibrated controllers are available in the main window.";
        }
        catch (Exception ex) { CandidatePicker.ItemsSource = null; Error(ex.Message); }
        NextButton.IsEnabled = CandidatePicker.SelectedItem != null;
    }

    private void RefreshDevices(object sender, RoutedEventArgs e) => ListDevices();
    private void CandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        var candidate = CandidatePicker.SelectedItem as Candidate;
        DeviceName.Text = candidate == null ? "" : original?.Layout.DisplayName ?? $"Controller {candidate.Device.VendorId:X4}-{candidate.Device.ProductId:X4}";
        CandidatePicker.ToolTip = candidate?.Device.Path;
        NextButton.IsEnabled = candidate != null;
    }

    private void ChangePage(Page value)
    {
        page = value;
        Error(null);
        DevicePage.Visibility = Visible(value == Page.Device);
        NeutralPage.Visibility = Visible(value == Page.Neutral);
        CapturePage.Visibility = Visible(value == Page.Capture);
        ReviewPage.Visibility = Visible(value == Page.Review);
        BackButton.Visibility = Visible(value != Page.Device);
        StageText.Text = $"Controller calibration · {(int)value + 1} of 4";
        PageTitle.Text = value switch { Page.Device => "Choose a controller", Page.Neutral => "Record the resting position",
            Page.Capture => "Identify physical inputs", _ => "Test your layout" };
        NextButton.Content = value switch { Page.Device => "Continue", Page.Neutral => "Record neutral",
            Page.Capture => "Test layout", _ => "Save layout" };
        NextButton.IsEnabled = value != Page.Device || CandidatePicker.SelectedItem != null;
        if (value == Page.Neutral)
            NeutralPrompt.Text = neutral.Count == 0
                ? "Release every button and trigger. Let both sticks rest in the center, then record neutral."
                : "Move a stick or press a button once, release it, then record the resting position again.";
        UpdateSignal();
    }

    private async void Next(object sender, RoutedEventArgs e)
    {
        Error(null);
        try
        {
            switch (page)
            {
                case Page.Device when CandidatePicker.SelectedItem is Candidate selected:
                    device = selected.Device;
                    neutral.Clear();
                    calibration = NewCalibration();
                    input = observe(device);
                    ChangePage(Page.Neutral);
                    break;
                case Page.Neutral:
                    await ObserveRest();
                    break;
                case Page.Capture:
                    if (poses.Count > 0) throw new InvalidDataException("Finish recording this input or select Skip input before testing.");
                    var complete = NewCalibration();
                    preview = complete.Preview();
                    calibration = complete;
                    var captured = steps.Where(s => s.Samples != null).ToArray();
                    var summary = new List<string> { $"{captured.Length} of {steps.Count} inputs recorded" };
                    if (preview is CalibratedLayout measured)
                        foreach (var trigger in measured.File.Controls.Where(c => c.Source is "LT" or "RT"))
                            summary.Add($"{trigger.Source} {(Math.Abs(trigger.Last - trigger.First) == 1 ? "digital" : "analog")}");
                    ReviewSummary.Text = string.Join("  ·  ", summary);
                    if (original != null)
                        ReviewHint.Text = "Check your changes in the tester. Skipped inputs keep their current calibration and profile assignments."
                            + MovedExtraSummary(original.Layout, preview);
                    ReviewTester.SetProfile(preview, PadBindings.Create(preview, []));
                    ChangePage(Page.Review);
                    break;
                case Page.Review:
                    var current = input!.Read();
                    if (!preview!.TryDecodePhysical(current.Report, out _, out _))
                        throw new InvalidDataException("Waiting for a matching controller report.");
                    using (original == null ? null : EngineClient.ReserveStoppedConverter())
                        SavedLayout = calibration!.Save();
                    DialogResult = true;
                    break;
            }
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async Task ObserveRest()
    {
        if (observingRest != null) return;
        var source = input!;
        var (version, _) = source.Read();
        if (neutral.Count == 1 && version == firstNeutralVersion)
            throw new InvalidDataException("Move and release a control to send another report.");
        using var observation = new CancellationTokenSource();
        observingRest = observation;
        NextButton.IsEnabled = BackButton.IsEnabled = false;
        SignalStatus.Text = "Recording the resting position. Keep all controls released.";
        try
        {
            var samples = await source.ReadRestSamplesAsync(observation.Token);
            observation.Token.ThrowIfCancellationRequested();
            var trial = NewCalibration();
            trial.AddNeutralSamples(samples);
            neutral.Add(samples);
            calibration = trial;
            if (neutral.Count == 1)
            {
                firstNeutralVersion = source.Read().Version;
                ChangePage(Page.Neutral);
            }
            else
            {
                ChangePage(Page.Capture);
                SelectStep(steps[0]);
            }
        }
        catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
        finally
        {
            observingRest = null;
            if (!observation.IsCancellationRequested)
            {
                BackButton.IsEnabled = true;
                UpdateSignal();
            }
        }
    }

    private static string MovedExtraSummary(DeviceLayout original, DeviceLayout preview)
    {
        if (original is not CalibratedLayout before || preview is not CalibratedLayout after) return "";
        var moved = before.File.Controls.Where(c => c.ExtraName != null &&
            after.File.Controls.Any(next => next.Source == c.Source && next.Field != c.Field))
            .Select(c => c.ExtraName);
        string names = string.Join(", ", moved);
        return names.Length == 0 ? "" : $"\nPhysical fields changed for {names}. Their assignments follow the newly recorded inputs.";
    }

    private ControllerCalibration NewCalibration(CalibrationStep? replace = null, byte[][]? replacement = null)
    {
        var result = new ControllerCalibration(device!, DeviceName.Text.Trim(), original);
        foreach (var samples in neutral) result.AddNeutralSamples(samples);
        foreach (var item in steps)
        {
            var reports = item == replace ? replacement : item.Samples;
            if (reports != null) item.Bind(result, reports);
        }
        return result;
    }

    private void Back(object sender, RoutedEventArgs e)
    {
        if (page == Page.Review) { ChangePage(Page.Capture); SelectStep(step ?? steps[0]); return; }
        if (page == Page.Capture)
        {
            if (MessageBox.Show(this, "Start again from the resting position? This clears the inputs recorded in this wizard.",
                "Restart calibration", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ResetSteps();
            neutral.Clear();
            poses.Clear();
            step = null;
            ChangePage(Page.Neutral);
            return;
        }
        input?.Dispose();
        input = null;
        neutral.Clear();
        ChangePage(Page.Device);
        ListDevices();
    }

    private void SelectStep(CalibrationStep selected)
    {
        step = selected;
        poses.Clear();
        waitingForRelease = true;
        selecting = true;
        try
        {
            ControlPicker.ItemsSource = steps;
            ControlPicker.Items.Refresh();
            ControlPicker.SelectedItem = selected;
        }
        finally { selecting = false; }
        Error(null);
        CaptureCount.Text = $"{steps.Count(s => s.Samples != null)} recorded · {steps.Count(s => s.Skipped)} skipped";
        PoseTitle.Text = selected.Label;
        SkipButton.Content = selected.Samples == null ? "Skip input" : "Clear and skip";
        RemoveExtraButton.IsEnabled = selected.Kind == CaptureKind.Extra && !IsSavedExtra(selected);
        NextButton.IsEnabled = steps.Any(s => s.Samples != null);
        UpdateSignal();
    }

    private void ControlChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!selecting && ControlPicker.SelectedItem is CalibrationStep selected) SelectStep(selected);
    }

    internal void UpdateSignal()
    {
        if (input == null || page == Page.Device || observingRest != null) return;
        try
        {
            var (version, report) = input.Read();
            if (page == Page.Review)
            {
                bool valid = preview!.TryDecodePhysical(report, out var physical, out var extras);
                ReviewTester.Update(valid ? PadReading.FromPhysical(physical, extras) : null, null);
                NextButton.IsEnabled = valid;
                return;
            }
            if (page == Page.Neutral)
            {
                SignalStatus.Text = $"Receiving controller reports · {report.Length} bytes";
                NextButton.IsEnabled = neutral.Count == 0 || version != firstNeutralVersion;
                return;
            }
            if (step == null) return;
            bool atRest = calibration!.IsNeutral(report);
            if (atRest) waitingForRelease = false;
            string retake = step.Samples != null && poses.Count == 0 ? "Recorded. Repeat to replace. " : "";
            PosePrompt.Text = waitingForRelease ? "Release the controls before the next pose."
                : $"{retake}{poses.Count + 1} of {step.Poses.Length} · {step.Poses[poses.Count]} Then choose Record pose.";
            CaptureButton.IsEnabled = !waitingForRelease && !atRest;
        }
        catch (Exception ex)
        {
            if (page == Page.Neutral) { NextButton.IsEnabled = false; SignalStatus.Text = ex.Message; }
            if (page == Page.Capture) { CaptureButton.IsEnabled = false; PosePrompt.Text = ex.Message; }
            if (page == Page.Review) { NextButton.IsEnabled = false; ReviewTester.Update(null, null, ex.Message); }
        }
    }

    private void CapturePose(object sender, RoutedEventArgs e)
    {
        if (step == null) return;
        Error(null);
        try
        {
            byte[] report = input!.Read().Report;
            if (waitingForRelease || calibration!.IsNeutral(report))
                throw new InvalidDataException("Release the controls, then hold the requested pose.");
            poses.Add(report);
            waitingForRelease = true;
            if (poses.Count == step.Poses.Length)
            {
                var finished = poses.ToArray();
                poses.Clear();
                calibration = NewCalibration(step, finished);
                step.Samples = finished;
                step.Skipped = false;
                Advance();
            }
            else UpdateSignal();
        }
        catch (Exception ex) { UpdateSignal(); Error(ex.Message); }
    }

    private void SkipControl(object sender, RoutedEventArgs e)
    {
        if (step == null) return;
        step.Samples = null;
        step.Skipped = true;
        Advance();
    }

    private void Advance()
    {
        var remaining = steps.Skip(steps.IndexOf(step!) + 1).Concat(steps)
            .FirstOrDefault(s => s.Samples == null && !s.Skipped);
        SelectStep(remaining ?? step!);
        if (remaining == null) CaptureCount.Text += original?.IsBuiltIn == true
            ? ". Choose Test layout to review the changes."
            : ". Add an extra button or choose Test layout.";
    }

    private void AddExtra(object sender, RoutedEventArgs e)
    {
        if (original?.IsBuiltIn == true) return;
        string name = ExtraName.Text.Trim().ToUpperInvariant();
        try { ControllerCalibration.ValidateExtraName(name); }
        catch (InvalidDataException ex) { Error(ex.Message); return; }
        if (steps.Any(s => string.Equals(s.Source, name, StringComparison.OrdinalIgnoreCase)))
        { Error("This button name is already in use."); return; }
        var item = CalibrationStep.Extra(name);
        steps.Add(item);
        ExtraName.Clear();
        SelectStep(item);
    }

    private void RemoveExtra(object sender, RoutedEventArgs e)
    {
        if (step?.Kind != CaptureKind.Extra || IsSavedExtra(step)) return;
        int i = steps.IndexOf(step);
        steps.RemoveAt(i);
        SelectStep(steps[Math.Min(i, steps.Count - 1)]);
    }

    private bool IsSavedExtra(CalibrationStep candidate) =>
        original?.Layout.ExtraSources.Any(extra => extra.Name == candidate.Source) == true;

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Error(string? text) { Problem.Text = text; Problem.Visibility = Visible(text != null); }
}
