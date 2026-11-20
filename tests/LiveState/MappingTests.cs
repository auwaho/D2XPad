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
        Console.WriteLine("Passed simultaneous actions, standard remaps, analog thresholds, overlap, release and lossless default axes.");
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
