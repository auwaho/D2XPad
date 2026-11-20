using D2XPad.Capture;
using D2XPad.Live;

internal static class ControllerLibraryTests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidDataException(name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return; }
        throw new InvalidDataException(name);
    }

    public static void Run()
    {
        string? previous = Environment.GetEnvironmentVariable("D2XPAD_LAYOUT_DIR");
        var temp = Directory.CreateTempSubdirectory("D2XPad-Library-");
        try
        {
            string layouts = Path.Combine(temp.FullName, "layouts");
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", layouts);
            var catalog = new ProfileCatalog(Path.Combine(temp.FullName, "profiles"));
            Learned(layouts, catalog);
            BuiltIn(layouts, catalog);
        }
        finally
        {
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", previous);
            temp.Delete(recursive: true);
        }
        Console.WriteLine("Passed offline controller catalog, archive/restore, stale edits, stable profile sources, 85HF override and factory reset checks.");
    }

    private static void Learned(string layouts, ProfileCatalog profiles)
    {
        var pad = new HidCollection(3, "usb-pad", 0x1234, 0x5678, 1, 5, "USB", "instance", 3, [0], []);
        var initial = new ControllerCalibration(pad, "First");
        initial.AddNeutral([0, 0, 0]); initial.AddNeutral([0, 0, 0]);
        initial.BindButton("A", [0, 1, 0], [0, 1, 0]);
        initial.BindExtra("P1", [0, 0, 1], [0, 0, 1]);
        var layout = initial.Save();
        var original = LayoutStore.Library().Single(e => e.Layout.Id == layout.Id);
        var profile = profiles.Save(profiles.OpenEditor(layout.DefaultProfileName), "Arcade", [new("bit-16", "P1", "Y")]);
        var editor = profiles.OpenEditor(profile.Name);
        var bytes = File.ReadAllBytes(editor.ManagedPath!);
        var another = new ControllerCalibration(pad with { ProductId = 0x5679 }, "Second");
        another.AddNeutral([0, 0, 0]); another.AddNeutral([0, 0, 0]);
        another.BindButton("B", [0, 0, 4], [0, 0, 4]);
        string second = another.Save().Id;
        Check(LayoutStore.Library().Count == 3, "Two learned layouts must coexist with the 85HF offline");
        Reject(() => new ControllerCalibration(pad with { ProductId = 0x5679 }, "Oops", original), "Another collection edited the controller");

        var updated = new ControllerCalibration(pad, "First updated", original);
        updated.AddNeutral([0, 0, 0]); updated.AddNeutral([0, 0, 0]);
        updated.BindExtra("P1", [0, 0, 2], [0, 0, 2]);
        updated.BindExtra("P2", [0, 0, 1], [0, 0, 1]);
        var staging = updated.Preview();
        Check(staging.ExtraSources[0] == layout.ExtraSources[0] && staging.ExtraSources[1].Id != "bit-16" &&
            staging.HasStandardInput("A") && PhysicalSession.LayoutFor(pad)?.DisplayName == "First", "Nonmutating partial preview");
        updated.Save();
        Reject(() => updated.Save(), "Concurrent recalibration replacement succeeded");
        Reject(() => LayoutStore.Forget(original), "Stale forget succeeded");
        var active = LayoutStore.Library().Single(e => e.Layout.Id == layout.Id);
        Check(File.Exists(Path.Combine(layouts, layout.Id + ".bak")) && active.Layout.DisplayName == "First updated", "Replacement backup");
        var loaded = profiles.Resolve(profile.Name).Bindings;
        Check(loaded.TryDecode([0, 1, 2], out var mapped, out _) && mapped.Buttons == 0x9000, "Profile lost source after it moved");
        Check(loaded.TryDecode([0, 0, 1], out mapped, out _) && mapped.Buttons == 0, "New paddle inherited old assignment");
        LayoutStore.Forget(active);
        var archived = LayoutStore.Library().Single(e => e.Layout.Id == layout.Id && e.IsArchived);
        Check(PhysicalSession.LayoutFor(pad) == null && !profiles.Names().Contains(profile.Name) &&
            !profiles.Names().Contains(layout.DefaultProfileName) && profiles.Names().Contains(second), "Archived controller stayed active");
        Check(File.ReadAllBytes(editor.ManagedPath!).SequenceEqual(bytes), "Archive changed profile bytes");
        Reject(() => profiles.Resolve(profile.Name), "Opened profile of archived controller");
        Reject(() => profiles.Save(profiles.OpenEditor(second), profile.Name, []), "Archived profile name was reused");
        Reject(() => profiles.Delete(editor), "Stale editor deleted archived profile");
        LayoutStore.RestoreArchived(archived);
        Check(PhysicalSession.LayoutFor(pad)?.Id == layout.Id && profiles.Names().Contains(profile.Name) &&
            profiles.Resolve(profile.Name).Bindings.TryDecode([0, 1, 2], out mapped, out _) && mapped.Buttons == 0x9000,
            "Restoring controller did not reactivate original profile");
    }

    private static void BuiltIn(string layouts, ProfileCatalog profiles)
    {
        var pad = new HidCollection(4, "bt-pad", 0x2dc8, 0x2067, 1, 5, "Bluetooth", "instance", 32, [1, 3], []);
        var original = LayoutStore.Library().Single(e => e.IsBuiltIn);
        var draft = new ControllerCalibration(pad, "85HF custom", original);
        byte[] Report(byte y = 142, byte extra = 0)
        {
            byte[] r = new byte[32];
            r[0] = 1; r[1] = 15; r[2] = r[4] = r[5] = 128; r[3] = y; r[8] = extra;
            return r;
        }
        draft.AddNeutral(Report()); draft.AddNeutral(Report());
        var telemetry = Report();
        telemetry[30] = 173;
        Check(draft.IsNeutral(telemetry), "85HF telemetry was treated as control movement");
        draft.BindAxis("LY", Report(19), Report(19), Report(237), Report(237));
        // Actual lt_sweep reports from 85HF-20261120-044436-8402ac: LT 123, 130, 255, 255.
        draft.BindTrigger("LT", Convert.FromHexString("010F80808080007B00010000241063000000000000000000000000D0EA9162D4"),
            Convert.FromHexString("010F80808080008200010000241063000000000000000000000000E0119262D4"),
            Convert.FromHexString("010F8080808000FF00010000281063000000000000000000000000501CA962CE"),
            Convert.FromHexString("010F8080808000FF000100002810630000000000000000000000006043A962D6"));
        draft.BindExtra("PL", Report(extra: 32), Report(extra: 32));
        Reject(() => draft.BindExtra("P5", Report(extra: 32), Report(extra: 32)), "Invented a fifth 85HF extra");
        Reject(() => draft.BindExtra("PR", Report(extra: 32), Report(extra: 32)), "Incorrect factory source verification succeeded");
        Check(DeviceLayout.Resolve(original.Layout.Id) == DeviceLayout.Bluetooth85HF, "Preview changed factory state");
        var modified = draft.Save();
        Check(modified.Id == original.Layout.Id && modified.ExtraSources.SequenceEqual(original.Layout.ExtraSources) &&
            modified.DefaultProfileName == "default", "Factory ID or canonical mappings changed");
        var baseline = profiles.Resolve("default").Bindings;
        Check(baseline.TryDecode(Convert.FromHexString("010F80808080AB5B0003000028106300000000000000000000000040CB9D65D1"), out var mixed, out _) &&
            mixed.Lt == 91 && mixed.Rt == 171, "Independent trigger samples after 85HF recalibration");
        Check(baseline.Layout == modified && baseline.TryDecode(Report(19, 32), out var applied, out _) &&
            applied.Ly == 0 && applied.Buttons == 0x1000 && modified.IsPhysicallyReleased(Report()), "Legacy profile bypassed native override");
        Check(PhysicalSession.LayoutFor(pad with { ButtonSignature = "changed" }) == DeviceLayout.Bluetooth85HF,
            "Custom calibration leaked across descriptor formats");
        var other = pad with { ButtonSignature = "changed" };
        LiveState output = LiveState.Neutral;
        var fallback = new PhysicalSession(other, baseline, s => output = s, () => output = LiveState.Neutral);
        fallback.Report(other, Report(128)); fallback.Report(other, Report(19, 32));
        Check(output.Ly == 19 && output.Buttons == 0x1000, "Mismatched descriptor did not use factory decoder");
        var customized = LayoutStore.Library().Single(e => e.IsBuiltIn);
        Reject(() => LayoutStore.Forget(customized), "Factory layout was forgotten");
        LayoutStore.RestoreDefault(customized);
        Check(DeviceLayout.Resolve(modified.Id) == DeviceLayout.Bluetooth85HF && File.Exists(Path.Combine(layouts, modified.Id + ".bak")) &&
            profiles.Resolve("default").Bindings.TryDecode(Report(19, 32), out var factory, out _) &&
            factory.Ly == 19 && factory.Buttons == 0x1000, "Failed to restore native 85HF decoding");
    }
}
