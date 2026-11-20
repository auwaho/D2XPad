using System.Buffers.Binary;
using System.Text.Json;

namespace D2XPad.Live;

// The wire status carries layout-specific source bits. These names preserve the 85HF diagnostics.
[Flags]
internal enum ExtraButtons : ulong { None = 0, L4 = 1, R4 = 2, PL = 4, PR = 8 }

// Adapters normalize axes to 0..255: center 128, left/up 0, right/down 255.
// Hat is clockwise from north (0..7) or neutral (8). Buttons use XInput bits.
internal readonly record struct LiveState(byte Lx, byte Ly, byte Rx, byte Ry,
    byte Lt, byte Rt, byte Hat, ushort Buttons)
{
    public static LiveState Neutral => new(128, 128, 128, 128, 0, 0, 8, 0);
    public const int WireSize = 9;

    public void Write(Span<byte> data)
    {
        if (data.Length != WireSize) throw new ArgumentException("Expected a nine-byte state.");
        data[0] = Lx;
        data[1] = Ly;
        data[2] = Rx;
        data[3] = Ry;
        data[4] = Lt;
        data[5] = Rt;
        data[6] = Hat;
        BinaryPrimitives.WriteUInt16LittleEndian(data[7..], Buttons);
    }

    public static LiveState Read(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize) throw new InvalidDataException("Invalid state length.");
        ushort buttons = BinaryPrimitives.ReadUInt16LittleEndian(data[7..]);
        if (data[6] > 8 || (buttons & 0x080F) != 0)
            throw new InvalidDataException("Invalid hat position or reserved button bits.");
        return new(data[0], data[1], data[2], data[3], data[4], data[5], data[6], buttons);
    }
}

internal sealed record XboxAction(string Target, int Strength = 100);
internal sealed record InputMapping(string Source, IReadOnlyList<XboxAction> Actions);
internal sealed record InputControl(string Id, string Name, bool IsAnalog);

internal sealed class PadBindings
{
    private enum Control
    {
        A, B, X, Y, LB, RB, L3, R3, Back, Start, Guide, DPadUp, DPadDown, DPadLeft, DPadRight,
        LT, RT, LeftStickLeft, LeftStickRight, LeftStickUp, LeftStickDown,
        RightStickLeft, RightStickRight, RightStickUp, RightStickDown
    }

    private static ReadOnlySpan<ushort> DigitalBits =>
        [0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0x40, 0x80, 0x20, 0x10, 0x400, 1, 2, 4, 8];
    private static readonly Dictionary<string, Control> Controls = Enum.GetValues<Control>()
        .ToDictionary(control => control.ToString(), StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<InputControl> ActionTargets { get; } = Array.AsReadOnly(
        Enum.GetValues<Control>().Select(control => new InputControl(control.ToString(), control switch
        {
            Control.Back => "View", Control.Start => "Menu",
            Control.DPadUp => "D-pad up", Control.DPadDown => "D-pad down",
            Control.DPadLeft => "D-pad left", Control.DPadRight => "D-pad right",
            Control.LeftStickUp => "Left stick up", Control.LeftStickDown => "Left stick down",
            Control.LeftStickLeft => "Left stick left", Control.LeftStickRight => "Left stick right",
            Control.RightStickUp => "Right stick up", Control.RightStickDown => "Right stick down",
            Control.RightStickLeft => "Right stick left", Control.RightStickRight => "Right stick right",
            _ => control.ToString()
        }, control >= Control.LT)).ToArray());

    // Version 1 and 2 profiles accepted single digital targets (including None).
    public static IReadOnlyList<string> TargetNames { get; } =
        Array.AsReadOnly(ActionTargets.Where(t => !t.IsAnalog).Select(t => t.Id).Prepend("None").ToArray());
    private static ReadOnlySpan<byte> DirectionBits => [1, 9, 8, 10, 2, 6, 4, 5];
    private static ReadOnlySpan<byte> HatFromBits => [8, 0, 4, 8, 6, 7, 5, 6, 2, 1, 3, 2, 8, 0, 4, 8];
    internal static byte HatButtons(int hat) => (uint)hat < 8 ? DirectionBits[hat] : (byte)0;
    internal static PadBindings PhysicalOnly { get; } = CreateMappings(DeviceLayout.Bluetooth85HF, []);

    private readonly record struct Route(Control Source, ulong ExtraMask, (Control Target, double Scale)[] Actions);
    private readonly Route[] routes;
    private readonly Dictionary<string, IReadOnlyList<XboxAction>> effective = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonElement legacyAssignments;
    public DeviceLayout Layout { get; }
    public IReadOnlyList<InputControl> Sources { get; }
    public IReadOnlyList<InputMapping> Mappings { get; }

    // For the legacy guided CLI check and reports. Advanced profiles use the interactive tester.
    public IReadOnlyList<ExtraAssignment> ExtraAssignments { get; }
    internal bool UsesDefault85HfAssignments { get; }
    public bool UsesReplayDefaults => Layout == DeviceLayout.Bluetooth85HF && UsesDefault85HfAssignments;
    public JsonElement Assignments => legacyAssignments.ValueKind == JsonValueKind.Object ? legacyAssignments :
        throw new InvalidDataException("Use the desktop input tester for profiles with standard remaps, analog actions or combinations.");

    public static string ExtraId(string sourceId) => "extra:" + sourceId;
    public static IReadOnlyList<XboxAction> DefaultActionsFor(string sourceId) =>
        Controls.TryGetValue(sourceId, out var control) ? Array.AsReadOnly(new[] { new XboxAction(control.ToString()) }) : [];
    public IReadOnlyList<XboxAction> GetActions(string sourceId) => effective[sourceId];

    public static PadBindings CreateMappings(DeviceLayout layout, IEnumerable<InputMapping> mappings) => new(layout, mappings);

    private PadBindings(DeviceLayout layout, IEnumerable<InputMapping> overrides)
    {
        Layout = layout;
        var extras = layout.ExtraSources.ToDictionary(e => ExtraId(e.Id), StringComparer.OrdinalIgnoreCase);
        Sources = Array.AsReadOnly(ActionTargets.Where(t => layout.HasStandardInput(t.Id))
            .Concat(layout.ExtraSources.Select(e => new InputControl(ExtraId(e.Id), e.Name, false))).ToArray());
        var available = Sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var mappings = new List<InputMapping>();
        foreach (var row in overrides)
        {
            if (row?.Source == null || !available.Remove(row.Source, out var source))
                throw new InvalidDataException($"Unknown or duplicate physical input '{row?.Source}' for {layout.DisplayName}.");
            if (row.Actions == null) throw new InvalidDataException($"Missing action list for {source.Name}.");
            var actions = new List<XboxAction>();
            var destinations = new HashSet<Control>();
            foreach (var action in row.Actions)
            {
                if (action?.Target == null || !Controls.TryGetValue(action.Target, out var target) || !destinations.Add(target))
                    throw new InvalidDataException($"Unknown or duplicate Xbox target '{action?.Target}' for {source.Name}.");
                if (action.Strength is < 1 or > 100 || (target < Control.LT && action.Strength != 100))
                    throw new InvalidDataException("Analog strength must be 1–100 percent. Digital actions require 100 percent.");
                actions.Add(new(target.ToString(), action.Strength));
            }
            var readOnly = actions.AsReadOnly();
            if (!actions.SequenceEqual(DefaultActionsFor(source.Id))) mappings.Add(new(source.Id, readOnly));
            effective.Add(source.Id, readOnly);
        }
        Mappings = mappings.AsReadOnly();
        var compiled = new List<Route>();
        foreach (var source in Sources)
        {
            if (!effective.TryGetValue(source.Id, out var actions))
                effective.Add(source.Id, actions = DefaultActionsFor(source.Id));
            if (actions.Count == 0) continue;
            extras.TryGetValue(source.Id, out var extra);
            compiled.Add(new(extra == null ? Controls[source.Id] : default, extra?.Mask ?? 0,
                actions.Select(a => (Controls[a.Target], a.Strength / 100.0)).ToArray()));
        }
        routes = compiled.ToArray();

        var singleExtras = new List<ExtraAssignment>();
        bool legacy = Mappings.All(row => extras.ContainsKey(row.Source));
        foreach (var extra in layout.ExtraSources)
        {
            var actions = GetActions(ExtraId(extra.Id));
            if (actions.Count == 0) continue;
            if (actions.Count == 1 && Controls[actions[0].Target] < Control.LT)
                singleExtras.Add(new(extra.Id, extra.Name, actions[0].Target));
            else legacy = false;
        }
        ExtraAssignments = singleExtras.AsReadOnly();
        if (legacy)
        {
            var assignments = layout.ExtraSources.ToDictionary(e => e.Id,
                e => GetActions(ExtraId(e.Id)).FirstOrDefault()?.Target ?? "None");
            legacyAssignments = JsonSerializer.SerializeToElement(assignments);
            UsesDefault85HfAssignments = layout.Id == DeviceLayout.Bluetooth85HF.Id && assignments["L4"] == "LB" &&
                assignments["R4"] == "RB" && assignments["PL"] == "A" && assignments["PR"] == "B";
        }
    }

    // Upgrade old profile aliases by their stable physical source IDs. Physical names now belong to the layout.
    public static PadBindings Create(DeviceLayout layout, IEnumerable<ExtraAssignment> assignments)
    {
        var available = layout.ExtraSources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<InputMapping>();
        foreach (var row in assignments)
        {
            if (row?.Source == null || !available.Remove(row.Source, out var source))
                throw new InvalidDataException($"Unknown or duplicate extra input '{row?.Source}' for {layout.DisplayName}.");
            if (string.IsNullOrWhiteSpace(row.Name) || row.Name != row.Name.Trim() || row.Name.Length > 64 ||
                row.Name.Any(char.IsControl) || !usedNames.Add(row.Name))
                throw new InvalidDataException("Invalid or duplicate legacy extra-button name.");
            if (!TargetNames.Contains(row.Target, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Choose a supported Xbox target for {row.Name}.");
            mappings.Add(new(ExtraId(source.Id), string.Equals(row.Target, "None", StringComparison.OrdinalIgnoreCase)
                ? [] : [new(row.Target)]));
        }
        return CreateMappings(layout, mappings);
    }

    public static PadBindings Load(string path)
    {
        using var file = File.OpenRead(path);
        using var doc = JsonDocument.Parse(file, new JsonDocumentOptions { AllowDuplicateProperties = false });
        return Parse(doc.RootElement);
    }

    public static PadBindings Parse(JsonElement root)
    {
        var layout = DeviceLayout.Resolve(DeviceLayout.Bluetooth85HF.Id);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != layout.ExtraSources.Count)
            throw new InvalidDataException("Bindings must assign exactly L4, R4, PL and PR.");
        return Create(layout, layout.ExtraSources.Select(s =>
        {
            if (!root.TryGetProperty(s.Id, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Set {s.Id} to one of: {string.Join(", ", TargetNames)}.");
            return new ExtraAssignment(s.Id, s.Name, value.GetString()!);
        }));
    }

    public bool TryDecode(ReadOnlySpan<byte> report, out LiveState state, out ExtraButtons extras)
    {
        if (!Layout.TryDecodePhysical(report, out state, out extras)) return false;
        if (Mappings.Count == 0) return true;
        int physicalBits = state.Buttons | HatButtons(state.Hat);
        Span<double> output = stackalloc double[(int)Control.RightStickDown + 1];
        output.Clear();
        foreach (var route in routes)
        {
            double input = route.ExtraMask != 0 ? (((ulong)extras & route.ExtraMask) != 0 ? 1 : 0)
                : ReadControl(route.Source, state, physicalBits);
            if (input <= 0) continue;
            foreach (var (target, scale) in route.Actions)
            {
                double strength = target < Control.LT ? (input >= 0.5 ? 1 : 0) : input * scale;
                output[(int)target] = Math.Max(output[(int)target], strength);
            }
        }
        int bits = 0;
        for (int i = 0; i < DigitalBits.Length; i++)
            if (output[i] != 0) bits |= DigitalBits[i];
        state = new(Axis(output, Control.LeftStickLeft, Control.LeftStickRight),
            Axis(output, Control.LeftStickUp, Control.LeftStickDown),
            Axis(output, Control.RightStickLeft, Control.RightStickRight),
            Axis(output, Control.RightStickUp, Control.RightStickDown),
            ToByte(output[(int)Control.LT] * 255), ToByte(output[(int)Control.RT] * 255),
            HatFromBits[bits & 15], (ushort)(bits & 0xFFF0));
        return true;
    }

    private static double ReadControl(Control control, LiveState state, int bits) => control switch
    {
        Control.LT => state.Lt / 255.0, Control.RT => state.Rt / 255.0,
        Control.LeftStickLeft => (128 - state.Lx) / 128.0, Control.LeftStickRight => (state.Lx - 128) / 127.0,
        Control.LeftStickUp => (128 - state.Ly) / 128.0, Control.LeftStickDown => (state.Ly - 128) / 127.0,
        Control.RightStickLeft => (128 - state.Rx) / 128.0, Control.RightStickRight => (state.Rx - 128) / 127.0,
        Control.RightStickUp => (128 - state.Ry) / 128.0, Control.RightStickDown => (state.Ry - 128) / 127.0,
        _ => (bits & DigitalBits[(int)control]) != 0 ? 1 : 0
    };

    private static byte Axis(ReadOnlySpan<double> output, Control negative, Control positive)
    {
        double signed = output[(int)positive] - output[(int)negative];
        return ToByte(128 + signed * (signed < 0 ? 128 : 127));
    }

    private static byte ToByte(double value) => (byte)Math.Round(value, MidpointRounding.AwayFromZero);
}
