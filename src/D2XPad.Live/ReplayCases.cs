using System.Text.Json;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

internal sealed record ReplayCase(string Id, byte[] Report, XInputExpectation Expected);

internal static class ReplayCases
{
    // The capture's action labels supply the expected control. Sample selection and
    // expectations use physical bytes, independently of the live decoder and SDK.
    public static IReadOnlyList<ReplayCase> Load(string path)
    {
        var steps = ReadReports(path);
        var cases = new List<ReplayCase>();

        void Add(string id, string step, Func<byte[], bool> eligible, Func<byte[], int> distance,
            ushort expectedButtons = 0)
        {
            if (!steps.TryGetValue(step, out var reports) || reports.Where(eligible).MinBy(distance) is not { } r)
                throw new InvalidDataException($"No suitable 85HF report for '{id}' in capture step '{step}'.");
            cases.Add(new(id, r, CapturedExpectation.FromRaw(r, expectedButtons)));
        }

        void Digital(string id, string step, ushort expected = 0, byte face = 0, byte menu = 0,
            byte rear = 0, byte hat = 15) =>
            Add(id, step, r => (r[1] & 15) == hat && r[8] == face && r[9] == menu && r[10] == rear
                && r[6] == 0 && r[7] == 0, CenterDistance, expected);

        Digital("neutral", "button_a");
        foreach (byte value in new byte[] { 64, 128, 192, 255 })
        {
            Add($"lt-{value}", "lt_sweep", r => AnalogOnly(r) && r[6] == 0 && Math.Abs(r[7] - value) <= 6,
                r => Math.Abs(r[7] - value));
            Add($"rt-{value}", "rt_sweep", r => AnalogOnly(r) && r[7] == 0 && Math.Abs(r[6] - value) <= 6,
                r => Math.Abs(r[6] - value));
        }
        Add("both-full", "both_full", r => AnalogOnly(r) && r[7] == 255 && r[6] == 255, CenterDistance);
        Add("lt-low-rt-high", "lt_low_rt_high", r => AnalogOnly(r) && r[7] is > 16 and < 128 && r[6] > 160,
            r => Math.Abs(r[7] - 64) + Math.Abs(r[6] - 192));
        Add("lt-high-rt-low", "lt_high_rt_low", r => AnalogOnly(r) && r[7] > 160 && r[6] is > 16 and < 128,
            r => Math.Abs(r[7] - 192) + Math.Abs(r[6] - 64));
        foreach (var (stick, x) in new[] { ("ls", 2), ("rs", 4) })
        {
            foreach (var (label, axis, endpoint) in new[] { ("left", x, 0), ("right", x, 255),
                ("up", x + 1, 0), ("down", x + 1, 255) })
            {
                Add($"{stick}-{label}", $"{stick}_{label}",
                    r => AnalogOnly(r) && r[6] == 0 && r[7] == 0 && r[axis] == endpoint, CenterDistance);
            }
        }

        Digital("a", "button_a", 0x1000, face: 1);
        Digital("b", "button_b", 0x2000, face: 2);
        Digital("x", "button_x", 0x4000, face: 8);
        Digital("y", "button_y", 0x8000, face: 16);
        Digital("lb", "button_lb", 0x0100, face: 64);
        Digital("rb", "button_rb", 0x0200, face: 128);
        Digital("back", "button_select", 0x0020, menu: 4);
        Digital("start", "button_start", 0x0010, menu: 8);
        Digital("l3", "button_l3", 0x0040, menu: 32);
        Digital("r3", "button_r3", 0x0080, menu: 64);
        Digital("dpad-up", "dpad_up", 1, hat: 0);
        Digital("dpad-right", "dpad_right", 8, hat: 2);
        Digital("dpad-down", "dpad_down", 2, hat: 4);
        Digital("dpad-left", "dpad_left", 4, hat: 6);
        Digital("l4-to-lb", "button_l4", 0x0100, rear: 1);
        Digital("r4-to-rb", "button_r4", 0x0200, rear: 2);
        Digital("pl-to-a", "button_pl", 0x1000, face: 32);
        Digital("pr-to-b", "button_pr", 0x2000, face: 4);

        // Native-only releases are taken from the isolated native-button steps.
        // Every state is an original packet; the selected sequence is assembled.
        Digital("pl-held", "pl_a", 0x1000, face: 32);
        Digital("pl-and-a-held", "pl_a", 0x1000, face: 33);
        Digital("a-still-held", "button_a", 0x1000, face: 1);
        Digital("a-released", "button_a");
        Digital("r4-held", "r4_rb", 0x0200, rear: 2);
        Digital("r4-and-rb-held", "r4_rb", 0x0200, face: 128, rear: 2);
        Digital("rb-still-held", "button_rb", 0x0200, face: 128);
        Digital("release", "button_rb");
        return cases;
    }

    private static int CenterDistance(byte[] r) =>
        Math.Abs(r[2] - 128) + Math.Abs(r[3] - 128) + Math.Abs(r[4] - 128) + Math.Abs(r[5] - 128);

    // Low two menu bits mirror trigger clicks; only their independent analog fields are exported.
    private static bool AnalogOnly(byte[] r) => (r[1] & 15) == 15 && r[8] == 0 && (r[9] & ~3) == 0 && r[10] == 0;

    private static Dictionary<string, List<byte[]>> ReadReports(string path)
    {
        using var input = File.OpenText(path);
        using var header = JsonDocument.Parse(input.ReadLine() ?? throw new InvalidDataException("The capture is empty."));
        var root = header.RootElement;
        if (root.GetProperty("type").GetString() != "session" || root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Expected a stage 1A schema version 1 capture.");
        var sources = root.GetProperty("collections").EnumerateArray().Where(c =>
            c.GetProperty("vid").GetInt32() == 0x2dc8 && c.GetProperty("pid").GetInt32() == 0x2067
            && c.GetProperty("usagePage").GetInt32() == 1 && c.GetProperty("usage").GetInt32() == 5
            && c.GetProperty("inputReportLength").GetInt32() == 32 && c.GetProperty("transport").GetString() == "Bluetooth"
            && c.TryGetProperty("inputReportIds", out var ids) && ids.ValueKind == JsonValueKind.Array
            && ids.EnumerateArray().Any(id => id.GetInt32() == 1)).ToArray();
        if (sources.Length != 1)
            throw new InvalidDataException("The capture must identify exactly one matching 85HF Bluetooth gamepad collection.");
        string collection = sources[0].GetProperty("id").GetString()!;
        var steps = new Dictionary<string, List<byte[]>>();
        while (input.ReadLine() is { } line)
        {
            using var entry = JsonDocument.Parse(line);
            var data = entry.RootElement;
            if (data.GetProperty("type").GetString() != "report" || data.GetProperty("collection").GetString() != collection
                || data.GetProperty("step").GetString() is not { } step)
                continue;
            byte[] bytes = Convert.FromHexString(data.GetProperty("hex").GetString()!);
            if (bytes.Length != 32 || bytes[0] != 1) continue;
            if (!steps.TryGetValue(step, out var reports)) steps.Add(step, reports = []);
            reports.Add(bytes);
        }
        return steps;
    }
}

// The live gesture and capture replay checks use the same consumer coordinates.
// Button expectations come from the named action, independent of the remapper.
internal static class CapturedExpectation
{
    public static XInputExpectation FromRaw(ReadOnlySpan<byte> raw, ushort buttons) =>
        new(raw[7], raw[6], SignedAxis(raw[2]), SignedAxis(raw[3], vertical: true),
            SignedAxis(raw[4]), SignedAxis(raw[5], vertical: true), buttons);

    private static short SignedAxis(byte raw, bool vertical = false)
    {
        double fraction = (raw - 128) / (raw < 128 ? 128d : 127d);
        if (vertical) fraction = -fraction;
        return (short)Math.Round(fraction * (fraction < 0 ? 32768 : 32767));
    }
}
