using D2XPad.Capture;
using D2XPad.Live;

internal static class HotProfileTests
{
    // Original 32-byte packets from 85HF-20261120-044436-8402ac.ndjson, selected by the
    // button_a, pl_a, button_pr and lt_low_rt_high action labels. Keep the case runnable offline.
    private static readonly byte[] Rest = Convert.FromHexString("010F80808080000000000000281063000000000000000000000000D009BA57D3");
    private static readonly byte[] A = Convert.FromHexString("010F80808080000001000000281063000000000000000000000000C0E9A357D2");
    private static readonly byte[] Pl = Convert.FromHexString("010F8080808000002000000024106300000000000000000000000040CB9D65D1");
    private static readonly byte[] PlAndA = Convert.FromHexString("010F808080800000210000002410630000000000000000000000006083B665CD");
    private static readonly byte[] Pr = Convert.FromHexString("010F8080808000000400000028106300000000000000000000000000E50E5CCA");
    private static readonly byte[] MixedTriggers = Convert.FromHexString("010F80808080AB5B0003000028106300000000000000000000000080338364D8");

    public static void Run(HidCollection device)
    {
        var directory = Directory.CreateTempSubdirectory("D2XPad-HotProfiles-");
        try
        {
            var catalog = new ProfileCatalog(directory.FullName);
            var preset = catalog.OpenEditor("default");
            var last = LiveState.Neutral;
            var session = new PhysicalSession(device, preset.Profile.Bindings, state => last = state, () => last = LiveState.Neutral);
            long initialGeneration = session.Connection.Generation;
            void Packet(byte[] raw, ushort buttons = 0, byte lt = 0, byte rt = 0, byte hat = 8)
            {
                session.Report(device, raw);
                Check(last == (LiveState.Neutral with { Buttons = buttons, Lt = lt, Rt = rt, Hat = hat }), "wrong output for " + Convert.ToHexString(raw));
            }
            void Apply(string selector) => session.ChangeBindings(catalog.Resolve(selector).Bindings);

            Packet(Rest);
            Packet(Pl, 0x1000);
            string first = catalog.Save(preset, "Arena", [new("PL", "Left paddle", "B")]).Name;
            string second = catalog.Save(preset, "Platform", [new("PR", "Dash", "DPadUp")]).Name;
            Apply(first);
            Packet(Pl);
            Check(session.Connection.WaitingForNeutral, "held PL must be released after switching");
            Packet(Rest);
            Packet(Pl, 0x2000);

            foreach (var (target, mask) in new[] { ("X", 0x4000), ("Y", 0x8000) })
            {
                var editing = catalog.OpenEditor(first);
                catalog.Save(editing, first, [new("PL", "Left paddle", target)]);
                Apply(first);
                Check(last == LiveState.Neutral, "editing the active profile must immediately release its previous target");
                Packet(Pl);
                Check(session.Connection.WaitingForNeutral, "repeated identical held packets must stay gated");
                Packet(Rest);
                Packet(Pl, (ushort)mask);
                Packet(PlAndA, (ushort)(mask | 0x1000));
                Packet(A, 0x1000);
                Packet(MixedTriggers, lt: 91, rt: 171);
                Packet(Rest);
                Packet(Pl, (ushort)mask);
            }
            Apply(second);
            Packet(Rest);
            Packet(Pl);
            Packet(Pr, hat: 0);
            Packet(Rest);
            var reopened = new ProfileCatalog(directory.FullName);
            session.ChangeBindings(reopened.Resolve(first).Bindings);
            Packet(Pl, 0x8000);
            session.ChangeBindings(reopened.Resolve(second).Bindings);
            Packet(Rest);
            Packet(Pr, hat: 0);
            Packet(Rest);
            var editingCombo = catalog.OpenEditor(second);
            catalog.SaveMappings(editingCombo, second, [new("extra:PL", [new("RT"), new("LeftStickUp", 75)])]);
            Packet(Pr, hat: 0);
            Apply(second);
            Packet(Pr);
            Check(session.Connection.WaitingForNeutral, "hot-switching to a chord keeps held controls gated");
            Packet(Rest);
            byte[] mixed = (byte[])MixedTriggers.Clone();
            mixed[8] = 0x20;
            session.Report(device, mixed);
            Check(last == (LiveState.Neutral with { Lt = 91, Rt = 255, Ly = 32 }), "new chord in the existing session");
            Packet(MixedTriggers, lt: 91, rt: 171);
            Packet(Rest);
            Check(session.Connection is { Connected: true, WaitingForNeutral: false, Disconnects: 0, Reconnects: 0 }
                && session.Connection.Generation == initialGeneration, "the selected controller connection changed");
            DeadzoneGateChecks(device, catalog, preset);
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles()) file.Delete();
            directory.Delete();
        }
        Console.WriteLine("Passed same-name live edits, chord activation and release, fresh catalog reload and 85HF packet replay.");
    }

    private static void DeadzoneGateChecks(HidCollection device, ProfileCatalog catalog, ProfileEditSnapshot preset)
    {
        const string name = "Deadzone gate fixture";
        catalog.SaveDeadzones(preset, name, new(30, 0, 10));
        catalog.SaveMappings(catalog.OpenEditor(name), name, [new("A", [])]);
        var enabled = catalog.Resolve(name).Bindings;
        byte[] drift = (byte[])Rest.Clone();
        drift[2] = 153;
        byte[] outside = (byte[])drift.Clone();
        outside[2] = 230;
        byte[] saturated = (byte[])drift.Clone();
        saturated[2] = 244;
        byte[] held = (byte[])drift.Clone();
        held[8] = 1;
        LiveState last = LiveState.Neutral;
        var session = new PhysicalSession(device, enabled, state => last = state, () => last = LiveState.Neutral);
        void VerifyReleaseGate(HidCollection current)
        {
            session.Report(current, outside);
            Check(session.Connection.WaitingForNeutral && last == LiveState.Neutral, "a stick outside its deadzone must keep the release gate armed");
            session.Report(current, held);
            Check(session.Connection.WaitingForNeutral && last == LiveState.Neutral, "a held unmapped physical button must keep the release gate armed");
            session.Report(current, drift);
            Check(!session.Connection.WaitingForNeutral && last == LiveState.Neutral && session.Latest?.Raw[2] == 153,
                "stick drift within the deadzone releases the gate without changing physical telemetry");
            session.Report(current, outside);
            Check(last.Lx is > 128 and < 255, "intentional stick movement passes through between the two thresholds");
            session.Report(current, saturated);
            Check(last.Lx == 255, "the loaded outer zone reaches full output after release or reconnect");
        }

        VerifyReleaseGate(device);
        session.SetDevice(null);
        Check(last == LiveState.Neutral, "disconnecting clears the filtered output");
        var returning = device with { Handle = 101, Path = "deadzone-return" };
        session.SetDevice(returning);
        VerifyReleaseGate(returning);

        var running = new PhysicalSession(device, preset.Profile.Bindings, state => last = state, () => last = LiveState.Neutral);
        running.Report(device, Rest);
        running.Report(device, drift);
        Check(last.Lx == 153, "profiles without deadzones preserve the physical movement");
        long generation = running.Connection.Generation;
        running.ChangeBindings(enabled);
        Check(!running.Connection.WaitingForNeutral && last == LiveState.Neutral && running.Connection.Generation == generation,
            "hot apply releases drift on the current attachment with the new deadzone");
        running.Report(device, saturated);
        Check(last.Lx == 255, "hot apply updates the outer cutoff on the current attachment");
        running.Report(device, drift);
        running.ChangeBindings(preset.Profile.Bindings);
        Check(running.Connection.WaitingForNeutral, "hot apply restores the original release tolerance for a zero-deadzone profile");

        byte[] slightMovement = (byte[])Rest.Clone();
        slightMovement[2] = 132;
        var narrowRange = PadBindings.CreateMappings(enabled.Layout, enabled.Mappings, new(0, 0, 99));
        running.Report(device, Rest);
        running.Report(device, slightMovement);
        running.ChangeBindings(narrowRange);
        running.Report(device, slightMovement);
        Check(running.Connection.WaitingForNeutral && last == LiveState.Neutral,
            "a narrow outer cutoff must keep the gate armed when a small physical offset would saturate the output");
        running.Report(device, Rest);
        Check(!running.Connection.WaitingForNeutral && last == LiveState.Neutral, "center must release the narrow range gate");
        running.Report(device, slightMovement);
        Check(last.Lx == 255, "intentional movement after release must still reach the configured outer cutoff");
        Console.WriteLine("Passed deadzone-aware release on startup, reconnect and compatible profile changes.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Hot profile regression: " + message);
    }
}
