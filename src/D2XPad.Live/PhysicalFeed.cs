using System.Diagnostics;
using D2XPad.Capture;

namespace D2XPad.Live;

internal sealed class PhysicalFeed : IDisposable
{
    private readonly RawInput input;
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PhysicalSession session;
    private readonly LiveOutput output;
    private readonly PhysicalRumbleSink? motors;
    private readonly object motorGate = new();
    private HidCollection? motorDevice;
    private bool vibrationEnabled;
    private bool suspended;
    private long feedbackValidAfter;

    public Task Failure => failure.Task;
    public PhysicalFrame? Latest => session.Latest;
    public long Received => session.Received;
    public long Unexpected => session.Unexpected;
    public PhysicalConnection Connection => session.Connection;
    public DeviceLayout Layout => session.Layout;
    public void ChangeBindings(PadBindings bindings) => session.ChangeBindings(bindings);
    public bool VibrationEnabled { get { lock (motorGate) return vibrationEnabled; } }
    public long VibrationEffects => motors?.EffectsWritten ?? 0;
    public string? VibrationProblem => VibrationEnabled
        ? motors?.Problem ?? output.RumbleFeedbackProblem : null;

    public static HidCollection[] Devices() => RawInput.Enumerate((vid, pid, page, usage) =>
        DeviceLayout.Supported.Any(layout => layout.MatchesCollection(vid, pid, page, usage)))
        .Where(PhysicalSession.Supports)
        .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    public static HidCollection Select(int? number, string? path = null)
    {
        var found = Devices();
        if (path != null)
            return Array.Find(found, d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("The selected controller is no longer connected.");
        if (found.Length == 0) throw new IOException("No supported controller is connected. Wake the controller and check its connection.");
        if (number is { } i && i >= 1 && i <= found.Length) return found[i - 1];
        if (number == null && found.Length == 1) return found[0];
        throw new ArgumentException("Use list, then choose one matching controller collection with --device N.");
    }

    public PhysicalFeed(HidCollection device, PadBindings bindings, LiveOutput output, bool vibrationEnabled = false)
    {
        this.output = output;
        session = new(device, bindings, output.Submit, output.ResetForPowerTransition);
        if (RumbleSurvey.Supports(device))
        {
            motorDevice = device;
            motors = new();
            output.RumbleChanged += ForwardRumble;
        }
        try
        {
            SetVibration(vibrationEnabled);
            input = new RawInput([device], session.Report, reason =>
            {
                SetMotorDevice(null);
                try { session.Stop(); }
                catch (IOException ex) { failure.TrySetException(ex); return; }
                failure.TrySetException(new IOException(reason));
            }, new(session.SelectInstance, DeviceChanged, PowerChanged));
        }
        catch
        {
            DisposeMotors();
            throw;
        }
    }

    public void SetVibration(bool enabled)
    {
        if (enabled && motors == null)
            throw new InvalidOperationException("Game vibration is unavailable for this controller and connection.");
        lock (motorGate)
        {
            if (vibrationEnabled == enabled) return;
            vibrationEnabled = enabled;
            UpdateMotorTarget();
        }
    }

    private void DeviceChanged(HidCollection? device)
    {
        if (device == null) SetMotorDevice(null);
        session.SetDevice(device);
        if (device != null) SetMotorDevice(device);
    }

    private void SetMotorDevice(HidCollection? device)
    {
        if (motors == null) return;
        lock (motorGate)
        {
            motorDevice = device;
            UpdateMotorTarget();
        }
    }

    private void PowerChanged(bool sleeping)
    {
        if (sleeping) SetMotorSuspended(true);
        session.PowerChanged(sleeping);
        if (!sleeping) SetMotorSuspended(false);
    }

    private void SetMotorSuspended(bool value)
    {
        if (motors == null) return;
        lock (motorGate)
        {
            suspended = value;
            UpdateMotorTarget();
        }
    }

    // The host and converter use the same Windows performance counter. Reject
    // feedback that was queued before the current device became eligible.
    private void UpdateMotorTarget()
    {
        feedbackValidAfter = Stopwatch.GetTimestamp();
        motors?.SetTarget(motorDevice, vibrationEnabled && !suspended);
    }

    private void ForwardRumble(byte left, byte right, long observed)
    {
        lock (motorGate)
        {
            if (motors == null || !vibrationEnabled || suspended || motorDevice == null || observed <= feedbackValidAfter)
                return;
            motors.Submit(motors.Generation, left, right);
        }
    }

    private void DisposeMotors()
    {
        if (motors == null) return;
        output.RumbleChanged -= ForwardRumble;
        SetMotorDevice(null);
        motors.Dispose();
    }

    public void Dispose()
    {
        SetVibration(false);
        SetMotorDevice(null);
        try { input.Dispose(); }
        finally
        {
            try { DisposeMotors(); }
            finally { session.Stop(); }
        }
    }
}
