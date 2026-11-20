using D2XPad.Capture;

namespace D2XPad.Live;

internal sealed class ControllerCalibration
{
    private readonly CollectionSignature signature;
    private readonly string name;
    private readonly List<byte[]> neutral = [];
    private readonly List<MeasuredControl> controls = [];
    private readonly (HidValueField Descriptor, InputField Field)[] values;
    private readonly (HidValueField Descriptor, InputField Field)[] releaseValues;
    private readonly byte[] declaredButtons, knownFields, knownButtons, idleChanges, alternateIdle;
    private readonly bool[] variableBytes;
    private readonly ControllerEntry? original;
    private readonly bool factory;
    private bool verified;
    private IReadOnlyList<MeasuredControl> Previous => original?.Layout is CalibratedLayout previous ? previous.File.Controls : [];

    public ControllerCalibration(HidCollection device, string displayName, ControllerEntry? original = null)
    {
        this.original = original;
        factory = DeviceLayout.Bluetooth85HF.MatchesCollection(device.VendorId, device.ProductId, device.UsagePage, device.Usage) &&
            DeviceLayout.Bluetooth85HF.MatchesReports(device.Transport, device.InputReportLength, device.InputReportIds);
        if (factory && original?.IsBuiltIn != true)
            throw new InvalidDataException("Select the built-in 85HF in the controller library to recalibrate it.");
        signature = factory
            ? new(device.VendorId, device.ProductId, device.UsagePage, device.Usage, device.Transport, 32, 1, CollectionSignature.Fingerprint(device))
            : CollectionSignature.From(device);
        if (original != null && (original.IsArchived || original.Layout.Id != (factory ? DeviceLayout.Bluetooth85HF.Id : signature.LayoutId()) ||
            original.Layout is CalibratedLayout prior && prior.File.Collection != signature))
            throw new InvalidDataException("The selected collection does not match the saved controller's report format.");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 64 || displayName.Any(char.IsControl))
            throw new InvalidDataException("Enter a controller name of 1–64 characters.");
        name = displayName.Trim();
        // Zero page/usage denotes the adapter's previously verified raw byte positions.
        IEnumerable<HidValueField> inputFields = factory ? Bluetooth85HfLayout.AnalogBytes.Values.Select(offset =>
            new HidValueField(1, 0, 0, offset * 8, 8, 0, 255)) : device.InputValues!;
        values = inputFields.Where(f => f.ReportId == signature.ReportId &&
            f.Width is >= 1 and <= 24 && f.Offset >= 8 && f.Offset <= signature.Length * 8 - f.Width &&
            f.Maximum > f.Minimum)
            .Select(f => (Descriptor: f, Field: new InputField(f.Offset, f.Width, f.Minimum < 0)))
            .Where(v => v.Field.CanRepresent(v.Descriptor.Minimum) && v.Field.CanRepresent(v.Descriptor.Maximum)).ToArray();
        releaseValues = values.Where(v => factory || IsMotion(v.Descriptor) || IsHat(v.Descriptor) ||
            v.Descriptor.Page == 9).ToArray();
        declaredButtons = new byte[signature.Length];
        foreach (var b in device.InputButtons ?? [])
            if (b.ReportId == signature.ReportId && b.ByteOffset >= 1 && b.ByteOffset < declaredButtons.Length)
                declaredButtons[b.ByteOffset] |= b.Mask;
        knownFields = (byte[])declaredButtons.Clone();
        knownButtons = (byte[])declaredButtons.Clone();
        idleChanges = new byte[signature.Length];
        alternateIdle = new byte[signature.Length];
        variableBytes = new bool[signature.Length];
        foreach (var (descriptor, field) in releaseValues)
        {
            IncludeField(knownFields, field);
            if (descriptor.Page == 9 && field.Width == 1) IncludeField(knownButtons, field);
        }
        foreach (var c in Previous) IncludeMeasured(c);
    }

    public static bool IsCandidate(HidCollection d) => d.UsagePage == 1 && d.Usage is 4 or 5 &&
        d.Transport is "Bluetooth" or "USB" && !d.Path.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) &&
        !d.Path.Contains("VIGEM", StringComparison.OrdinalIgnoreCase);

    private void EnsureReport(byte[] report)
    {
        if (report.Length != signature.Length || report[0] != signature.ReportId)
            throw new InvalidDataException("This sample belongs to a different report ID or length. Capture it again.");
    }

    public void AddNeutral(byte[] report) => AddNeutralSamples([report]);

    public void AddNeutralSamples(IReadOnlyList<byte[]> reports)
    {
        if (reports.Count == 0) throw new InvalidDataException("Wait for a controller input report before recording neutral.");
        if (controls.Count > 0 || neutral.Count >= 2)
            throw new InvalidDataException("The released position has already been recorded.");
        byte[] reference = neutral.Count == 0 ? reports[0] : neutral[0];
        foreach (var report in reports)
        {
            EnsureReport(report);
            if (factory && !Bluetooth85HfLayout.DigitalsReleased(report))
                throw new InvalidDataException("Release all 85HF buttons and the D-pad before recording neutral.");
            foreach (var (d, f) in releaseValues)
                if (Math.Abs(f.Read(report) - f.Read(reference)) > Tolerance(d))
                    throw new InvalidDataException("A control moved between released samples. Release the controls and retry.");
            if (!factory && (ButtonChanged(report, reference, knownButtons) ||
                Previous.Any(c => Math.Abs(c.Field.Read(report) - c.Field.Read(reference)) > Tolerance(c))))
                throw new InvalidDataException("A control moved between released samples. Release the controls and retry.");
        }
        if (!factory)
        {
            foreach (var report in reports)
            {
                for (int i = 1; i < report.Length; i++)
                {
                    byte changed = (byte)((report[i] ^ reference[i]) & ~knownFields[i]);
                    if (changed == 0) continue;
                    idleChanges[i] |= changed;
                    // One demonstrated alternate value gives a bit mask. Multiple
                    // unexplained idle values make the remaining byte ambiguous.
                    if (alternateIdle[i] == 0) alternateIdle[i] = changed;
                    else if (alternateIdle[i] != changed) variableBytes[i] = true;
                }
            }
        }
        neutral.Add((byte[])reports[0].Clone());
    }

    private static bool IsHat(HidValueField d) => d.Page == 1 && d.Usage == 0x39;
    private static bool IsMotion(HidValueField d) => d.Page == 1 && d.Usage is >= 0x30 and <= 0x38 || d.Page == 2;
    private static int Tolerance(HidValueField d) => IsHat(d) || d.Maximum - d.Minimum == 1
        ? 0 : Math.Max(1, (d.Maximum - d.Minimum) / 25);
    private static int Tolerance(MeasuredControl c) => c.Source == "Hat" || Math.Abs(c.Last - c.First) == 1
        ? 0 : Math.Max(1, Math.Abs(c.Last - c.First) / 25);

    private static void IncludeField(byte[] mask, InputField field)
    {
        for (int i = field.Offset; i < field.Offset + field.Width; i++) mask[i / 8] |= (byte)(1 << (i % 8));
    }

    private void IncludeMeasured(MeasuredControl c)
    {
        IncludeField(knownFields, c.Field);
        if (c.Field.Width == 1) IncludeField(knownButtons, c.Field);
    }

    private static bool ButtonChanged(ReadOnlySpan<byte> report, ReadOnlySpan<byte> reference, ReadOnlySpan<byte> mask)
    {
        for (int i = 1; i < report.Length; i++)
            if (((report[i] ^ reference[i]) & mask[i]) != 0) return true;
        return false;
    }

    public bool IsNeutral(byte[] report)
    {
        EnsureReport(report);
        if (neutral.Count == 0) return false;
        if (factory && !Bluetooth85HfLayout.DigitalsReleased(report)) return false;
        foreach (var (d, f) in releaseValues)
            if (Math.Abs(f.Read(report) - Rest(f)) > Tolerance(d)) return false;
        // The verified 85HF packet also contains unrelated changing telemetry.
        if (factory) return true;
        if (ButtonChanged(report, neutral[0], declaredButtons)) return false;
        foreach (var c in Previous.Concat(controls))
            if (Math.Abs(c.Field.Read(report) - Rest(c.Field)) > Tolerance(c)) return false;
        for (int i = 1; i < report.Length; i++)
        {
            if (!variableBytes[i] && ((report[i] ^ neutral[0][i]) & ~(knownFields[i] | idleChanges[i])) != 0)
                return false;
        }
        return true;
    }

    private void CheckSamples(params byte[][] reports)
    {
        if (neutral.Count < 2) throw new InvalidDataException("Capture the released controller twice first.");
        foreach (var report in reports) EnsureReport(report);
    }

    private int Rest(InputField f) => (f.Read(neutral[0]) + f.Read(neutral[^1])) / 2;
    private static int Mean(int a, int b) => (a + b) / 2;

    public void BindAxis(string source, byte[] negative1, byte[] negative2, byte[] positive1, byte[] positive2)
    {
        if (!CalibratedLayout.IsStick(source)) throw new InvalidDataException("Unknown stick axis.");
        CheckSamples(negative1, negative2, positive1, positive2);
        var matches = new List<MeasuredControl>();
        foreach (var (d, f) in values.Where(v => !IsHat(v.Descriptor) && v.Field.Width >= 3 &&
            (!factory || v.Field.Offset == Bluetooth85HfLayout.AnalogBytes[source] * 8)))
        {
            int a = f.Read(negative1), b = f.Read(negative2), c = f.Read(positive1), e = f.Read(positive2);
            int start = Mean(a, b), end = Mean(c, e), center = Rest(f);
            int span = Math.Abs(end - start);
            if (Math.Sign(start - center) == Math.Sign(end - center) || span < Math.Max(4, (d.Maximum - d.Minimum) / 3) ||
                Math.Min(Math.Abs(start - center), Math.Abs(end - center)) < Math.Max(2, span / 8) ||
                Math.Max(Math.Abs(a - b), Math.Abs(c - e)) > Math.Max(1, span / 8) ||
                Math.Abs(f.Read(neutral[0]) - f.Read(neutral[1])) > Math.Max(1, span / 20)) continue;
            matches.Add(new(source, f, center, start, end));
        }
        Add(Unique(matches, "stick axis"));
    }

    public void BindTrigger(string source, byte[] half1, byte[] half2, byte[] full1, byte[] full2)
    {
        if (source is not ("LT" or "RT")) throw new InvalidDataException("Unknown trigger.");
        CheckSamples(half1, half2, full1, full2);
        var fields = values.Where(v => !IsHat(v.Descriptor) &&
            (!factory || v.Field.Offset == Bluetooth85HfLayout.AnalogBytes[source] * 8)).ToArray();
        var analog = new List<MeasuredControl>();
        bool HasPartialTravel(InputField f, int minimumSpan)
        {
            int start = Rest(f), a = f.Read(half1), b = f.Read(half2), c = f.Read(full1), e = f.Read(full2);
            int end = Mean(c, e), span = Math.Abs(end - start);
            if (span < minimumSpan || Math.Abs(c - e) > Math.Max(1, span / 8) ||
                Math.Abs(f.Read(neutral[0]) - f.Read(neutral[1])) > Math.Max(1, span / 20)) return false;
            double first = (double)(a - start) / (end - start), second = (double)(b - start) / (end - start);
            return first is >= 0.08 and <= 0.92 && second is >= 0.08 and <= 0.92 && Math.Abs(first - second) <= 0.4;
        }
        MeasuredControl Trigger(InputField f) => new(source, f, Rest(f), Rest(f), Mean(f.Read(full1), f.Read(full2)));
        foreach (var (d, f) in fields)
            if (f.Width >= 2 && HasPartialTravel(f, Math.Max(3, (d.Maximum - d.Minimum) / 3))) analog.Add(Trigger(f));
        if (analog.Count > 0) { Add(Unique(analog, "analog trigger")); return; }
        if (factory)
            throw new InvalidDataException($"The verified {source} field did not show repeatable half and full travel. Capture the trigger again.");
        // If an analog candidate moved during the gesture, a switched digital mirror
        // must never silently replace it when the half-travel evidence is missing.
        if (fields.Any(v => v.Descriptor.Maximum - v.Descriptor.Minimum > 1 &&
            Math.Abs(v.Field.Read(full1) - Rest(v.Field)) > Math.Max(2, (v.Descriptor.Maximum - v.Descriptor.Minimum) / 4)))
            throw new InvalidDataException("The trigger value moved but half travel was unclear. Capture two partial presses and two full presses.");

        // Some controllers advertise a digital trigger but also include an undocumented
        // analog byte. Accept one byte only with full endpoint travel and repeatable
        // intermediate values. Ambiguous packed or split payloads cannot establish a range.
        var hidden = new List<MeasuredControl>();
        bool unclearHiddenTravel = false;
        for (int i = 1; i < signature.Length; i++)
        {
            var f = new InputField(i * 8, 8);
            if (variableBytes[i] || knownButtons[i] != 0 || values.Any(v => v.Field.Overlaps(f))) continue;
            if (HasPartialTravel(f, 3)) hidden.Add(Trigger(f));
            else
            {
                int start = Rest(f), end = Mean(full1[i], full2[i]), span = Math.Abs(end - start);
                // A single bit may be a digital button anywhere in its byte. Multiple
                // stable changed bits need intermediate evidence before a digital mirror
                // can be trusted as the controller's only trigger representation.
                if (span >= 8 && System.Numerics.BitOperations.PopCount((uint)(start ^ end)) > 1 &&
                    Math.Abs(full1[i] - full2[i]) <= Math.Max(1, span / 8) &&
                    Math.Abs(neutral[0][i] - neutral[1][i]) <= Math.Max(1, span / 20))
                    unclearHiddenTravel = true;
            }
        }
        if (hidden.Count > 0)
        {
            if (unclearHiddenTravel)
                throw new InvalidDataException("Multiple undocumented fields moved. Capture the trigger again at half and full travel.");
            var raw = Unique(hidden, "undocumented analog trigger");
            if (Math.Min(raw.First, raw.Last) > 4 || Math.Max(raw.First, raw.Last) < 251)
                throw new InvalidDataException("An undocumented trigger value changed, but a full 8-bit range could not be verified. Check both full and half travel.");
            Add(raw);
            return;
        }
        if (unclearHiddenTravel)
            throw new InvalidDataException("An undocumented trigger field moved but partial travel was unclear. Retry both half presses before assigning a digital trigger.");
        var declaredDigital = fields.Where(v => v.Descriptor.Maximum - v.Descriptor.Minimum == 1)
            .Select(v => v.Field).Where(f => f.Read(full1) != Rest(f) && f.Read(full1) == f.Read(full2) &&
                f.Read(half1) == f.Read(full1) && f.Read(half2) == f.Read(full1)).Select(Trigger).ToArray();
        if (declaredDigital.Length > 0) { Add(Unique(declaredDigital, "two-state trigger")); return; }
        var bits = ButtonCandidates(source, full1, full2).Where(c =>
            c.Field.Read(half1) == c.Last && c.Field.Read(half2) == c.Last);
        Add(Unique(bits, "digital trigger"));
    }

    private static string Canonical(string name) => name switch
    {
        "LS" => "L3", "RS" => "R3", "View" => "Back", "Menu" => "Start", "Up" => "DPadUp",
        "Down" => "DPadDown", "Left" => "DPadLeft", "Right" => "DPadRight", _ => name
    };

    public static void ValidateExtraName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 6 || name.Any(c => c is not (>= 'A' and <= 'Z' or >= '0' and <= '9')))
            throw new InvalidDataException("Use 1–6 uppercase English letters or digits for an extra button.");
        string[] reserved = ["LS", "RS", "VIEW", "MENU", "LT", "RT", "LX", "LY", "RX", "RY", "UP", "DOWN", "LEFT", "RIGHT", "DPAD", "HAT", "NONE", "HOME", "SELECT"];
        if (reserved.Contains(name) || CalibratedLayout.ButtonBits.Keys.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("This name belongs to a standard control. Choose another extra button name.");
    }

    public void BindButton(string source, byte[] pressed1, byte[] pressed2)
    {
        source = Canonical(source);
        if (!CalibratedLayout.ButtonBits.ContainsKey(source)) throw new InvalidDataException("Unknown standard button.");
        CheckSamples(pressed1, pressed2);
        if (factory)
        {
            VerifyFactory(pressed1, pressed2, CalibratedLayout.ButtonBits[source], 8, 0);
            verified = true;
            return;
        }
        Add(Unique(ButtonCandidates(source, pressed1, pressed2), "button"));
    }

    public void BindExtra(string name, byte[] pressed1, byte[] pressed2)
    {
        ValidateExtraName(name);
        if (factory)
        {
            var extra = DeviceLayout.Bluetooth85HF.ExtraSources.FirstOrDefault(e => e.Name == name)
                ?? throw new InvalidDataException("The verified 85HF layout includes L4, R4, PL and PR.");
            CheckSamples(pressed1, pressed2);
            VerifyFactory(pressed1, pressed2, 0, 8, extra.Mask);
            verified = true;
            return;
        }
        if (controls.Any(c => c.ExtraName == name)) throw new InvalidDataException("An extra button already uses this name.");
        if (controls.Count(c => c.ExtraName != null) >= 64) throw new InvalidDataException("This layout already contains 64 extra buttons.");
        CheckSamples(pressed1, pressed2);
        var match = Unique(ButtonCandidates("", pressed1, pressed2), "extra button");
        var prior = Previous.FirstOrDefault(c => c.ExtraName == name);
        string source = prior?.Source ?? CalibratedLayout.ExtraId(match.Field);
        if (prior == null && Previous.Any(c => c.Source == source))
        {
            // A remeasured paddle can keep a legacy bit-based ID after moving. If a
            // new paddle uses its old location, mint a separate stable identity.
            var identity = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { signature, name, match.Field.Offset });
            source = "extra-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(identity))[..16];
        }
        Add(match with { Source = source, ExtraName = name });
    }

    private List<MeasuredControl> ButtonCandidates(string source, byte[] first, byte[] second)
    {
        var matches = new List<MeasuredControl>();
        for (int i = 1; i < signature.Length; i++)
        {
            int candidateBits = variableBytes[i] ? knownButtons[i] : ~idleChanges[i];
            int changed = (first[i] ^ neutral[0][i]) & ~(first[i] ^ second[i]) & candidateBits;
            for (int bit = 0; changed != 0 && bit < 8; bit++)
            {
                if ((changed & (1 << bit)) == 0) continue;
                var f = new InputField(i * 8 + bit, 1);
                if (values.Any(v => (IsMotion(v.Descriptor) || IsHat(v.Descriptor)) && v.Field.Overlaps(f))) continue;
                matches.Add(new(source, f, f.Read(neutral[0]), f.Read(neutral[0]), f.Read(first)));
            }
        }
        return matches;
    }

    private static MeasuredControl Unique(IEnumerable<MeasuredControl> candidates, string kind)
    {
        var matches = candidates.Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(matches.Length == 0
            ? $"No repeatable {kind} field found. Move only this control and retry, or skip this control."
            : $"Multiple fields changed for this {kind}. Keep the other controls released and retry.");
    }

    private void Add(MeasuredControl control)
    {
        if (controls.Any(c => c.Source == control.Source || c.Field.Overlaps(control.Field)))
            throw new InvalidDataException("This physical field is already assigned. Check the control and retry.");
        controls.Add(control);
        IncludeMeasured(control);
    }

    public void BindDpad(byte[][] first, byte[][] second)
    {
        if (first.Length != 4 || second.Length != 4) throw new InvalidDataException("Capture all four D-pad directions twice.");
        CheckSamples(first);
        CheckSamples(second);
        if (factory)
        {
            for (int i = 0; i < 4; i++) VerifyFactory(first[i], second[i], 0, i * 2, 0);
            verified = true;
            return;
        }
        var hats = new List<MeasuredControl>();
        foreach (var (d, f) in values.Where(v => IsHat(v.Descriptor)))
        {
            int rest = Rest(f);
            int[] points = first.Select(report => f.Read(report)).ToArray();
            int start = d.Minimum == rest ? d.Minimum + 1 : d.Minimum;
            if (f.Read(neutral[0]) != f.Read(neutral[1]) || !points.SequenceEqual(second.Select(report => f.Read(report)))) continue;
            foreach (int count in new[] { 4, 8 })
            {
                int end = start + count - 1;
                if (end <= d.Maximum && CalibratedLayout.IsClockwiseHat(f, rest, start, end, points))
                    hats.Add(new("Hat", f, rest, start, end, Cardinals: points));
            }
        }
        if (hats.Count > 0) { Add(Unique(hats, "D-pad hat")); return; }
        var directions = CalibratedLayout.Dpad.Select((source, i) => Unique(
            ButtonCandidates(source, first[i], second[i]), source)).ToArray();
        if (directions.Select(x => x.Field).Distinct().Count() != 4 ||
            directions.Any(d => controls.Any(c => c.Source == d.Source || c.Field.Overlaps(d.Field))))
            throw new InvalidDataException("D-pad directions overlap another control. Capture each direction separately.");
        controls.AddRange(directions);
        foreach (var direction in directions) IncludeMeasured(direction);
    }

    public DeviceLayout Preview()
    {
        if (controls.Count == 0 && !verified) throw new InvalidDataException("Capture at least one control before saving this layout.");
        var measured = controls.ToDictionary(c => c.Source);
        bool replacesDpad = controls.Any(c => c.Source == "Hat" || CalibratedLayout.Dpad.Contains(c.Source));
        var merged = new List<MeasuredControl>();
        foreach (var old in Previous)
        {
            if (measured.Remove(old.Source, out var replacement)) merged.Add(replacement);
            else if (!replacesDpad || old.Source != "Hat" && !CalibratedLayout.Dpad.Contains(old.Source))
            {
                var overlap = controls.FirstOrDefault(c => c.Field.Overlaps(old.Field));
                if (overlap != null)
                    throw new InvalidDataException($"The new input for {overlap.ExtraName ?? overlap.Source} overlaps the saved {old.ExtraName ?? old.Source}. Recheck both inputs before saving.");
                merged.Add(old);
            }
        }
        merged.AddRange(measured.Values);
        return new CalibratedLayout(new(1, signature, name, merged.ToArray(), factory ? DeviceLayout.Bluetooth85HF.Id : null));
    }

    private static void VerifyFactory(byte[] first, byte[] second, ushort buttons, int hat, ulong mask)
    {
        foreach (var report in new[] { first, second })
            if (!DeviceLayout.Bluetooth85HF.TryDecodePhysical(report, out var state, out var extras) ||
                state.Buttons != buttons || state.Hat != hat || (ulong)extras != mask)
                throw new InvalidDataException("The reports differ from the verified 85HF control. Release other buttons and repeat this input.");
    }

    public DeviceLayout Save()
    {
        var layout = (CalibratedLayout)Preview();
        LayoutStore.Save(layout, original);
        return DeviceLayout.Resolve(layout.Id);
    }
}
