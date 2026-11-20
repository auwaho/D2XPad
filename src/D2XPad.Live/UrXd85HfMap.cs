using System.Text.Json;

namespace D2XPad.Live;

// Only import maps describing the 32-byte Bluetooth format verified with the stage 1A capture.
internal static class UrXd85HfMap
{
    private sealed record Field(string Type, int Offset, int Mask = 255);
    private static readonly Dictionary<string, Field> Verified = new()
    {
        ["a"] = new("button", 8, 1), ["b"] = new("button", 8, 2), ["x"] = new("button", 8, 8),
        ["y"] = new("button", 8, 16), ["lb"] = new("button", 8, 64), ["rb"] = new("button", 8, 128),
        ["pl"] = new("button", 8, 32), ["pr"] = new("button", 8, 4),
        ["select"] = new("button", 9, 4), ["start"] = new("button", 9, 8), ["home"] = new("button", 9, 16),
        ["l3"] = new("button", 9, 32), ["r3"] = new("button", 9, 64),
        ["l4"] = new("button", 10, 1), ["r4"] = new("button", 10, 2),
        ["dpad"] = new("hat", 1, 15), ["lx"] = new("axis", 2), ["ly"] = new("axis", 3),
        ["rx"] = new("axis", 4), ["ry"] = new("axis", 5), ["lt"] = new("trigger", 7), ["rt"] = new("trigger", 6)
    };

    public static void Validate(JsonElement root)
    {
        static string? Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Num(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
        static bool Flag(JsonElement e, string key, bool expected, bool optional = false) =>
            e.TryGetProperty(key, out var v) ? v.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False) : optional;

        if (!string.Equals(Str(root, "vid"), "2DC8", StringComparison.OrdinalIgnoreCase)
            || Str(root, "pid") != "2067" || Str(root, "layout") != "xbox" || !Flag(root, "has_report_id", false))
            throw new InvalidDataException("Expected a UR-XD 2DC8:2067 Xbox layout with has_report_id set to false.");
        if (root.EnumerateObject().Any(p => p.Name is not ("name" or "vid" or "pid" or "layout"
            or "has_report_id" or "reports" or "interfaces")))
            throw new InvalidDataException("The UR-XD map has unsupported top-level options.");
        if (!root.TryGetProperty("interfaces", out var interfaces) || interfaces.ValueKind != JsonValueKind.Array
            || interfaces.GetArrayLength() != 1 || interfaces[0].ValueKind != JsonValueKind.Number
            || !interfaces[0].TryGetInt32(out int iface) || iface != -1)
            throw new InvalidDataException("Expected the single Bluetooth 85HF interface (-1) in the UR-XD map.");
        var reports = root.GetProperty("reports");
        if (reports.ValueKind != JsonValueKind.Object || reports.EnumerateObject().Count() != 1)
            throw new InvalidDataException("Ambiguous UR-XD report selection. Expected a single 0 or -1_0 report.");
        var report = reports.EnumerateObject().First();
        if (report.Name is not ("0" or "-1_0") || report.Value.ValueKind != JsonValueKind.Object
            || report.Value.EnumerateObject().Count() != 1
            || !report.Value.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected 85HF inputs under the single 0 or -1_0 report.");

        var errors = new List<string>();
        var occupied = new List<(string Name, int Offset, int Mask)>();
        foreach (var entry in inputs.EnumerateObject())
        {
            string name = entry.Name;
            var data = entry.Value;
            if (!Verified.TryGetValue(name, out var field) || data.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Unsupported input {name}.");
                continue;
            }
            string? type = Str(data, "type");
            int offset = Num(data, "byte") ?? -1;
            int mask = type switch { "button" => Num(data, "bitmask") ?? 0, "hat" => 15, _ => 255 };
            if (offset is < 0 or >= 32)
                errors.Add($"{name} is outside the 32-byte report.");
            if (type != field.Type || offset != field.Offset || mask != field.Mask)
                errors.Add($"{name} must be {field.Type} at byte {field.Offset}, mask 0x{field.Mask:X2}.");
            foreach (var used in occupied.Where(p => p.Offset == offset && (p.Mask & mask) != 0))
                errors.Add($"{used.Name} and {name} overlap at byte {offset}.");
            occupied.Add((name, offset, mask));

            if (type is "axis" or "trigger" && (Num(data, "length") != 1 || !Flag(data, "center", type == "axis")))
                errors.Add($"{name} has an unsupported width or center setting.");
            if (type == "trigger" && !Flag(data, "is_analog", true))
                errors.Add($"{name} must use its independent analog field.");
            bool invertY = name is "ly" or "ry";
            if (!Flag(data, "invert", invertY, optional: !invertY) || !Flag(data, "signed", false, optional: true))
                errors.Add($"{name} uses a direction or signed encoding that differs from the verified 85HF map.");
            if (data.EnumerateObject().Any(p => p.Name switch
                {
                    "type" or "byte" or "range_confidence" or "centeredness" => false,
                    "bitmask" => type != "button",
                    "length" or "center" or "invert" or "signed" => type is not ("axis" or "trigger"),
                    "is_analog" => type != "trigger",
                    _ => true
                }))
                errors.Add($"{name} has unsupported control options.");
        }
        foreach (string missing in Verified.Keys.Where(k => !inputs.TryGetProperty(k, out _)))
            errors.Add($"Missing input {missing}.");
        if (errors.Count != 0)
            throw new InvalidDataException("UR-XD map differs from the captured Bluetooth layout. " + string.Join(" ", errors));
    }
}
