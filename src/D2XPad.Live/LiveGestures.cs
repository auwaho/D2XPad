using System.Text.Json;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

internal sealed record Gesture(string Id, string Prompt, Func<byte[], bool> Matches, ushort Buttons = 0);

internal static class LiveGestures
{
    // Read the physical packet directly so a decoder error cannot satisfy its own check.
    private static bool Centered(byte[] r) => Enumerable.Range(2, 4).All(i => Math.Abs(r[i] - 128) <= 9);
    private static bool Released(byte[] r) => (r[1] & 15) == 15 && r[8] == 0 && (r[9] & 0x7c) == 0 && (r[10] & 3) == 0;
    private static bool AtRest(byte[] r) => Released(r) && Centered(r) && r[6] <= 2 && r[7] <= 2;

    public static IReadOnlyList<Gesture> Create(JsonElement bindings)
    {
        var rest = new Gesture("release", "Release all controls and center both sticks.", AtRest);
        var items = new List<Gesture> { rest with { Id = "start", Prompt = "Press and release A on the 85HF, then leave both sticks centered." } };
        void Trigger(string id, string prompt, Func<int,int,bool> depth) =>
            items.Add(new(id, prompt, r => Centered(r) && Released(r) && depth(r[7], r[6])));
        Trigger("lt-partial", "Hold LT about halfway. Leave RT released.", (l,r) => l is >= 55 and <= 200 && r <= 2);
        Trigger("lt-full", "Pull LT all the way. Leave RT released.", (l,r) => l >= 249 && r <= 2);
        Trigger("rt-partial", "Release LT. Hold RT about halfway.", (l,r) => l <= 2 && r is >= 55 and <= 200);
        Trigger("rt-full", "Pull RT all the way. Leave LT released.", (l,r) => l <= 2 && r >= 249);
        Trigger("lt-low-rt-high", "Hold RT all the way. Squeeze LT to about one third (0.15–0.50).", (l,r) => l is > 12 and < 145 && r > 165);
        Trigger("lt-high-rt-low", "Hold LT all the way. Ease RT to about one third (0.15–0.50).", (l,r) => l > 165 && r is > 12 and < 145);
        Trigger("both-full", "Pull both triggers all the way.", (l,r) => l >= 249 && r >= 249);
        foreach (var (stick, x, y) in new[] { ("left", 2, 3), ("right", 4, 5) })
        {
            foreach (var (dir, axis, low) in new[] { ("left", x, true), ("right", x, false), ("up", y, true), ("down", y, false) })
                items.Add(new($"{stick}-stick-{dir}", $"Release both triggers. Hold the {stick} stick fully {dir}.",
                    r => Released(r) && r[6] <= 2 && r[7] <= 2 && (low ? r[axis] < 14 : r[axis] > 241)
                        && Enumerable.Range(2, 4).Where(i => i != axis).All(i => Math.Abs(r[i] - 128) < 55)));
        }
        foreach (var (source, offset, mask) in new[] { ("L4", 10, 1), ("R4", 10, 2), ("PL", 8, 32), ("PR", 8, 4) })
        {
            var target = bindings.GetProperty(source).GetString()!;
            var expected = TargetMask(target);
            items.Add(new(source, $"Center the sticks. Hold only {source} (mapped to {target}).",
                r => r[offset] == mask && r[offset == 8 ? 10 : 8] == 0 && (r[9] & 0x7c) == 0
                    && (r[1] & 15) == 15 && Centered(r) && r[6] <= 2 && r[7] <= 2, expected));
            items.Add(rest with { Id = $"release-{source}" });
        }
        return items;
    }

    private static ushort TargetMask(string target) => target.ToUpperInvariant() switch
    {
        "NONE" => 0, "DPADUP" => 1, "DPADDOWN" => 2, "DPADLEFT" => 4, "DPADRIGHT" => 8,
        "START" => 16, "BACK" => 32, "L3" => 64, "R3" => 128, "LB" => 256, "RB" => 512,
        "A" => 4096, "B" => 8192, "X" => 16384, "Y" => 32768,
        "GUIDE" => throw new ArgumentException("XInputGetState cannot verify Guide. Choose an observable button for the live check."),
        _ => throw new ArgumentException($"Unknown binding {target}.")
    };

    public static XInputExpectation Expected(byte[] raw, Gesture g) => CapturedExpectation.FromRaw(raw, g.Buttons);
}
