using D2XPad.Live;

string capture = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "85hf-replay.ndjson");
ProfileCatalogTests.Run(capture);
DeviceLayoutTests.Run();
MappingTests.Run();
await ReusedSlotTests.RunAsync();

var defaults = PadBindings.Load(Path.Combine(AppContext.BaseDirectory, "85hf-bindings.json"));
byte[] report = new byte[32];
report[0] = 1;
report[1] = 15;
report.AsSpan(2, 4).Fill(128);

void Check(bool condition, string control)
{
    if (!condition) throw new InvalidDataException($"Failed: {control}.");
}

void CheckButtons(PadBindings map, int bits, ExtraButtons extra = ExtraButtons.None, byte hat = 8)
{
    Check(map.TryDecode(report, out var state, out var sources)
        && state == (LiveState.Neutral with { Buttons = (ushort)bits, Hat = hat }) && sources == extra,
        $"buttons {bits:X4}, sources {extra}, hat {hat} for {Convert.ToHexString(report.AsSpan(0, 11))}");
}

Check(defaults.UsesReplayDefaults, "default capture bindings");
CheckButtons(defaults, 0);
for (byte i = 0; i < 8; i++)
{
    report[1] = (byte)(0xF0 | i);
    CheckButtons(defaults, 0, hat: i);
}
report[1] = 15;
report[9] = 0x10;
CheckButtons(defaults, 0x400);
report[9] = 3; // Trigger click flags alone produce no virtual buttons.
CheckButtons(defaults, 0);
report[9] = 0;

foreach (var (index, mask, source, native, expected) in new[]
{
    (10, 1, ExtraButtons.L4, 64, 0x0100), (10, 2, ExtraButtons.R4, 128, 0x0200),
    (8, 32, ExtraButtons.PL, 1, 0x1000), (8, 4, ExtraButtons.PR, 2, 0x2000)
})
{
    report[index] |= (byte)mask;
    CheckButtons(defaults, expected, source);
    CheckButtons(PadBindings.PhysicalOnly, 0, source);
    report[8] |= (byte)native;
    CheckButtons(defaults, expected, source);
    CheckButtons(PadBindings.PhysicalOnly, expected, source);
    report[8] &= (byte)~native;
    CheckButtons(defaults, expected, source);
    report[8] |= (byte)native;
    report[index] &= (byte)~mask;
    CheckButtons(defaults, expected);
    report[8] = 0;
    CheckButtons(defaults, 0);
}

var path = Path.GetTempFileName();
try
{
    File.WriteAllText(path, """{"L4":"DPadUp","R4":"DPadLeft","PL":"X","PR":"None"}""");
    var custom = PadBindings.Load(path);
    Check(!custom.UsesReplayDefaults, "custom bindings detected before replay");
    report[8] = 36;
    report[10] = 3;
    var all = ExtraButtons.L4 | ExtraButtons.R4 | ExtraButtons.PL | ExtraButtons.PR;
    CheckButtons(custom, 0x4000, all, 7);
    report[1] = 2; // East cancels west; north remains held.
    CheckButtons(custom, 0x4000, all, 0);
    report[1] = 3; // Southeast cancels northwest.
    CheckButtons(custom, 0x4000, all, 8);
    CheckButtons(PadBindings.PhysicalOnly, 0, all, 3);
    Check(PadBindings.HatButtons(3) == 10 && PadBindings.HatButtons(8) == 0, "physical D-pad for the tester");
}
finally { File.Delete(path); }

report[1] = 15;
report[2] = 0;
report[3] = 15;
report[4] = 255;
report[5] = 202;
report[6] = 57;
report[7] = 214;
report[8] = 0;
report[10] = 0;
Check(defaults.TryDecode(report, out var analog, out _) && analog == new LiveState(0, 15, 255, 202, 214, 57, 8, 0),
    "asymmetric axes, independent triggers");
Check(!defaults.TryDecode(report[..31], out _, out _) && !defaults.TryDecode(new byte[33], out _, out _), "wrong report lengths");
report[0] = 3;
Check(!defaults.TryDecode(report, out _, out _), "wrong report ID");
byte[] wire = new byte[LiveState.WireSize];
analog = analog with { Buttons = 0x1600 };
analog.Write(wire);
Check(wire.SequenceEqual(new byte[] { 0, 15, 255, 202, 214, 57, 8, 0, 0x16 }) && LiveState.Read(wire) == analog,
    "wire layout");
foreach (var invalid in new byte[][] { [], new byte[10], [0, 0, 0, 0, 0, 0, 9, 0, 0], [0, 0, 0, 0, 0, 0, 8, 0, 8] })
{
    try { LiveState.Read(invalid); }
    catch (InvalidDataException) { continue; }
    throw new InvalidDataException("Accepted a malformed state frame.");
}
Console.WriteLine("Synthetic checks passed for bindings, overlap/release, D-pad, analog input, report framing and IPC.");

{
    static byte Raw(short value, bool vertical = false)
    {
        var fraction = value / (value < 0 ? 32768d : 32767d);
        if (vertical) fraction = -fraction;
        return (byte)Math.Round(128 + fraction * (fraction < 0 ? 128 : 127));
    }
    int[] hatBits = [1, 9, 8, 10, 2, 6, 4, 5, 0];
    var cases = ReplayCases.Load(capture);
    Check(cases.Count == 46, "complete 85HF replay");
    foreach (var item in cases)
    {
        var e = item.Expected;
        Check(defaults.TryDecode(item.Report, out var state, out _) && state.Lt == e.Lt && state.Rt == e.Rt
            && state.Lx == Raw(e.Lx) && state.Ly == Raw(e.Ly, true) && state.Rx == Raw(e.Rx) && state.Ry == Raw(e.Ry, true)
            && (state.Buttons | hatBits[state.Hat]) == e.Buttons, item.Id);
    }
    Console.WriteLine($"Decoded {cases.Count} selected capture states and matched their expected defaults.");
}
