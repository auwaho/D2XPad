using D2XPad.Live;

internal static class MappingTests
{
    public static void Run()
    {
        var layout = DeviceLayout.Bluetooth85HF;
        var report = new byte[32];
        report[0] = 1;
        report[1] = 15;
        report.AsSpan(2, 4).Fill(128);
        PadBindings Map(params InputMapping[] rows) => PadBindings.CreateMappings(layout, rows);
        LiveState Read(PadBindings map)
        {
            if (!map.TryDecode(report, out var state, out _)) throw new InvalidDataException("Expected an 85HF report.");
            return state;
        }
        var chord = Map(new("extra:L4", [new("RT"), new("LeftStickUp", 75), new("A")]),
            new("extra:R4", [new("RT", 60), new("LeftStickDown", 75), new("A")]));
        report[6] = 90;
        report[10] = 1;
        Check(Read(chord) == (LiveState.Neutral with { Rt = 255, Ly = 32, Buttons = 0x1000 }), "all L4 actions in one report");
        report[10] = 3;
        Check(Read(chord) == (LiveState.Neutral with { Rt = 255, Buttons = 0x1000 }), "opposite equal stick actions cancel");
        report[10] = 2;
        Check(Read(chord) == (LiveState.Neutral with { Rt = 153, Ly = 223, Buttons = 0x1000 }), "R4 remains held after releasing L4");
        report[10] = 0;
        Check(Read(chord) == (LiveState.Neutral with { Rt = 90 }), "release restores real trigger depth");
        report[6] = 0;

        var mapAxes = Map(new("LeftStickUp", [new("RightStickLeft", 50), new("X")]),
            new("DPadRight", [new("LeftStickRight")]), new("A", []), new("RT", [new("LT", 50), new("Y")]));
        report[1] = 1; // northeast becomes D-pad up and left stick right
        report[3] = 64;
        report[8] = 1;
        report[6] = 127;
        Check(Read(mapAxes) == (LiveState.Neutral with { Rx = 96, Lx = 255, Buttons = 0x4000, Lt = 64, Hat = 0 }),
            "standard unbind, diagonal, scaled analog source, stick Y polarity and trigger below half");
        report[3] = 65;
        report[6] = 128;
        Check(Read(mapAxes) == (LiveState.Neutral with { Rx = 97, Lx = 255, Buttons = 0x8000, Lt = 64, Hat = 0 }),
            "analog-to-digital threshold at half travel");
        report[8] = 0;
        report[1] = 15;
        report[6] = 0;
        // Force the mapper to reconstruct every native analog value, even when no extra is held.
        for (int i = 0; i < 256; i++)
        {
            report[2] = report[3] = report[4] = report[5] = report[6] = report[7] = (byte)i;
            Check(Read(chord) == new LiveState((byte)i, (byte)i, (byte)i, (byte)i, (byte)i, (byte)i, 8, 0),
                "lossless default axes and independent triggers at " + i);
        }

        var custom = Map(new("b", [new("a")]), new("extra:pl", [new("lt", 30)]));
        Check(custom.GetActions("B").Single().Target == "A" && custom.GetActions("extra:PL").Single() == new XboxAction("LT", 30),
            "canonical IDs and case-insensitive parsing");
        Check(Map(new InputMapping("B", [new("B")]), new InputMapping("extra:PL", [])).Mappings.Count == 0, "implicit default normalization");
        Reject(() => Map(new InputMapping("L4", [new("B")])));
        Reject(() => Map(new InputMapping("A", []), new InputMapping("a", [])));
        Reject(() => Map(new InputMapping("A", [new("Y"), new("y")])));
        Reject(() => Map(new InputMapping("extra:PL", [new("None")])));
        Reject(() => Map(new InputMapping("A", [new("RT", 101)])));
        Reject(() => Map(new InputMapping("A", [new("RT", 0)])));
        Reject(() => Map(new InputMapping("A", [new("Y", 50)])));
        DeadzoneChecks();
        OuterDeadzoneChecks();
        Console.WriteLine("Passed simultaneous actions, standard remaps, analog thresholds, overlap, release and lossless default axes.");
    }

    private static void DeadzoneChecks()
    {
        var layout = DeviceLayout.Bluetooth85HF;
        var left = PadBindings.CreateMappings(layout, [], new(20, 0));
        var right = PadBindings.CreateMappings(layout, [], new(0, 20));
        var report = new byte[32];
        report[0] = 1;
        report[1] = 15;
        report.AsSpan(2, 4).Fill(128);
        LiveState Read(PadBindings bindings)
        {
            Check(bindings.TryDecode(report, out var state, out _), "valid deadzone input report");
            return state;
        }

        Check(Read(left) == LiveState.Neutral, "deadzone preserves exact center");
        report[2] = report[4] = 151;
        report[6] = 57;
        report[7] = 207;
        var untouched = LiveState.Neutral with { Lt = 207, Rt = 57 };
        Check(Read(left) == (untouched with { Rx = 151 }) && Read(right) == (untouched with { Lx = 151 }),
            "one stick's inner drift is ignored without affecting the other stick or triggers");
        report[3] = 151;
        Check(Read(left) == (untouched with { Lx = 134, Ly = 134, Rx = 151 }),
            "combined diagonal travel exits the radial deadzone and keeps its direction");
        report[2] = 96;
        report[3] = 128;
        Check(Read(left).Lx == 120, "linear rescaling preserves a negative axis excursion");
        report[2] = 0;
        report[3] = 255;
        Check(Read(left) == (untouched with { Lx = 0, Ly = 255, Rx = 151 }),
            "full directional travel is preserved even outside the unit circle");

        var remapped = PadBindings.CreateMappings(layout,
            [new("LeftStickRight", [new("X")]), new("extra:L4", [new("LeftStickDown", 10)])], new(30, 0));
        report[2] = 196;
        report[3] = 128;
        report[10] = 1;
        Check(Read(remapped) == (untouched with { Rx = 151, Ly = 141 }),
            "physical filtering precedes the remap threshold and leaves generated stick actions intact");
        Reject(() => PadBindings.CreateMappings(layout, [], new(-1, 0)));
        Reject(() => PadBindings.CreateMappings(layout, [], new(0, StickDeadzones.Maximum + 1)));
        Console.WriteLine("Passed radial stick deadzones, independent sticks, remap ordering and full travel.");
    }

    private static void OuterDeadzoneChecks()
    {
        Check(StickDeadzones.Filter(0.1, 0, 10, 10) == (0, 0), "the inner threshold stays neutral with an outer zone");
        var halfway = StickDeadzones.Filter(0.5, 0, 10, 10);
        Check(Math.Abs(halfway.X - 0.5) < 1e-12 && halfway.Y == 0, "travel is linear between the two thresholds");
        var cutoff = StickDeadzones.Filter(-0.9, 0, 10, 10);
        Check(cutoff == (-1, 0), "a 90 percent cutoff reaches full negative travel");
        var diagonal = StickDeadzones.Filter(0.63, -0.84, 10, 10);
        Check(Math.Abs(diagonal.X - 0.6) < 1e-12 && Math.Abs(diagonal.Y + 0.8) < 1e-12,
            "an outer zone saturates diagonals to unit travel while preserving their direction");
        var abovePreviousLimit = StickDeadzones.Filter(0, -0.745, 64, 15);
        Check(StickDeadzones.Filter(0.63, 0, 64, 15) == (0, 0) && Math.Abs(abovePreviousLimit.Y + 0.5) < 1e-12,
            "a 64 percent inner threshold and an 85 percent outer cutoff center and rescale the remaining travel");
        var narrow = StickDeadzones.Filter(0.995, 0, 99, 0);
        Check(Math.Abs(narrow.X - 0.5) < 1e-12, "the 99 to 100 percent range remains usable");

        var layout = DeviceLayout.Bluetooth85HF;
        var outer = PadBindings.CreateMappings(layout, [], new(0, 0, 10, 0));
        var report = new byte[32];
        report[0] = 1;
        report[1] = 15;
        report.AsSpan(2, 4).Fill(128);
        report[2] = report[4] = 244;
        report[6] = 57;
        Check(outer.TryDecode(report, out var state, out _) &&
            state == (LiveState.Neutral with { Lx = 255, Rx = 244, Rt = 57 }),
            "a left outer zone reaches full output without changing the right stick or trigger");
        Reject(() => PadBindings.CreateMappings(layout, [], new(0, 0, -1, 0)));
        Reject(() => PadBindings.CreateMappings(layout, [], new(0, 0, 0, 100)));
        Reject(() => PadBindings.CreateMappings(layout, [], new(30, 0, 70, 0)));
        Reject(() => PadBindings.CreateMappings(layout, [], new(64, 0, 37, 0)));
        Console.WriteLine("Passed outer stick cutoff, radial interpolation, independent output and threshold validation.");
    }

    private static void Check(bool passed, string detail)
    {
        if (!passed) throw new InvalidDataException("Mapping test failed: " + detail);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Expected invalid mapping to fail.");
    }
}
