using System.Diagnostics;
using System.Text.Json;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

// Independent Windows client. Only the deliberate physical arming chord can request sleep.
internal sealed class SleepCheck
{
    private const ushort A = 0x1000, B = 0x2000;
    private sealed record External(DateTimeOffset AtUtc, KernelPowerTimes Power, int[] Slots, XInputReading? Pad);
    private sealed record Observation(External XInput, SessionStatus Host);
    private readonly Dictionary<string, Observation> evidence = [];
    private readonly string file;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly Stopwatch lastSave = new();
    private SessionStatus initial = null!;
    private KernelPowerTimes before = null!;
    private int[] slots = [];
    private Observation? latest;
    private External? firstWakeRead, firstWakePad;
    private DateTimeOffset? requestedUtc;
    private bool heldAtSuspend, heldAtDetachBeforeSuspend;
    private int polls, missingWakeSlotReads, neutralReads;
    private string step = "starting", instruction = "", outcome = "running";
    private string? error;

    private SleepCheck()
    {
        string directory = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "Live-runs")).FullName;
        file = Path.Combine(directory, $"sleep-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
    }

    public static Task<int> RunAsync(CancellationToken token) => new SleepCheck().RunHardwareAsync(token);

    private async Task<int> RunHardwareAsync(CancellationToken token)
    {
        Console.WriteLine($"Report: {file}");
        try
        {
            initial = await SessionControl.SendAsync("status", null, token);
            before = SleepPower.ReadTimes();
            slots = XInputReader.ReadAll().Keys.Order().ToArray();
            if (!initial.Connected || initial.Suspended || !slots.Contains(initial.Slot))
                throw new InvalidOperationException("Start the live converter and connect the selected Bluetooth 85HF first.");

            SetStep("release-before-sleep", "Release all controls. If needed, tap and release A to wake the 85HF. Ctrl+C cancels the check.");
            await WaitAsync(s => s.Host.Connected && !s.Host.WaitingForNeutral && s.Host.Extras == ExtraButtons.None && Rest(s.XInput.Pad), 200, token);
            SetStep("arm-sleep", "To put Windows to sleep now, hold A and fully squeeze RT together for two seconds. Keep both held until Windows sleeps. Release them while the computer is asleep, then wake it with the keyboard or power button.");
            await WaitAsync(s => Gesture(s, A, right: true), 2000, token);
            requestedUtc = DateTimeOffset.UtcNow;
            SetStep("sleep-requested", "Windows is entering sleep. Release A and RT while it sleeps. Wake the computer without touching the gamepad controls.");
            token.ThrowIfCancellationRequested();
            SleepPower.RequestSleep();

            await WaitAsync(s => s.XInput.Power.CompletedCycleSince(before) && s.Host.Suspends == initial.Suspends + 1 &&
                s.Host.Resumes == initial.Resumes + 1 && !s.Host.Suspended && s.Host.LastSuspendUtc.HasValue &&
                s.Host.LastResumeUtc >= s.Host.LastSuspendUtc, 0, token, TimeSpan.FromSeconds(40));
            var resumed = latest!.Host;
            heldAtSuspend = resumed.LastSuspendedState is { Buttons: A, Rt: >= 220, Lt: <= 2 };
            var armed = evidence["arm-sleep"].Host;
            // Windows can remove the BLE collection before it announces system suspend.
            // Only a new detach in this armed interval can establish that it released the chord.
            heldAtDetachBeforeSuspend = resumed.LastSuspendedState == LiveState.Neutral
                && resumed.LastSuspendDetach is { } detach
                && detach.AtUtc >= requestedUtc && detach.AtUtc <= resumed.LastSuspendUtc
                && detach.Disconnects == armed.Disconnects + 1 && detach.Reconnects == armed.Reconnects
                && detach.Generation == armed.Generation + 1 && detach.HeldState == armed.State
                && detach.Received == armed.Received && resumed.ReceivedAtResume == detach.Received;
            SetStep("neutral-after-wake", "Leave the controls released. If the 85HF is asleep, wake it and release the controls.");
            await WaitAsync(s =>
            {
                if (!s.Host.Connected || s.Host.WaitingForNeutral || !s.XInput.Slots.SequenceEqual(slots) || !Rest(s.XInput.Pad)) return false;
                neutralReads++;
                return true;
            }, 500, token);
            SetStep("input-after-wake", "Hold B and fully squeeze LT on the same 85HF. Leave A and RT released.");
            await WaitAsync(s => s.Host.Received > s.Host.ReceivedAtResume && Gesture(s, B, right: false), 200, token);
            SetStep("final-release", "Release B and LT and center the sticks.");
            await WaitAsync(s => s.Host.Connected && !s.Host.WaitingForNeutral && Rest(s.XInput.Pad), 200, token);
            outcome = heldAtSuspend || heldAtDetachBeforeSuspend ? "passed" : "inconclusive";
            if (outcome == "inconclusive")
                error = "The cycle and post-wake input were observed. The held A and RT state could not be traced to suspend or to a Bluetooth removal immediately preceding suspend.";
        }
        catch (Exception ex)
        {
            outcome = ex switch { OperationCanceledException when token.IsCancellationRequested => "cancelled",
                TimeoutException => "timed-out", _ => "failed" };
            error = ex.Message;
        }
        finally { Save(); Console.WriteLine($"Sleep check: {outcome}. {error}".TrimEnd()); }
        return outcome == "passed" ? 0 : 1;
    }

    private async Task WaitAsync(Func<Observation, bool> matches, int stableMs, CancellationToken token, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        TimeSpan? since = null;
        while (true)
        {
            var s = await ReadAsync(token);
            if (matches(s))
            {
                since ??= timer.Elapsed;
                if ((timer.Elapsed - since.Value).TotalMilliseconds >= stableMs)
                {
                    evidence[step] = s;
                    Save();
                    return;
                }
            }
            else since = null;
            if (timer.Elapsed >= (timeout ?? TimeSpan.FromMinutes(5))) throw new TimeoutException($"Timed out at {step}. {instruction}");
            if (lastSave.Elapsed >= TimeSpan.FromSeconds(2)) Save();
            await Task.Delay(50, token);
        }
    }

    private async Task<Observation> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Sample the consumer before asking the converter for status. This preserves the
        // first post-wake reading even if the converter needs more time to answer its pipe.
        var power = SleepPower.ReadTimes();
        var all = XInputReader.ReadAll();
        var x = new External(DateTimeOffset.UtcNow, power, all.Keys.Order().ToArray(), all.GetValueOrDefault(initial.Slot));
        polls++;
        bool woke = requestedUtc != null && power.CompletedCycleSince(before);
        if (woke)
        {
            firstWakeRead ??= x;
            if (x.Pad == null) missingWakeSlotReads++;
            else if (firstWakePad == null)
            {
                firstWakePad = x;
                if (x.Pad is not { Buttons: 0, Lt: 0, Rt: 0, Lx: 0, Ly: 0, Rx: 0, Ry: 0 })
                    throw new InvalidDataException("The first available XInput reading after system wake was not neutral.");
            }
            if (step is "sleep-requested" or "neutral-after-wake" && x.Pad != null && !Rest(x.Pad))
                throw new InvalidDataException("A control became active before the requested post-wake gesture.");
        }
        // The original slot can be temporarily unavailable while Windows resumes its driver.
        // A different occupied slot is a failure, including during that recovery window.
        bool awaitingSlot = requestedUtc != null && firstWakePad == null;
        if (awaitingSlot ? x.Slots.Except(slots).Any() : !x.Slots.SequenceEqual(slots))
            throw new IOException($"Occupied XInput slots changed from [{string.Join(",", slots)}] to [{string.Join(",", x.Slots)}].");

        var host = await SessionControl.SendAsync("status", null, token);
        latest = new(x, host);
        if (host.SessionId != initial.SessionId || host.Slot != initial.Slot || host.Profile != initial.Profile)
            throw new IOException("The converter session, assigned slot or active profile changed during the check.");
        if (requestedUtc == null && (host.Suspends != initial.Suspends || host.Resumes != initial.Resumes || power != before))
            throw new IOException("Windows changed power state before the sleep request was armed.");
        if (host.Suspends > initial.Suspends + 1 || host.Resumes > initial.Resumes + 1)
            throw new IOException("Multiple power transitions occurred during the check.");
        return latest;
    }

    private static bool Gesture(Observation s, ushort buttons, bool right) =>
        s.Host.Connected && !s.Host.WaitingForNeutral && s.Host.Extras == ExtraButtons.None &&
        s.Host.State.Buttons == buttons && s.XInput.Pad is { } p && p.Buttons == buttons &&
        (right ? p.Rt >= 220 && p.Lt <= 2 && s.Host.State.Rt >= 220 && s.Host.State.Lt <= 2
               : p.Lt >= 220 && p.Rt <= 2 && s.Host.State.Lt >= 220 && s.Host.State.Rt <= 2);

    private static bool Rest(XInputReading? p) =>
        p is { Buttons: 0, Lt: <= 2, Rt: <= 2 } && Math.Abs((int)p.Lx) <= 2500 && Math.Abs((int)p.Ly) <= 2500 &&
            Math.Abs((int)p.Rx) <= 2500 && Math.Abs((int)p.Ry) <= 2500;

    private void SetStep(string name, string text)
    {
        step = name;
        instruction = text;
        Console.WriteLine(text);
        Save();
    }

    private void Save()
    {
        var report = new { startedUtc = started, step, instruction, outcome, error, initialSession = initial?.SessionId,
            profile = initial?.Profile, slot = initial?.Slot, initialSlots = slots, initialKernel = before, requestedUtc,
            polls, firstWakeRead, firstWakePad, missingWakeSlotReads, heldAtSuspend, heldAtDetachBeforeSuspend, neutralReads, evidence, latest };
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
        lastSave.Restart();
    }
}
