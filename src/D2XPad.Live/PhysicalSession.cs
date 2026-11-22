using D2XPad.Capture;

namespace D2XPad.Live;

internal sealed record PhysicalFrame(long Number, ExtraButtons Extras, byte[] Raw);
internal sealed record SuspendDetachEvidence(DateTimeOffset AtUtc, long Received, int Disconnects,
    int Reconnects, long Generation, LiveState HeldState);
internal sealed record PhysicalConnection(bool Connected, bool WaitingForNeutral, int Disconnects,
    int Reconnects, LiveState? LastDisconnectedState, LiveState State, long Received,
    PhysicalFrame? Latest, long Generation, bool Suspended, int Suspends, int Resumes,
    LiveState? LastSuspendedState, DateTimeOffset? LastSuspendUtc, DateTimeOffset? LastResumeUtc,
    long ReceivedAtResume, SuspendDetachEvidence? LastSuspendDetach, string DevicePath);

// The lock orders physical packets, hotplug notifications, and profile switches before submission.
internal sealed class PhysicalSession
{
    private readonly object gate = new();
    private readonly HidCollection selected;
    public DeviceLayout Layout { get; }
    private readonly Action<LiveState> submit;
    private readonly Action resetForPowerTransition;
    private PadBindings bindings;
    private HidCollection? active;
    private PhysicalFrame? latest;
    private LiveState state = LiveState.Neutral;
    private LiveState? lastDisconnectedState;
    private bool waitingForNeutral = true;
    private int disconnects, reconnects;
    private long generation = 1, received, unexpected;
    private bool suspended;
    private int suspends, resumes;
    private LiveState? lastSuspendedState;
    private DateTimeOffset? lastSuspendUtc, lastResumeUtc;
    private long receivedAtResume;
    private SuspendDetachEvidence? pendingDetach, lastSuspendDetach;

    public PhysicalSession(HidCollection selected, PadBindings bindings, Action<LiveState> submit,
        Action resetForPowerTransition)
    {
        Layout = LayoutFor(selected) ?? throw new InvalidDataException("The selected HID collection has no supported layout.");
        if (bindings.Layout.Id != Layout.Id)
            throw new InvalidDataException($"Choose a profile for {Layout.DisplayName}.");
        active = this.selected = selected;
        this.bindings = BindToSelectedLayout(bindings);
        this.submit = submit;
        this.resetForPowerTransition = resetForPowerTransition;
    }

    public PhysicalConnection Connection
    {
        get
        {
            lock (gate) return new(active != null, waitingForNeutral, disconnects, reconnects,
                lastDisconnectedState, state, received, latest, generation, suspended, suspends, resumes,
                lastSuspendedState, lastSuspendUtc, lastResumeUtc, receivedAtResume, lastSuspendDetach,
                active?.Path ?? selected.Path);
        }
    }

    public PhysicalFrame? Latest { get { lock (gate) return latest; } }
    public long Received => Interlocked.Read(ref received);
    public long Unexpected => Interlocked.Read(ref unexpected);

    internal static bool MatchesDevice(DeviceLayout layout, HidCollection d) =>
        layout.MatchesCollection(d.VendorId, d.ProductId, d.UsagePage, d.Usage) &&
        layout.MatchesReports(d.Transport, d.InputReportLength, d.InputReportIds) &&
        (layout is not CalibratedLayout measured || measured.MatchesDescriptor(d));

    public static DeviceLayout? LayoutFor(HidCollection d) => DeviceLayout.Supported.FirstOrDefault(layout => MatchesDevice(layout, d))
        ?? (MatchesDevice(DeviceLayout.Bluetooth85HF, d) ? DeviceLayout.Bluetooth85HF : null);

    public static bool Supports(HidCollection d) => LayoutFor(d) != null;

    public HidCollection? SelectInstance(IReadOnlyList<HidCollection> candidates)
    {
        var eligible = candidates.Where(d => MatchesDevice(Layout, d));
        // Without a container ID, only the original interface path identifies this instance.
        var exact = eligible.FirstOrDefault(c => string.Equals(c.Path, selected.Path, StringComparison.OrdinalIgnoreCase)
            && (selected.ContainerId == null || c.ContainerId == null ||
                string.Equals(c.ContainerId, selected.ContainerId, StringComparison.OrdinalIgnoreCase)));
        if (exact != null || selected.ContainerId == null) return exact;
        var matches = eligible.Where(c => string.Equals(c.ContainerId, selected.ContainerId,
            StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public void SetDevice(HidCollection? device)
    {
        lock (gate)
        {
            if (ReferenceEquals(active, device)) return;
            // Windows can remove the Bluetooth collection before delivering the suspend callback.
            if (active != null && device == null)
                pendingDetach = new(DateTimeOffset.UtcNow, received, disconnects + 1, reconnects, generation + 1, state);
            else
                pendingDetach = null;
            if (active != null)
            {
                lastDisconnectedState = state;
                disconnects++;
                Submit(LiveState.Neutral);
            }
            active = device;
            latest = null;
            waitingForNeutral = true;
            generation++;
            if (device != null) reconnects++;
        }
    }

    public void ChangeBindings(PadBindings replacement)
    {
        lock (gate)
        {
            if (replacement.Layout.Id != Layout.Id)
                throw new InvalidDataException($"Choose a profile for {Layout.DisplayName}.");
            var updated = BindToSelectedLayout(replacement);
            bool needsRelease = latest == null || !Layout.IsPhysicallyReleased(latest.Raw, updated.Deadzones);
            Submit(LiveState.Neutral);
            // The last physical report still belongs to this attachment and is independent of its mapping.
            waitingForNeutral = needsRelease;
            bindings = updated;
        }
    }

    private PadBindings BindToSelectedLayout(PadBindings profile) => profile.Layout == Layout ? profile :
        PadBindings.CreateMappings(Layout, profile.Mappings, profile.Deadzones);

    public void PowerChanged(bool sleeping)
    {
        lock (gate)
        {
            if (suspended == sleeping) return;
            if (sleeping)
            {
                lastSuspendedState = state;
                lastSuspendDetach = active == null ? pendingDetach : null;
                pendingDetach = null;
                lastSuspendUtc = DateTimeOffset.UtcNow;
                suspends++;
            }
            else
            {
                lastResumeUtc = DateTimeOffset.UtcNow;
                receivedAtResume = received;
                resumes++;
            }
            suspended = sleeping;
            waitingForNeutral = true;
            latest = null;
            resetForPowerTransition();
            state = LiveState.Neutral;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            active = null;
            pendingDetach = null;
            waitingForNeutral = true;
            latest = null;
            Submit(LiveState.Neutral);
        }
    }

    public void Report(HidCollection device, byte[] report)
    {
        lock (gate)
        {
            if (suspended || !ReferenceEquals(active, device)) return;
            if (!bindings.TryDecode(report, out var decoded, out var extras))
            {
                Interlocked.Increment(ref unexpected);
                return;
            }
            if (waitingForNeutral)
            {
                waitingForNeutral = !Layout.IsPhysicallyReleased(report, bindings.Deadzones);
                decoded = LiveState.Neutral;
            }
            Submit(decoded);
            latest = new(Interlocked.Increment(ref received), extras, report);
        }
    }

    private void Submit(LiveState next)
    {
        if (next == state) return;
        submit(next);
        state = next;
    }
}
