using System.Diagnostics;
using System.Text.Json;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

// Runs as a separate standard-user process. The converter reports physical lifecycle
// events; every virtual control and every occupied slot are also read through XInput.
internal sealed class ReconnectCheck
{
    private const string Default = "default", Alternate = "southpaw-buttons";
    private const ushort A = 0x1000, B = 0x2000;
    private sealed record Sample(DateTimeOffset AtUtc, SessionStatus Host, int[] Slots, XInputReading Pad);
    private sealed record Evidence(string Step, Sample Sample);
    private static readonly TimeSpan GestureTimeout = TimeSpan.FromMinutes(5);
    private readonly List<Evidence> evidence = [];
    private readonly Stopwatch lastSave = new();
    private readonly string file;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private SessionStatus? initial;
    private Sample? latest;
    private int[] initialSlots = [];
    private string activeProfile = "";
    private string step = "starting", instruction = "Connecting to the running converter.";
    private string outcome = "running";
    private string? error, cleanupError;
    private bool changed, profileRestored;
    private bool? heldAtRemoval;
    private int pollCount, offlineNeutralSamples;

    private ReconnectCheck()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "Live-runs"));
        file = Path.Combine(directory.FullName, $"reconnect-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
    }

    internal static Task<int> RunAsync(CancellationToken token) => new ReconnectCheck().RunHardwareAsync(token);

    private async Task<int> RunHardwareAsync(CancellationToken token)
    {
        Console.WriteLine($"Report: {file}");
        try
        {
            initial = await SessionControl.SendAsync("status", null, token);
            activeProfile = initial.Profile;
            initialSlots = XInputReader.ReadAll().Keys.Order().ToArray();
            if (!initial.Connected || !initialSlots.Contains(initial.Slot))
                throw new InvalidOperationException("Start the live converter with the physical Bluetooth 85HF connected before this check.");
            await ReadAsync(token);
            Mark();
            if (activeProfile != Default) await SwitchAsync(Default, token);

            SetStep("initial-release", "Release all controls. If the controller is idle, tap and release A once.");
            await WaitAsync(s => ConnectedBeforeCut(s) && !s.Host.WaitingForNeutral && AtRest(s.Pad), 150, token);
            Mark();
            SetStep("hold-before-switch", "Hold PL and fully squeeze RT. Keep both held until asked to release.");
            await WaitAsync(s => ConnectedBeforeCut(s) && RearTrigger(s, ExtraButtons.PL, A, right: true), 200, token);
            Mark();

            await SwitchAsync(Alternate, token);
            SetStep("neutral-after-switch", "Keep PL and RT held while the profile changes.");
            await WaitAsync(s =>
            {
                if (!ConnectedBeforeCut(s) || !s.Host.WaitingForNeutral)
                    throw new InvalidDataException("The release barrier ended before the held profile switch could be observed.");
                return s.Host.State == LiveState.Neutral && ExactlyNeutral(s.Pad);
            }, 200, token, TimeSpan.FromSeconds(3));
            Mark();
            SetStep("release-after-switch", "Release PL and RT completely and center both sticks.");
            await WaitAsync(s => ConnectedBeforeCut(s) && !s.Host.WaitingForNeutral && AtRest(s.Pad), 150, token);
            Mark();
            SetStep("hold-before-disconnect", "Hold PL and fully squeeze RT again. Wait for confirmation before powering off.");
            await WaitAsync(s => ConnectedBeforeCut(s) && RearTrigger(s, ExtraButtons.PL, B, right: true), 200, token);
            Mark();

            SetStep("power-off", "Keep PL and RT held and power off the 85HF. Leave the controller off until asked to reconnect.");
            var detached = await WaitAsync(s => !s.Host.Connected && s.Host.Disconnects == initial.Disconnects + 1, 0, token);
            heldAtRemoval = detached.Host.LastDisconnectedState is { } previous &&
                (previous.Buttons & B) != 0 && previous.Rt >= 220;
            Mark();
            SetStep("offline-neutral", "Keep the 85HF off while XInput is sampled.");
            var offline = await WaitAsync(s =>
            {
                if (s.Host.Connected || s.Host.Disconnects != initial.Disconnects + 1)
                    throw new InvalidDataException("The controller reconnected before the offline neutral readings were complete.");
                if (s.Host.State != LiveState.Neutral || !ExactlyNeutral(s.Pad))
                {
                    offlineNeutralSamples = 0;
                    return false;
                }
                offlineNeutralSamples++;
                return true;
            }, 1000, token, TimeSpan.FromSeconds(5));
            Mark();

            SetStep("power-on", "Turn the same 85HF on, release the controls and let it reconnect over Bluetooth. If needed, tap and release A.");
            bool Reconnected(Sample s) => s.Host.Connected && s.Host.Disconnects == initial.Disconnects + 1
                && s.Host.Reconnects == initial.Reconnects + 1 && s.Host.Generation > offline.Host.Generation;
            await WaitAsync(s => Reconnected(s) && !s.Host.WaitingForNeutral && AtRest(s.Pad), 150, token);
            Mark();
            SetStep("input-after-reconnect", "Hold PR and fully squeeze LT. This is a different action from the one held before disconnect.");
            await WaitAsync(s => Reconnected(s) && s.Host.Received > offline.Host.Received
                && RearTrigger(s, ExtraButtons.PR, A, right: false), 200, token);
            Mark();
            SetStep("final-release", "Release PR and LT, then center both sticks.");
            await WaitAsync(s => Reconnected(s) && AtRest(s.Pad), 150, token);
            Mark();
            if (heldAtRemoval == true) outcome = "passed";
            else
            {
                outcome = "inconclusive";
                error = "XInput reached neutral offline and fresh input returned, but the last physical state at removal did not still hold both PL and RT.";
            }
        }
        catch (Exception ex)
        {
            outcome = ex switch { OperationCanceledException when token.IsCancellationRequested => "cancelled", TimeoutException => "timed-out", _ => "failed" };
            error = ex.GetBaseException().Message;
        }
        finally
        {
            if (initial != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var now = await SessionControl.SendAsync("status", null, cleanup.Token);
                    if (now.SessionId != initial.SessionId) throw new IOException("The original converter session ended.");
                    if (changed && now.Profile != initial.Profile)
                    {
                        if (now.Profile != activeProfile)
                            throw new IOException("The active profile was changed by another client. It was left selected.");
                        await SwitchAsync(initial.Profile, cleanup.Token);
                    }
                    else activeProfile = now.Profile;
                    var last = await ReadAsync(cleanup.Token);
                    profileRestored = last.Host.Profile == initial.Profile;
                    if (!profileRestored) throw new IOException("The initial profile could not be verified.");
                    evidence.Add(new("profile-restored", last));
                }
                catch (Exception ex) { cleanupError = ex.GetBaseException().Message; outcome = "failed"; }
            }
            Save();
            Console.WriteLine($"Hardware check: {outcome}. {error} {cleanupError}".TrimEnd());
        }
        return outcome == "passed" ? 0 : 1;
    }

    private bool ConnectedBeforeCut(Sample s) => s.Host.Connected && s.Host.Disconnects == initial!.Disconnects
        && s.Host.Reconnects == initial.Reconnects;

    private static bool RearTrigger(Sample s, ExtraButtons rear, ushort button, bool right)
    {
        var state = s.Host.State;
        return !s.Host.WaitingForNeutral && s.Host.Extras == rear && state.Buttons == button && s.Pad.Buttons == button
            && (right ? state.Rt >= 220 && s.Pad.Rt >= 220 && state.Lt <= 2 && s.Pad.Lt <= 2
                      : state.Lt >= 220 && s.Pad.Lt >= 220 && state.Rt <= 2 && s.Pad.Rt <= 2);
    }

    private static bool AtRest(XInputReading pad) => pad.Buttons == 0 && pad.Lt <= 2 && pad.Rt <= 2
        && Math.Abs((int)pad.Lx) <= 2500 && Math.Abs((int)pad.Ly) <= 2500
        && Math.Abs((int)pad.Rx) <= 2500 && Math.Abs((int)pad.Ry) <= 2500;

    private static bool ExactlyNeutral(XInputReading p) =>
        p.Buttons == 0 && p.Lt == 0 && p.Rt == 0 && p.Lx == 0 && p.Ly == 0 && p.Rx == 0 && p.Ry == 0;

    private async Task SwitchAsync(string name, CancellationToken token)
    {
        // Cleanup can restore the original selection even when a successful switch loses its reply.
        activeProfile = name;
        changed = true;
        var status = await SessionControl.SendAsync("switch", name, token);
        if (status.SessionId != initial!.SessionId || status.Profile != name)
            throw new IOException($"The active converter did not confirm switching to '{name}'.");
    }

    private async Task<Sample> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var status = await SessionControl.SendAsync("status", null, token);
        var all = XInputReader.ReadAll();
        int[] slots = all.Keys.Order().ToArray();
        if (status.SessionId != initial!.SessionId || status.Slot != initial.Slot)
            throw new IOException("The converter session or its XInput slot changed during the check.");
        if (!slots.SequenceEqual(initialSlots))
            throw new IOException($"XInput slot set changed from [{string.Join(",", initialSlots)}] to [{string.Join(",", slots)}].");
        if (status.Profile != activeProfile)
            throw new IOException("A different client changed the active profile during verification.");
        ++pollCount;
        latest = new(DateTimeOffset.UtcNow, status, slots, all[initial.Slot]);
        return latest;
    }

    private async Task<Sample> WaitAsync(Func<Sample, bool> matches, int stableMs, CancellationToken token, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        TimeSpan? since = null;
        while (timer.Elapsed <= (timeout ?? GestureTimeout))
        {
            var sample = await ReadAsync(token);
            if (matches(sample))
            {
                since ??= timer.Elapsed;
                if ((timer.Elapsed - since.Value).TotalMilliseconds >= stableMs) return sample;
            }
            else since = null;
            if (lastSave.Elapsed >= TimeSpan.FromSeconds(2)) Save();
            await Task.Delay(50, token);
        }
        throw new TimeoutException($"Timed out waiting for '{step}'. {instruction}");
    }

    private void SetStep(string id, string prompt)
    {
        step = id;
        instruction = prompt;
        Console.WriteLine($"{id}: {prompt}");
        Save();
    }

    private void Mark()
    {
        evidence.Add(new(step, latest!));
        Save();
    }

    private void Save()
    {
        var report = new { startedUtc = started, source = "physical Bluetooth 85HF and independent Windows XInputGetState",
            outcome, step, instruction, initialProfile = initial?.Profile, initialSlots, pollCount,
            offlineNeutralSamples, heldAtRemoval, profileRestored, latest, evidence, error, cleanupError };
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
        lastSave.Restart();
    }
}
