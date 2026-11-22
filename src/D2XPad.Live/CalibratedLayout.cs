using System.Security.Cryptography;
using System.Text.Json;
using D2XPad.Capture;

namespace D2XPad.Live;

internal sealed record CollectionSignature(ushort Vid, ushort Pid, ushort Page, ushort Usage,
    string Transport, int Length, byte ReportId, string Descriptor)
{
    internal static CollectionSignature From(HidCollection d)
    {
        if (!ControllerCalibration.IsCandidate(d) || d.InputReportLength is not (>= 2 and <= 1024) ||
            d.InputReportIds is not { Count: 1 } || d.InputValues == null)
            throw new InvalidDataException("Calibration requires a physical game controller with one input report ID, readable HID capabilities and a report length of 2–1024 bytes.");
        return new(d.VendorId, d.ProductId, d.UsagePage, d.Usage, d.Transport, d.InputReportLength.Value,
            d.InputReportIds[0], Fingerprint(d));
    }

    internal static string Fingerprint(HidCollection d) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { d.InputValues, d.ButtonSignature })));

    internal string LayoutId() => "hid-" + Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this)))[..20];
}

internal sealed record InputField(int Offset, int Width, bool Signed = false)
{
    public int Read(ReadOnlySpan<byte> report)
    {
        int value = 0;
        for (int bit = 0; bit < Width; bit++)
            value |= ((report[(Offset + bit) >> 3] >> ((Offset + bit) & 7)) & 1) << bit;
        return Signed && (value & (1 << (Width - 1))) != 0 ? value - (1 << Width) : value;
    }

    public bool Overlaps(InputField other) => Offset < other.Offset + other.Width && other.Offset < Offset + Width;
    public bool CanRepresent(int value)
    {
        int limit = 1 << (Signed ? Width - 1 : Width);
        return value >= (Signed ? -limit : 0) && value < limit;
    }
}

// First and Last are the measured left/up and right/down endpoints, or rest/full
// for a trigger or button. A hat records all cardinal values and its real null value.
internal sealed record MeasuredControl(string Source, InputField Field, int Rest, int First, int Last,
    string? ExtraName = null, int[]? Cardinals = null);
internal sealed record LayoutFile(int Version, CollectionSignature Collection, string Name, MeasuredControl[] Controls, string? BaseLayout = null);

internal sealed class CalibratedLayout : DeviceLayout
{
    internal static readonly IReadOnlyDictionary<string, ushort> ButtonBits = new Dictionary<string, ushort>
    {
        ["A"] = 0x1000, ["B"] = 0x2000, ["X"] = 0x4000, ["Y"] = 0x8000, ["LB"] = 0x100, ["RB"] = 0x200,
        ["L3"] = 0x40, ["R3"] = 0x80, ["Back"] = 0x20, ["Start"] = 0x10, ["Guide"] = 0x400,
        ["DPadUp"] = 1, ["DPadRight"] = 8, ["DPadDown"] = 2, ["DPadLeft"] = 4
    };
    internal static readonly string[] Dpad = ["DPadUp", "DPadRight", "DPadDown", "DPadLeft"];
    private static readonly string[] HatAxes = ["LX", "LY", "RX", "RY", "LT", "RT", "Hat"];
    internal LayoutFile File { get; }
    private readonly Dictionary<string, MeasuredControl> controls;
    private readonly Dictionary<string, ulong> masks;
    public override string Id { get; }
    internal bool Is85HF => File.BaseLayout == Bluetooth85HF.Id;
    public override string DefaultProfileName => Is85HF ? Bluetooth85HF.DefaultProfileName : Id;
    public override string DisplayName => File.Name;
    public override string ShortName => Is85HF ? Bluetooth85HF.ShortName : File.Name;
    public override string Transport => File.Collection.Transport;
    public override IReadOnlyList<ExtraSource> ExtraSources { get; }

    internal CalibratedLayout(LayoutFile file)
    {
        var s = file.Collection;
        bool builtIn = file.BaseLayout == Bluetooth85HF.Id;
        if (file.Version != 1 || file.BaseLayout != null && !builtIn || s == null || s.Page != 1 || s.Usage is not (4 or 5) ||
            s.Transport is not ("Bluetooth" or "USB") || s.Length is < 2 or > 1024 ||
            s.Descriptor is not { Length: 64 } || !s.Descriptor.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 64 || file.Name.Any(char.IsControl) ||
            file.Controls == null || file.Controls.Length > 89 || !builtIn && file.Controls.Length == 0 ||
            builtIn && !(Bluetooth85HF.MatchesCollection(s.Vid, s.Pid, s.Page, s.Usage) && Bluetooth85HF.MatchesReports(s.Transport, s.Length, [s.ReportId])))
            throw new InvalidDataException("Invalid controller layout header or empty control list.");
        File = file;
        Id = builtIn ? Bluetooth85HF.Id : s.LayoutId();
        controls = new(StringComparer.Ordinal);
        masks = new(StringComparer.Ordinal);
        var extras = new List<ExtraSource>();
        foreach (var c in file.Controls)
        {
            if (c == null || c.Source == null || c.Field == null || c.Field.Width is < 1 or > 24 ||
                c.Field.Offset < 8 || c.Field.Offset > s.Length * 8 - c.Field.Width ||
                !c.Field.CanRepresent(c.Rest) || !c.Field.CanRepresent(c.First) || !c.Field.CanRepresent(c.Last) ||
                controls.Values.Any(p => p.Field.Overlaps(c.Field)) || !controls.TryAdd(c.Source, c))
                throw new InvalidDataException("Duplicate, overlapping or invalid physical control field.");
            if (builtIn && (!Bluetooth85HfLayout.AnalogBytes.TryGetValue(c.Source, out int offset) ||
                c.Field != new InputField(offset * 8, 8) || c.ExtraName != null))
                throw new InvalidDataException("An 85HF calibration can only change the travel of its verified analog fields.");
            if (c.ExtraName != null)
            {
                ControllerCalibration.ValidateExtraName(c.ExtraName);
                if (!IsExtraId(c.Source, s.Length) || extras.Count == 64 ||
                    extras.Any(e => e.Name == c.ExtraName)) throw new InvalidDataException("Invalid extra button identity.");
                ulong mask = 1UL << extras.Count;
                extras.Add(new(c.Source, c.ExtraName, mask));
                masks.Add(c.Source, mask);
            }
            else if (!ButtonBits.ContainsKey(c.Source) && !HatAxes.Contains(c.Source))
                throw new InvalidDataException("Unknown standard control.");

            if (c.Source == "Hat")
            {
                if (c.Cardinals is not { Length: 4 } points || !IsClockwiseHat(c.Field, c.Rest, c.First, c.Last, points))
                    throw new InvalidDataException("Unsupported hat direction encoding.");
            }
            else if (c.Cardinals != null || c.First == c.Last ||
                (IsStick(c.Source) ? c.Rest <= Math.Min(c.First, c.Last) || c.Rest >= Math.Max(c.First, c.Last)
                 : c.First != c.Rest || (c.Source is not ("LT" or "RT") && c.Field.Width != 1)))
                throw new InvalidDataException("Invalid calibrated endpoints.");
        }
        if (controls.ContainsKey("Hat") && Dpad.Any(controls.ContainsKey))
            throw new InvalidDataException("Multiple D-pad representations.");
        ExtraSources = builtIn ? Bluetooth85HF.ExtraSources : extras.AsReadOnly();
    }

    internal static bool IsStick(string name) => name is "LX" or "LY" or "RX" or "RY";
    internal static string ExtraId(InputField f) => $"bit-{f.Offset}";
    private static bool IsExtraId(string id, int length) =>
        id.StartsWith("bit-", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out int position) &&
        position >= 8 && position < length * 8 && id == $"bit-{position}" ||
        id.StartsWith("extra-", StringComparison.Ordinal) && id.Length == 22 && id[6..].All(Uri.IsHexDigit);
    internal static bool IsClockwiseHat(InputField f, int rest, int first, int last, int[] points)
    {
        if (points.Length != 4 || points.Distinct().Count() != 4) return false;
        int count = last - first + 1;
        if (count is not (4 or 8) || rest >= first && rest <= last || !f.CanRepresent(first) || !f.CanRepresent(last) ||
            points.Any(p => p < first || p > last)) return false;
        int step = (points[1] - points[0] + count) % count;
        return (step == count / 4 || step == count * 3 / 4) && Enumerable.Range(0, 4).All(i =>
            points[i] == first + (points[0] - first + i * step) % count);
    }

    public override bool HasStandardInput(string id)
    {
        string key = id switch
        {
            "LeftStickLeft" or "LeftStickRight" => "LX", "LeftStickUp" or "LeftStickDown" => "LY",
            "RightStickLeft" or "RightStickRight" => "RX", "RightStickUp" or "RightStickDown" => "RY", _ => id
        };
        return Is85HF || controls.ContainsKey(key) || controls.ContainsKey("Hat") && Dpad.Contains(key);
    }

    public override bool MatchesCollection(uint vendorId, uint productId, ushort usagePage, ushort usage) =>
        File.Collection.Vid == vendorId && File.Collection.Pid == productId &&
        File.Collection.Page == usagePage && File.Collection.Usage == usage;
    public override bool MatchesReports(string transport, int? reportLength, IReadOnlyList<byte>? reportIds) =>
        Is85HF ? Bluetooth85HF.MatchesReports(transport, reportLength, reportIds) :
        Transport == transport && reportLength == File.Collection.Length && reportIds is { Count: 1 } && reportIds[0] == File.Collection.ReportId;
    internal bool MatchesDescriptor(HidCollection d) => File.Collection.Descriptor == CollectionSignature.Fingerprint(d);

    public override bool TryDecodePhysical(ReadOnlySpan<byte> report, out LiveState state, out ExtraButtons extras)
    {
        state = LiveState.Neutral;
        extras = ExtraButtons.None;
        if (report.Length != File.Collection.Length || report[0] != File.Collection.ReportId) return false;
        if (Is85HF && !Bluetooth85HF.TryDecodePhysical(report, out state, out extras)) return false;
        byte hat = state.Hat;
        ushort buttons = state.Buttons;
        foreach (var c in File.Controls)
        {
            int value = c.Field.Read(report);
            switch (c.Source)
            {
                case "LX": state = state with { Lx = Stick(c, value) }; break;
                case "LY": state = state with { Ly = Stick(c, value) }; break;
                case "RX": state = state with { Rx = Stick(c, value) }; break;
                case "RY": state = state with { Ry = Stick(c, value) }; break;
                case "LT": state = state with { Lt = Scale(value, c.Rest, c.Last) }; break;
                case "RT": state = state with { Rt = Scale(value, c.Rest, c.Last) }; break;
                case "Hat":
                    var points = c.Cardinals!;
                    if (value >= c.First && value <= c.Last)
                    {
                        int count = c.Last - c.First + 1;
                        int delta = (value - points[0] + count) % count;
                        if ((points[1] - points[0] + count) % count != count / 4) delta = (count - delta) % count;
                        hat = (byte)(delta * 8 / count);
                    }
                    break;
                default:
                    if (value != c.Last) break;
                    if (c.ExtraName != null) extras |= (ExtraButtons)masks[c.Source];
                    else buttons |= ButtonBits[c.Source];
                    break;
            }
        }
        if ((buttons & 15) != 0)
        {
            ReadOnlySpan<byte> directions = [8, 0, 4, 8, 6, 7, 5, 6, 2, 1, 3, 2, 8, 0, 4, 8];
            hat = directions[buttons & 15];
        }
        state = state with { Hat = hat, Buttons = (ushort)(buttons & 0xfff0) };
        return true;
    }

    private static byte Scale(int v, int start, int end) =>
        (byte)Math.Round(255 * Math.Clamp((double)(v - start) / (end - start), 0, 1));
    private static byte Stick(MeasuredControl c, int v) =>
        (v - c.Rest) * Math.Sign(c.Last - c.Rest) > 0
            ? (byte)(128 + (Scale(v, c.Rest, c.Last) * 127 + 127) / 255)
            : (byte)(128 - (Scale(v, c.Rest, c.First) * 128 + 127) / 255);

    public override bool IsPhysicallyReleased(ReadOnlySpan<byte> report, StickDeadzones deadzones = default)
    {
        if (!TryDecodePhysical(report, out var s, out var extras)) return false;
        if (Is85HF && !Bluetooth85HfLayout.DigitalsReleased(report)) return false;
        // Check each digital input separately so opposite held D-pad buttons cannot cancel the gate.
        foreach (var c in File.Controls)
            if ((c.Field.Width == 1 || c.Source == "Hat") && c.Field.Read(report) != c.Rest) return false;
        int triggerTolerance = Is85HF ? 2 : 5;
        return extras == 0 && s.Buttons == 0 && s.Hat == 8 && s.Lt <= triggerTolerance && s.Rt <= triggerTolerance &&
            StickDeadzones.IsReleased(s.Lx, s.Ly, deadzones.Left, deadzones.LeftOuter) &&
            StickDeadzones.IsReleased(s.Rx, s.Ry, deadzones.Right, deadzones.RightOuter);
    }
}
