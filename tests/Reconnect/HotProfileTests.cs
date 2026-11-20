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
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles()) file.Delete();
            directory.Delete();
        }
        Console.WriteLine("Passed same-name live edits, chord activation and release, fresh catalog reload and 85HF packet replay.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Hot profile regression: " + message);
    }
}
