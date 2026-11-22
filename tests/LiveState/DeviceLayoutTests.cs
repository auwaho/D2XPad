using D2XPad.Live;

internal static class DeviceLayoutTests
{
    public static void Run()
    {
        var layout = DeviceLayout.Bluetooth85HF;
        byte[] report = new byte[32];
        report[0] = 1;
        report[1] = 15;
        report.AsSpan(2, 4).Fill(128);
        var remap = PadBindings.Create(layout, [new("pl", "Jump", "y")]);
        var off = PadBindings.Create(layout, []);
        report[8] = 0x20;
        Check(remap.ExtraAssignments.Single() == new ExtraAssignment("PL", "PL", "Y"), "immutable physical source labels after legacy migration");
        Check(remap.TryDecode(report, out var state, out var extras) && state.Buttons == 0x8000 && extras == ExtraButtons.PL, "custom target");
        Check(off.TryDecode(report, out state, out extras) && state == LiveState.Neutral && extras == ExtraButtons.PL
            && !layout.IsPhysicallyReleased(report), "removed button stays physical and holds the release gate");
        report[8] |= 1;
        Check(remap.TryDecode(report, out state, out _) && state.Buttons == 0x9000, "native A passes through during remap");

        Reject(() => PadBindings.Create(layout, [new("PL", "First", "LB"), new("pl", "Second", "RB")]));
        Reject(() => PadBindings.Create(layout, [new("L4", "Shared", "Y"), new("R4", "shared", "Y")]));
        Reject(() => PadBindings.Create(layout, [new("A", "Standard A", "Y")]));
        Reject(() => PadBindings.Create(layout, [new("L4", "Left", "Unknown")]));
        Reject(() => PadBindings.Create(layout, [new("L4", " ", "X")]));
        Reject(() => DeviceLayout.Resolve("unverified-layout"));

        var fixture = new FixtureLayout();
        var other = PadBindings.Create(fixture, [new("side", "Shift", "DPadRight")]);
        Check(other.TryDecode([0x13, 255, 1], out state, out extras) && (ulong)extras == 1UL << 35 &&
            state == (LiveState.Neutral with { Rt = 255, Hat = 2 }), "independent adapter with another framing and 64-bit source flag");
        Check(!other.TryDecode(report, out _, out _), "reject cross-device reports");
        Reject(() => PadBindings.Create(fixture, [new("PL", "Jump", "Y")]));
        Console.WriteLine("Passed device-specific extra sources, empty profiles, independent physical state, names and target validation.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Accepted an invalid extra-button mapping.");
    }

    private static void Check(bool success, string reason)
    {
        if (!success) throw new InvalidDataException($"Layout test failed: {reason}.");
    }

    // Deliberately unrelated report ID, length, trigger position, and extra input. Never registered for hardware.
    private sealed class FixtureLayout : DeviceLayout
    {
        public override string Id => "fixture";
        public override string DefaultProfileName => "fixture-default";
        public override string ShortName => "Fixture";
        public override string DisplayName => ShortName;
        public override string Transport => "USB";
        public override IReadOnlyList<ExtraSource> ExtraSources { get; } = [new("side", "Side", 1UL << 35)];
        public override bool MatchesCollection(uint vendorId, uint productId, ushort usagePage, ushort usage) => false;
        public override bool MatchesReports(string transport, int? reportLength, IReadOnlyList<byte>? reportIds) =>
            transport == Transport && reportLength == 3 && reportIds?.Contains((byte)0x13) == true;
        public override bool IsPhysicallyReleased(ReadOnlySpan<byte> report, StickDeadzones deadzones = default) => report is [0x13, 0, 0];
        public override bool TryDecodePhysical(ReadOnlySpan<byte> report, out LiveState state, out ExtraButtons extras)
        {
            state = LiveState.Neutral;
            extras = ExtraButtons.None;
            if (report is not [0x13, var trigger, var side]) return false;
            state = state with { Rt = trigger };
            if ((side & 1) != 0) extras = (ExtraButtons)ExtraSources[0].Mask;
            return true;
        }
    }
}
