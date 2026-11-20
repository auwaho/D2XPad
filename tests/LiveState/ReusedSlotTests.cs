using D2XPad.Live;
using D2XPad.SeriesProbe;

internal static class ReusedSlotTests
{
    internal static async Task RunAsync()
    {
        foreach (var scenario in new[] { "reused", "late startup", "concurrent arrival", "discard first",
                     "ambiguous", "unrelated", "stale packet", "stuck trigger", "switch slot", "cancel" })
        {
            var sent = new List<LiveState>();
            uint packet = 0;
            int readsSinceChange = 0;
            var state = LiveState.Neutral;
            using var cancel = new CancellationTokenSource();

            void Submit(LiveState next)
            {
                sent.Add(next);
                if (scenario == "cancel" && next != LiveState.Neutral) cancel.Cancel();
                if (!(scenario == "stuck trigger" && next == LiveState.Neutral || scenario == "discard first" && sent.Count == 1))
                    state = next;
                packet++;
                readsSinceChange = 0;
            }

            IReadOnlyDictionary<int, XInputReading> Read()
            {
                readsSinceChange++;
                var quiet = new XInputReading(0, 0, 0, 0, 0, 0, 0, 0);
                var changing = quiet with { Packet = scenario == "stale packet" ? 0 : packet, Lt = state.Lt, Rt = state.Rt };
                bool moved = scenario == "switch slot" && sent.Count > 1;
                var snapshot = new Dictionary<int, XInputReading>();
                if (scenario != "late startup" && (scenario != "concurrent arrival" || sent.Count > 0))
                    snapshot[0] = scenario == "ambiguous" || moved ? changing : quiet;
                bool lateStart = scenario is "late startup" or "concurrent arrival";
                if (!lateStart || sent.Count > 1 || sent.Count == 1 && readsSinceChange >= 5)
                    snapshot[2] = scenario == "unrelated" || moved ? quiet : changing;
                return snapshot;
            }

            bool expectedOwnership = scenario is "reused" or "late startup" or "concurrent arrival" or "discard first";
            int? slot = null;
            try
            {
                var timeout = TimeSpan.FromMilliseconds(220);
                slot = await XInputSlotCheck.IdentifyAsync(Submit, Read, new Random(31), 2 * timeout, timeout, cancel.Token);
            }
            catch (Exception ex) when (!expectedOwnership && ex is IOException or TimeoutException or OperationCanceledException) { }
            if (slot.HasValue != expectedOwnership || sent.LastOrDefault() != LiveState.Neutral)
                throw new InvalidDataException($"Failed XInput slot ownership test: {scenario}.");
            if (slot.HasValue && (slot != 2 || readsSinceChange < 3))
                throw new InvalidDataException("Missing a unique, released output slot.");
            int expectedReports = scenario == "discard first" ? 5 : 4;
            if (expectedOwnership && (sent.Count != expectedReports || sent.SkipLast(1).Any(s =>
                    s.Lt is 0 or >= 30 || s.Rt is 0 or >= 30 || s != (LiveState.Neutral with { Lt = s.Lt, Rt = s.Rt }))))
                throw new InvalidDataException("Identification used a control outside its trigger range.");
        }
        Console.WriteLine("Passed XInput slot ownership for delayed and reused slots, concurrent hotplug, ambiguity, cancellation and final neutral.");
    }
}
