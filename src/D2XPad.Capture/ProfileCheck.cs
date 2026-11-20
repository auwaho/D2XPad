using System.Text.Json;

namespace D2XPad.Capture;

internal static class ProfileCheck
{
    private static readonly string[] Buttons =
        ["a", "b", "x", "y", "lb", "rb", "select", "start", "home", "l3", "r3", "l4", "r4", "pl", "pr"];
    private static readonly string[] Directions =
        ["up", "up_right", "right", "down_right", "down", "down_left", "left", "up_left"];
    private static readonly string[] Axes = ["lx", "ly", "rx", "ry"];

    public static int Run(string capturePath, TextWriter output)
    {
        try
        {
            return Analyze(capturePath, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            output.WriteLine($"Could not verify the capture. {ex.Message}");
            return 2;
        }
    }

    private static int Analyze(string path, TextWriter output)
    {
        using var lines = File.ReadLines(path).GetEnumerator();
        Require(lines.MoveNext(), "The capture file is empty.");
        using var first = JsonDocument.Parse(lines.Current);
        var header = first.RootElement;
        Require(Text(header, "type") == "session" && Int(header, "schemaVersion") == 1,
            "Expected a D2XPad capture with schema version 1.");
        var map = new Map(header.GetProperty("profile"));
        var digest = Text(header, "profileSha256");
        output.WriteLine($"Checking the 85HF against the profile saved in this capture · SHA-256 {digest}");
        var sources = header.GetProperty("collections").EnumerateArray()
            .Select(device => Text(device, "id")).ToHashSet(StringComparer.Ordinal);
        var streams = new Dictionary<(string Collection, int Length), Candidate[]>();
        var finished = new HashSet<string>(StringComparer.Ordinal);
        string? active = null;
        bool? completed = null;
        long reportCount = 0;
        int lineNumber = 1;

        while (lines.MoveNext())
        {
            lineNumber++;
            Require(completed is null, $"Found data after the session ended on line {lineNumber}.");
            using var line = JsonDocument.Parse(lines.Current);
            var item = line.RootElement;
            switch (Text(item, "type"))
            {
                case "step":
                    var name = Text(item, "step");
                    var phase = Text(item, "phase");
                    Require(Guide.StepIds.Contains(name), $"Unknown step {name}.");
                    if (phase == "start" && active is null && !finished.Contains(name))
                    {
                        active = name;
                        if (name is "neutral" or "neutral_end")
                            foreach (var c in streams.Values.SelectMany(c => c)) c.BeginNeutral(name);
                    }
                    else if (phase == "end" && active == name)
                    {
                        finished.Add(name);
                        active = null;
                    }
                    else
                        throw new InvalidDataException($"Unexpected marker for step {name} on line {lineNumber}.");
                    break;
                case "report":
                    var source = Text(item, "collection");
                    Require(sources.Contains(source), $"Unknown HID collection {source}.");
                    var bytes = Convert.FromHexString(Text(item, "hex"));
                    Require(bytes.Length > 0, $"Empty HID report on line {lineNumber}.");
                    reportCount++;
                    var step = item.GetProperty("step").GetString();
                    if (step == "idle") step = null;
                    Require(step == active, $"The report's step marker does not match the active step on line {lineNumber}.");
                    var key = (source, bytes.Length);
                    if (!streams.TryGetValue(key, out var candidates))
                    {
                        candidates = new[] { -1, 0, 1 }.Where(shift => map.Fits(bytes.Length, shift))
                            .Select(shift => new Candidate(source, bytes.Length, shift)).ToArray();
                        streams.Add(key, candidates);
                    }
                    foreach (var candidate in candidates)
                        candidate.Observe(step, map.Decode(bytes, candidate.Shift));
                    break;
                case "end":
                    completed = item.GetProperty("completed").GetBoolean();
                    if (item.TryGetProperty("reports", out var expectedCount))
                        Require(expectedCount.GetInt64() == reportCount, "The number of HID reports differs from the count in the session footer.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown record type on line {lineNumber}.");
            }
        }

        bool fullSession = completed == true && active is null && finished.SetEquals(Guide.StepIds);
        output.WriteLine($"Raw reports received: {reportCount}. Steps completed: {finished.Count}/{Guide.StepIds.Count}.");
        if (!fullSession)
            output.WriteLine("The session is incomplete. Results are available for the recorded steps.");
        if (streams.Count == 0)
            output.WriteLine("No HID reports were captured. Check the selected device and its connection.");

        var ranked = streams.Values.SelectMany(c => c).OrderByDescending(c => c.Passed).ToList();
        foreach (var (key, candidates) in streams)
        {
            if (candidates.Length == 0)
                output.WriteLine($"{key.Collection}, {key.Length} bytes — report too short for this profile.");
            else
                foreach (var c in candidates.OrderBy(c => c.Shift))
                    output.WriteLine($"{c.Label} — matched {c.Passed}/{Guide.StepIds.Count} steps.");
        }
        if (ranked.Count == 0) return 1;
        var best = ranked[0];
        output.WriteLine($"Results for {best.Label}. The offset is added to each byte index in the profile.");
        foreach (var id in Guide.StepIds)
        {
            if (!best.Steps.TryGetValue(id, out var check))
                output.WriteLine($"  [?] {id} — no reports during this step.");
            else
                output.WriteLine($"  [{(check.Pass ? "+" : "×")}] {id} — {check.Describe()}");
        }
        if (ranked.Count > 1 && ranked[1].Passed == best.Passed)
        {
            output.WriteLine("Multiple candidates matched the same number of steps. The collection and offset are ambiguous.");
            return 1;
        }
        bool verified = fullSession && best.Passed == Guide.StepIds.Count;
        output.WriteLine(verified
            ? "This capture confirms every profile check."
            : "Some profile checks remain unconfirmed. Review the marked steps and the neutral state after release.");
        return verified ? 0 : 1;
    }

    private sealed class Candidate(string source, int length, int shift)
    {
        public int Shift => shift;
        public string Label => $"{source}, {length} bytes, offset {shift:+0;-0;0}";
        public Dictionary<string, Evidence> Steps { get; } = new();
        public int Passed => Steps.Values.Count(s => s.Pass);
        private Frame? previous;
        public void BeginNeutral(string id)
        {
            if (previous is { } last) Steps[id] = new Evidence(id, last);
        }
        public void Observe(string? id, Frame f)
        {
            previous = f;
            if (id is null) return;
            if (!Steps.TryGetValue(id, out var e)) Steps[id] = e = new Evidence(id);
            e.Add(f);
        }
    }

    private readonly record struct Frame(int Keys, int Hat, int Lx, int Ly, int Rx, int Ry, int Lt, int Rt)
    {
        public bool QuietControls => Keys == 0 && Hat >= 8 && Lt <= 12 && Rt <= 12;
        public bool SticksAtRest => Centered(Lx) && Centered(Ly) && Centered(Rx) && Centered(Ry);
        public bool Rest => QuietControls && SticksAtRest;
        public int Axis(int i) => i switch { 0 => Lx, 1 => Ly, 2 => Rx, 3 => Ry, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
        private static bool Centered(int value) => value is >= 96 and <= 160;
    }

    // The profile is UR-XD's unnumbered, single-report, 8-bit 85HF layout.
    // Report prefixes are tested on the original buffer, never stripped in the capture.
    private sealed class Map
    {
        private readonly (int Index, int Mask)[] keys;
        private readonly (int Index, bool Flip)[] axes;
        private readonly int hat, lt, rt, min, max;

        public Map(JsonElement profile)
        {
            Require(string.Equals(Text(profile, "vid"), "2DC8", StringComparison.OrdinalIgnoreCase)
                && Text(profile, "pid") == "2067" && !Flag(profile, "has_report_id", true),
                "Expected a 2DC8:2067 profile with unnumbered reports.");
            var reports = profile.GetProperty("reports");
            Require(reports.EnumerateObject().Count() == 1 && reports.TryGetProperty("0", out _),
                "Expected a single report keyed by 0.");
            var inputs = reports.GetProperty("0").GetProperty("inputs");

            (JsonElement Field, int Index) Field(string name, string type)
            {
                var field = inputs.GetProperty(name);
                var index = Int(field, "byte");
                Require(Text(field, "type") == type && index is >= 0 and < 65535
                    && (!field.TryGetProperty("length", out var len) || len.GetInt32() == 1)
                    && !Flag(field, "signed") && !field.TryGetProperty("bitshift", out _)
                    && (type == "button" || !field.TryGetProperty("bitmask", out _))
                    && !field.TryGetProperty("deadzone", out _), $"Unsupported field definition for {name}.");
                return (field, index);
            }
            keys = Buttons.Select(name =>
            {
                var (field, index) = Field(name, "button");
                int mask = Int(field, "bitmask");
                Require(mask is > 0 and <= 255 && (mask & (mask - 1)) == 0, $"Expected a single-bit mask for {name}.");
                return (index, mask);
            }).ToArray();
            axes = Axes.Select((name, i) =>
            {
                var (field, index) = Field(name, "axis");
                Require(Flag(field, "center"), $"Expected a centered axis for {name}.");
                // UR-XD reverses centered unsigned Y first, then applies the profile's invert flag.
                return (index, (i is 1 or 3) ^ Flag(field, "invert"));
            }).ToArray();
            hat = Field("dpad", "hat").Index;
            lt = Field("lt", "trigger").Index;
            rt = Field("rt", "trigger").Index;
            var indexes = keys.Select(k => k.Index).Concat(axes.Select(a => a.Index)).Concat([hat, lt, rt]).ToArray();
            min = indexes.Min();
            max = indexes.Max();
        }
        public bool Fits(int length, int shift) => min + shift >= 0 && max + shift < length;
        public Frame Decode(byte[] data, int shift)
        {
            int bits = 0;
            for (int i = 0; i < keys.Length; i++)
                if ((data[keys[i].Index + shift] & keys[i].Mask) != 0) bits |= 1 << i;
            int Axis(int i) => axes[i].Flip ? 255 - data[axes[i].Index + shift] : data[axes[i].Index + shift];
            return new Frame(bits, data[hat + shift] & 15, Axis(0), Axis(1), Axis(2), Axis(3), data[lt + shift], data[rt + shift]);
        }
    }

    private enum Kind { Neutral, Keys, Hat, Axis, Sweep, Together }
    private sealed class Evidence
    {
        private readonly string id;
        private readonly Kind kind;
        private readonly int target;
        private readonly bool high;
        private bool hit, interference, rest;
        private int count, min = 255, max, peakOther, keyUnion, pairProgress, sweepProgress;
        private readonly int[] keyOrder = [];
        private readonly Frame? preceding;
        private int pairedLt, pairedRt;
        private readonly HashSet<int> values = [];

        public Evidence(string id, Frame? preceding = null)
        {
            this.id = id;
            this.preceding = preceding;
            int Key(string name) => 1 << Array.IndexOf(Buttons, name);
            if (id.StartsWith("button_")) { kind = Kind.Keys; target = Key(id[7..]); }
            else if (id is "pl_a" or "r4_rb")
            {
                kind = Kind.Keys;
                int first = id == "pl_a" ? Key("pl") : Key("r4");
                int second = id == "pl_a" ? Key("a") : Key("rb");
                target = first | second;
                keyOrder = [first, target, second];
            }
            else if (id.StartsWith("dpad_")) { kind = Kind.Hat; target = Array.IndexOf(Directions, id[5..]); }
            else if (id.StartsWith("ls_") || id.StartsWith("rs_"))
            {
                kind = Kind.Axis;
                target = (id[0] == 'r' ? 2 : 0) + (id[3..] is "up" or "down" ? 1 : 0);
                high = id[3..] is "up" or "right";
            }
            else if (id.EndsWith("_sweep")) kind = Kind.Sweep;
            else if (id is "both_full" or "lt_low_rt_high" or "lt_high_rt_low") kind = Kind.Together;
            else if (id is "neutral" or "neutral_end") kind = Kind.Neutral;
            else throw new InvalidDataException($"Unknown action {id}.");
        }

        public bool Pass => count == 0 ? preceding?.Rest == true
            : hit && rest && !interference && pairProgress == keyOrder.Length
            && (kind != Kind.Sweep || (values.Count >= 8 && sweepProgress == 3));

        public void Add(Frame f)
        {
            count++;
            rest = f.Rest;
            switch (kind)
            {
                case Kind.Neutral:
                    hit |= rest;
                    interference |= !rest;
                    break;
                case Kind.Keys:
                    keyUnion |= f.Keys;
                    hit |= f.Keys == target;
                    if (pairProgress < keyOrder.Length && f.Keys == keyOrder[pairProgress]) pairProgress++;
                    interference |= (f.Keys & ~target) != 0 || f.Hat < 8 || f.Lt > 12 || f.Rt > 12;
                    // A stick click can move its own shaft. Check stick travel separately for those two steps.
                    if (id is not ("button_l3" or "button_r3")) interference |= !f.SticksAtRest;
                    break;
                case Kind.Hat:
                    values.Add(f.Hat);
                    hit |= f.Hat == target;
                    interference |= f.Keys != 0 || f.Lt > 12 || f.Rt > 12 || !f.SticksAtRest;
                    if (f.Hat < 8 && Math.Abs(f.Hat - target) is > 1 and < 7) interference = true;
                    break;
                case Kind.Axis:
                    var axis = f.Axis(target);
                    Range(axis);
                    hit |= high ? axis >= 207 : axis <= 48;
                    interference |= !f.QuietControls || (high ? axis <= 48 : axis >= 207);
                    for (int i = 0; i < 4; i++)
                        if (i != target && Math.Abs(f.Axis(i) - 128) > 64) interference = true;
                    break;
                case Kind.Sweep:
                    bool left = id == "lt_sweep";
                    int value = left ? f.Lt : f.Rt;
                    Range(value);
                    values.Add(value);
                    if ((sweepProgress, value) is (0, >= 30 and <= 94) or (1, >= 95 and <= 160) or (2, >= 161 and <= 225))
                        sweepProgress++;
                    peakOther = Math.Max(peakOther, left ? f.Rt : f.Lt);
                    hit |= value >= 235 && sweepProgress == 3;
                    interference |= peakOther > 12 || f.Hat < 8 || f.Keys != 0 || !f.SticksAtRest;
                    break;
                case Kind.Together:
                    bool match = id switch
                    {
                        "both_full" => f.Lt >= 235 && f.Rt >= 235,
                        "lt_low_rt_high" => Low(f.Lt) && High(f.Rt),
                        _ => High(f.Lt) && Low(f.Rt)
                    };
                    if (match && !hit) (pairedLt, pairedRt) = (f.Lt, f.Rt);
                    hit |= match;
                    interference |= f.Hat < 8 || f.Keys != 0 || !f.SticksAtRest;
                    break;
            }
        }
        private static bool Low(int v) => v is >= 25 and <= 110;
        private static bool High(int v) => v is >= 150 and <= 232;
        private void Range(int v) { min = Math.Min(min, v); max = Math.Max(max, v); }
        public string Describe()
        {
            if (count == 0)
                return preceding?.Rest == true
                    ? "no new reports, last state of this collection before the step was neutral"
                    : "no new reports, last observed state was outside neutral";
            string result = kind switch
            {
                Kind.Keys => $"buttons {string.Join("+", Buttons.Where((_, i) => (keyUnion & (1 << i)) != 0))}",
                Kind.Axis => $"{Axes[target]} axis {min}…{max} (after applying axis direction)",
                Kind.Sweep => $"range {min}…{max}, {values.Count} distinct values, {sweepProgress}/3 intermediate levels in order, other trigger peaked at {peakOther}",
                Kind.Hat => $"hat {string.Join(",", values.Order())}",
                Kind.Together when hit => $"both held at LT={pairedLt}, RT={pairedRt}",
                Kind.Together => "expected LT/RT combination never observed",
                _ => $"neutral position, {count} reports"
            };
            if (!hit && kind != Kind.Together) result += ", expected position never reached";
            if (pairProgress != keyOrder.Length) result += $", button transitions in order {pairProgress}/{keyOrder.Length}";
            if (kind != Kind.Neutral) result += $", {count} reports";
            if (interference) result += ", unexpected movement or button press";
            if (!rest) result += ", last state was outside neutral";
            return result;
        }
    }

    private static int Int(JsonElement obj, string name) => obj.GetProperty(name).GetInt32();
    private static string Text(JsonElement obj, string name) =>
        obj.GetProperty(name).GetString() ?? throw new InvalidDataException($"Field {name} is empty.");
    private static bool Flag(JsonElement obj, string name, bool fallback = false) =>
        obj.TryGetProperty(name, out var prop) ? prop.GetBoolean() : fallback;
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }
}
