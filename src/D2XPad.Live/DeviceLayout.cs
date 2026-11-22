namespace D2XPad.Live;

// Source IDs and flags belong to the hardware layout. Profile names and targets are user data.
internal sealed record ExtraSource(string Id, string Name, ulong Mask);
internal sealed record ExtraAssignment(string Source, string Name, string Target);

internal abstract class DeviceLayout
{
    public static DeviceLayout Bluetooth85HF { get; } = new Bluetooth85HfLayout();
    internal static IReadOnlyList<DeviceLayout> Supported => LayoutStore.All;
    public abstract string Id { get; }
    public abstract string DefaultProfileName { get; }
    public abstract string DisplayName { get; }
    public abstract string ShortName { get; }
    public abstract string Transport { get; }
    public abstract IReadOnlyList<ExtraSource> ExtraSources { get; }
    public virtual bool HasStandardInput(string id) => true;

    public static DeviceLayout Resolve(string id)
    {
        var entry = LayoutStore.Library().FirstOrDefault(c => c.Layout.Id == id)
            ?? throw new InvalidDataException($"Unsupported controller layout '{id}'.");
        if (entry.IsArchived) throw new InvalidDataException($"Restore the archived controller '{entry.Layout.DisplayName}' to use its profiles.");
        return entry.Layout;
    }

    public abstract bool MatchesCollection(uint vendorId, uint productId, ushort usagePage, ushort usage);
    public abstract bool MatchesReports(string transport, int? reportLength, IReadOnlyList<byte>? reportIds);
    public abstract bool TryDecodePhysical(ReadOnlySpan<byte> report, out LiveState state, out ExtraButtons extras);
    public abstract bool IsPhysicallyReleased(ReadOnlySpan<byte> report, StickDeadzones deadzones = default);
}

internal sealed class Bluetooth85HfLayout : DeviceLayout
{
    private static readonly (ExtraSource Source, int Offset, byte Bit)[] Fields =
    [
        (new("L4", "L4", (ulong)ExtraButtons.L4), 10, 0x01),
        (new("R4", "R4", (ulong)ExtraButtons.R4), 10, 0x02),
        (new("PL", "PL", (ulong)ExtraButtons.PL), 8, 0x20),
        (new("PR", "PR", (ulong)ExtraButtons.PR), 8, 0x04)
    ];

    public override string Id => "85hf-bluetooth";
    public override string DefaultProfileName => "default";
    public override string DisplayName => "8BitDo 85HF";
    public override string ShortName => "85HF";
    public override string Transport => "Bluetooth";
    public override IReadOnlyList<ExtraSource> ExtraSources { get; } =
        Array.AsReadOnly(Fields.Select(f => f.Source).ToArray());

    // These are the same report-1 fields used by TryDecodePhysical. Recalibration
    // measures their travel while the factory adapter continues to decode buttons.
    internal static readonly IReadOnlyDictionary<string, int> AnalogBytes = new Dictionary<string, int>
        { ["LX"] = 2, ["LY"] = 3, ["RX"] = 4, ["RY"] = 5, ["LT"] = 7, ["RT"] = 6 };

    public override bool MatchesCollection(uint vendorId, uint productId, ushort usagePage, ushort usage) =>
        vendorId == 0x2DC8 && productId == 0x2067 && usagePage == 1 && usage == 5;

    public override bool MatchesReports(string transport, int? reportLength, IReadOnlyList<byte>? reportIds) =>
        transport == Transport && reportLength == 32 && reportIds?.Contains((byte)1) == true;

    // These are the fixed physical controls, before any extra-button assignments.
    private static readonly ushort[] Face = MakeButtonTable(0x1000, 0x2000, 0, 0x4000, 0x8000, 0, 0x0100, 0x0200);
    private static readonly ushort[] Menu = MakeButtonTable(0, 0, 0x0020, 0x0010, 0x0400, 0x0040, 0x0080);

    public override bool TryDecodePhysical(ReadOnlySpan<byte> report, out LiveState state, out ExtraButtons extras)
    {
        extras = ExtraButtons.None;
        if (report.Length != 32 || report[0] != 1)
        {
            state = LiveState.Neutral;
            return false;
        }
        foreach (var field in Fields)
            if ((report[field.Offset] & field.Bit) != 0) extras |= (ExtraButtons)field.Source.Mask;
        int hat = report[1] & 15;
        state = new(report[2], report[3], report[4], report[5], report[7], report[6],
            (byte)(hat < 8 ? hat : 8), (ushort)(Face[report[8]] | Menu[report[9]]));
        return true;
    }

    // Unassigned extra buttons still block the release gate. Ignore the two digital trigger mirrors.
    public override bool IsPhysicallyReleased(ReadOnlySpan<byte> r, StickDeadzones deadzones = default) =>
        DigitalsReleased(r) && r[6] <= 2 && r[7] <= 2 &&
        StickDeadzones.IsReleased(r[2], r[3], deadzones.Left, deadzones.LeftOuter) &&
        StickDeadzones.IsReleased(r[4], r[5], deadzones.Right, deadzones.RightOuter);

    internal static bool DigitalsReleased(ReadOnlySpan<byte> r) =>
        r.Length == 32 && r[0] == 1 && (r[1] & 15) == 15 && r[8] == 0 &&
        (r[9] & 0x7c) == 0 && (r[10] & 3) == 0;

    private static ushort[] MakeButtonTable(params ushort[] buttons)
    {
        var table = new ushort[256];
        for (int value = 0; value < table.Length; value++)
            for (int bit = 0; bit < buttons.Length; bit++)
                if ((value & (1 << bit)) != 0) table[value] |= buttons[bit];
        return table;
    }
}
