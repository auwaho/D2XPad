using System.Diagnostics;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

internal static class XInputSlotCheck
{
    // A new slot could belong to another controller, and Windows can reuse an
    // already readable slot. Confirm that our host controls the same slot throughout.
    internal static async Task<int> IdentifyAsync(Action<LiveState> submit, Func<IReadOnlyDictionary<int, XInputReading>> read,
        Random random, TimeSpan startupTimeout, TimeSpan stepTimeout, CancellationToken token)
    {
        Dictionary<int, uint> priorPackets = [];
        int? claimed = null;
        bool lowOnLeft = random.Next(2) == 0;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var (slot, packet) = i == 0 ? await DiscoverAsync() : await ChallengeAsync(stepTimeout);
                claimed = slot;
                priorPackets = new Dictionary<int, uint> { [slot] = packet };
            }
        }
        finally { submit(LiveState.Neutral); }

        var released = await ObserveAsync(0, 0, stepTimeout);
        return released.Slot;

        async Task<(int Slot, uint Packet)> DiscoverAsync()
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var remaining = startupTimeout - timer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("No XInput slot followed the virtual Xbox trigger sequence.");
                priorPackets = read().ToDictionary(p => p.Key, p => p.Value.Packet);
                try { return await ChallengeAsync(remaining < stepTimeout ? remaining : stepTimeout); }
                catch (TimeoutException)
                {
                    // A driver can discard reports submitted before its XInput interface
                    // becomes visible. The next attempt submits a different pose.
                }
            }
        }

        Task<(int Slot, uint Packet)> ChallengeAsync(TimeSpan timeout)
        {
            token.ThrowIfCancellationRequested();
            // Both axes stay below XINPUT_GAMEPAD_TRIGGER_THRESHOLD (30).
            byte low = (byte)random.Next(3, 13), high = (byte)random.Next(17, 28);
            var (lt, rt) = lowOnLeft ? (low, high) : (high, low);
            lowOnLeft = !lowOnLeft;
            submit(LiveState.Neutral with { Lt = lt, Rt = rt });
            return ObserveAsync(lt, rt, timeout);
        }

        async Task<(int Slot, uint Packet)> ObserveAsync(byte lt, byte rt, TimeSpan timeout)
        {
            bool releasing = lt == 0 && rt == 0;
            var timer = Stopwatch.StartNew();
            int? lastMatch = null;
            int stable = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                var snapshot = read();
                if (claimed is { } candidate && !snapshot.ContainsKey(candidate))
                    throw new IOException("The candidate XInput slot disconnected during identification. Start conversion again.");
                var matches = snapshot.Where(p => (!releasing || p.Key == claimed) && Matches(p.Value, lt, rt)).ToArray();
                if (matches.Length > 1)
                    throw new IOException("Multiple XInput slots matched the virtual Xbox trigger state. The controller was released.");
                if (matches is [{ } match] && (claimed == null || match.Key == claimed) &&
                    (!priorPackets.TryGetValue(match.Key, out uint previous) || match.Value.Packet != previous))
                {
                    stable = lastMatch == match.Key ? stable + 1 : 1;
                    lastMatch = match.Key;
                    if (stable >= 3) return (match.Key, match.Value.Packet);
                }
                else { stable = 0; lastMatch = null; }
                await Task.Delay(10, token);
            } while (timer.Elapsed < timeout);
            throw new TimeoutException(releasing
                ? "The identified virtual Xbox slot did not return to neutral."
                : "No XInput slot followed the virtual Xbox trigger sequence.");
        }
    }

    private static bool Matches(XInputReading p, byte lt, byte rt) =>
        p.Buttons == 0 && p.Lx == 0 && p.Ly == 0 && p.Rx == 0 && p.Ry == 0 &&
        (lt == 0 && rt == 0 ? p.Lt == 0 && p.Rt == 0 : Math.Abs(p.Lt - lt) <= 1 && Math.Abs(p.Rt - rt) <= 1);
}
