using System.Text.Json;
using System.Text.Json.Nodes;
using D2XPad.Live;

internal static class ProfileCatalogTests
{
    public static void Run(string? capturePath)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string workspace = Path.Combine(tempRoot, $"D2XPad-Profiles-{Guid.NewGuid():N}");
        string store = Path.Combine(workspace, "catalog");
        Directory.CreateDirectory(workspace);
        var catalog = new ProfileCatalog(store);
        try
        {
            var alternate = catalog.Resolve("SOUTHPAW-BUTTONS");
            Check(alternate.Name == "southpaw-buttons" && alternate.Assignments.GetProperty("L4").GetString() == "RB", "built-in selection");
            Check(catalog.Resolve("default").Bindings.UsesReplayDefaults && !Directory.Exists(store), "read without creating the store");
            string file = Path.Combine(workspace, "game.json");
            File.WriteAllText(file, alternate.Assignments.GetRawText());
            var imported = catalog.Import(file, "Game profile");
            Check(JsonElement.DeepEquals(imported.Assignments, catalog.Resolve("game PROFILE").Assignments), "catalog round-trip");
            string installed = Path.Combine(store, "Game profile.json");
            string initial = File.ReadAllText(installed);
            Reject(() => catalog.Import(file, "game PROFILE"));
            Reject(() => catalog.Import(file, "DEFAULT"));
            Reject(() => catalog.Import(file, "../escape"));
            Reject(() => catalog.Import(file, "NUL"));
            Check(File.ReadAllText(installed) == initial, "conflicts preserve the installed file");

            var native = JsonNode.Parse(initial)!.AsObject();
            foreach (var (field, value) in new[] { ("schemaVersion", (JsonNode)99), ("device", (JsonNode)"usb"), ("unknown", (JsonNode)true) })
            {
                var broken = native.DeepClone();
                broken[field] = value;
                File.WriteAllText(file, broken.ToJsonString());
                Reject(() => catalog.Import(file, "invalid"));
            }
            File.WriteAllText(file, """{"L4":"A","R4":"LB","PL":"X","PR":"None","L4":"B"}""");
            Reject(() => catalog.Resolve(file));
            File.WriteAllText(file, """{"L4":"Typo","R4":"LB","PL":"X","PR":"None"}""");
            Reject(() => catalog.Import(file, "invalid"));
            Check(catalog.Names().SequenceEqual(DeviceLayout.Supported.Select(l => l.DefaultProfileName)
                .Concat(["Game profile", "southpaw-buttons"]).Order(StringComparer.OrdinalIgnoreCase)), "stable catalog after failed imports");

            File.WriteAllText(file, alternate.Assignments.GetRawText());
            int created = 0;
            Parallel.For(0, 8, _ =>
            {
                try { catalog.Import(file, "race"); Interlocked.Increment(ref created); }
                catch (IOException) { }
            });
            Check(created == 1 && catalog.Resolve("race").Name == "race" && !Directory.EnumerateFiles(store, "*.tmp").Any(),
                "concurrent import creates one complete file");
            ProfileStorageTests.Run(workspace);
            EditorChecks(catalog, store, file);
            DeletionChecks(catalog, store, file);
            MappingCatalogChecks(catalog, store, file);

            if (capturePath != null)
            {
                using var reader = File.OpenText(capturePath);
                var map = JsonNode.Parse(reader.ReadLine()!)!["profile"]!;
                File.WriteAllText(file, map.ToJsonString());
                Check(catalog.Import(file).Bindings.UsesReplayDefaults, "verified capture map");
                var reports = map["reports"]!.AsObject();
                reports["-1_0"] = reports["0"]!.DeepClone();
                File.WriteAllText(file, map.ToJsonString());
                Reject(() => catalog.Import(file, "ambiguous"));
                reports.Remove("0");
                File.WriteAllText(file, map.ToJsonString());
                Check(catalog.Import(file, "normalized").Bindings.UsesReplayDefaults, "single -1_0 report");
                var inputs = reports["-1_0"]!["inputs"]!;
                inputs["lt"]!["byte"] = 28;
                inputs["rt"]!["byte"] = 28;
                inputs["ry"]!["byte"] = 28;
                inputs["dpad"]!["byte"] = 28;
                inputs["a"]!["byte"] = 32;
                File.WriteAllText(file, map.ToJsonString());
                string error = Reject(() => catalog.Import(file, "invalid"));
                Check(error.Contains("overlap at byte 28") && error.Contains("outside the 32-byte report"), "conflict and bounds diagnostics");
            }
            Console.WriteLine("Profile catalog checks passed for presets, import, editable extras, deletion, backups, stale edits and concurrent writers.");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(workspace)) == Path.TrimEndingDirectorySeparator(tempRoot))
                Directory.Delete(workspace, recursive: true);
        }
    }

    private static void EditorChecks(ProfileCatalog catalog, string store, string external)
    {
        var baseline = catalog.OpenEditor("default");
        var readOnly = catalog.OpenEditor(external);
        Check(!baseline.CanReplace && !readOnly.CanReplace, "built-in and external profiles are copy-only");
        Reject(() => catalog.Save(baseline, baseline.Profile.Name, []));
        Reject(() => catalog.Save(readOnly, "Game profile", []));
        string externalText = File.ReadAllText(external);
        var row = baseline.Profile.Bindings.ExtraAssignments.First() with { Name = "Paddle", Target = "X" };
        var custom = catalog.Save(readOnly, "Editor test", [row]);
        var opened = catalog.OpenEditor("editor TEST");
        Check(File.ReadAllText(external) == externalText && opened.CanReplace
            && opened.Profile.Bindings.ExtraAssignments.SequenceEqual(new[] { row with { Name = row.Source } }), "source and target round-trip with calibrated label");
        string destination = Path.Combine(store, "Editor test.json");
        string priorBytes = File.ReadAllText(destination);
        int saved = 0;
        Parallel.For(0, 8, _ =>
        {
            try { catalog.Save(opened, custom.Name, []); Interlocked.Increment(ref saved); }
            catch (IOException) { }
        });
        Check(saved == 1 && catalog.Resolve(custom.Name).Bindings.ExtraAssignments.Count == 0,
            "one concurrent editor wins and an empty extra list remains empty");
        Check(File.ReadAllText(Path.ChangeExtension(destination, ".bak")) == priorBytes, "backup preserves the previous JSON");
        Check(Reject(() => catalog.Save(opened, custom.Name, [row])).Contains("changed"), "stale edit detected");
        Check(catalog.Save(opened, "Recovered draft", [row]).Bindings.ExtraAssignments.Count == 1, "stale draft can save a copy");
        catalog.Import(Path.ChangeExtension(destination, ".bak"), "Restored backup");
        Check(catalog.Resolve("Restored backup").Bindings.ExtraAssignments.Single() == row with { Name = row.Source }, "backup recovery");

        var second = catalog.OpenEditor(custom.Name);
        Reject(() => catalog.Save(second, custom.Name, [row, row]));
        Reject(() => catalog.Save(second, custom.Name, [row with { Source = "Foreign button from another layout" }]));
        string anotherSource = baseline.Profile.Bindings.ExtraAssignments.First(a => a.Source != row.Source).Source;
        Reject(() => catalog.Save(second, custom.Name, [row, row with { Source = anotherSource, Name = row.Name.ToUpperInvariant() }]));
        Reject(() => catalog.Save(second, custom.Name, [row with { Source = "A" }]));
        Reject(() => catalog.Save(second, custom.Name, [row with { Target = "LeftTrigger" }]));
        Reject(() => catalog.Save(second, "com1.pad", [row]));
        File.Delete(destination);
        Reject(() => catalog.Save(second, custom.Name, [row]));
        Check(!File.Exists(destination) && !Directory.EnumerateFiles(store, "*.tmp").Any(), "failed edits leave the store consistent");
    }

    private static void MappingCatalogChecks(ProfileCatalog catalog, string store, string external)
    {
        File.WriteAllText(external, """
            {"schemaVersion":2,"name":"Legacy","device":"85hf-bluetooth","extraButtons":[
              {"source":"PL","name":"Custom jump paddle","target":"Y"}]}
            """);
        string legacy = File.ReadAllText(external);
        var migrated = catalog.Import(external, "Migrated paddle");
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(store, "Migrated paddle.json")))!;
        Check((int)saved["schemaVersion"]! == 3 && saved["extraButtons"] == null &&
            (string?)saved["mappings"]![0]!["source"] == "extra:PL" && File.ReadAllText(external) == legacy,
            "legacy import writes v3 by source and leaves the original file alone");
        Check(migrated.Bindings.Sources.Single(s => s.Id == "extra:PL").Name == "PL" &&
            migrated.Bindings.GetActions("extra:PL").SequenceEqual([new XboxAction("Y")]), "legacy v2 target and fixed name");
        var chord = new XboxAction[] { new("RT"), new("LeftStickUp", 75) };
        var snapshot = catalog.OpenEditor("Migrated paddle");
        catalog.SaveMappings(snapshot, "Multi", [new("extra:L4", chord), new("A", []), new("LT", [new("Y")])]);
        chord[0] = new("LT");
        var reopened = new ProfileCatalog(store).Resolve("Multi").Bindings;
        Check(reopened.Mappings.Count == 3 && reopened.GetActions("extra:L4").SequenceEqual(
            [new XboxAction("RT"), new XboxAction("LeftStickUp", 75)]) && reopened.GetActions("A").Count == 0 &&
            reopened.GetActions("LT").Single().Target == "Y" && reopened.GetActions("RT").Single().Target == "RT" &&
            reopened.GetActions("extra:PL").Count == 0, "schema v3 chord, unbind, unchanged standard and unassigned extra round-trip");
        string multi = File.ReadAllText(Path.Combine(store, "Multi.json"));
        foreach (string invalid in new[] {
            multi.Replace("\"strength\": 75", "\"strength\": 0"),
            multi.Replace("\"strength\": 75", "\"strength\": 1.5"),
            multi.Replace("\"target\": \"RT\"", "\"unexpected\": \"RT\""),
            multi.Replace("\"target\": \"LeftStickUp\"", "\"target\": \"RT\"")
        })
        {
            File.WriteAllText(external, invalid);
            Reject(() => catalog.Import(external, "Broken chord"));
        }
        Check(!catalog.Names().Contains("Broken chord"), "invalid chords leave no profile");
    }

    private static void DeletionChecks(ProfileCatalog catalog, string store, string external)
    {
        const string name = "Delete fixture";
        string path = Path.Combine(store, name + ".json");
        string backup = Path.ChangeExtension(path, ".bak");
        string source = File.ReadAllText(external);
        catalog.Import(external, name);
        var first = catalog.OpenEditor(name);
        catalog.Save(first, name, []);
        var current = catalog.OpenEditor(name.ToUpperInvariant());
        string latest = File.ReadAllText(path), previous = File.ReadAllText(backup);
        int callbacks = 0;
        void Count() => callbacks++;
        Reject(() => catalog.Delete(catalog.OpenEditor("default"), Count));
        Reject(() => catalog.Delete(catalog.OpenEditor("southpaw-buttons"), Count));
        Reject(() => catalog.Delete(catalog.OpenEditor(external), Count));
        Reject(() => catalog.Delete(catalog.OpenEditor(path), Count));
        Reject(() => new ProfileCatalog(Path.Combine(store, "other")).Delete(current, Count));
        Reject(() => catalog.Delete(current with { ManagedPath = Path.Combine(store, "Game profile.json") }, Count));
        Check(Reject(() => catalog.Delete(first, Count)).Contains("changed"), "stale deletion rejected");
        Check(callbacks == 0 && File.ReadAllText(path) == latest && File.ReadAllText(backup) == previous,
            "protected or stale deletions leave the session and catalog unchanged");

        string foreign = File.ReadAllText(Path.Combine(store, "Game profile.json"));
        File.WriteAllText(backup, foreign);
        Reject(() => catalog.Delete(current, Count));
        Check(callbacks == 0 && File.ReadAllText(backup) == foreign && File.ReadAllText(path) == latest,
            "an unrelated backup survives a name collision");
        File.WriteAllText(backup, previous);
        Reject(() => catalog.Delete(current, () => throw new IOException("Default profile switch failed.")));
        Check(File.ReadAllText(path) == latest && File.ReadAllText(backup) == previous,
            "failed pre-deletion switch preserves both revisions");

        catalog.Delete(current, () =>
        {
            Count();
            Reject(() => catalog.Save(current, name, first.Profile.Bindings.ExtraAssignments));
        });
        Check(callbacks == 1 && !catalog.Names().Contains(name) && !File.Exists(path)
            && File.ReadAllText(backup) == latest && File.ReadAllText(external) == source,
            "atomic deletion retains the latest revision and preserves the import source");
        catalog.Import(backup, "Restored deletion");
        Check(catalog.Resolve("Restored deletion").Bindings.ExtraAssignments.Count == 0, "deleted profile recovery");
        Reject(() => catalog.Delete(current, Count));
        Check(callbacks == 1, "missing profile does not invoke the pre-deletion switch");
        catalog.Import(external, name);
        string recreated = File.ReadAllText(path);
        catalog.Delete(catalog.OpenEditor(name));
        Check(File.ReadAllText(backup) == recreated, "deleting a recreated name updates its backup");
    }

    private static void Check(bool pass, string detail)
    {
        if (!pass) throw new InvalidDataException("Profile test failed: " + detail);
    }

    private static string Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return ex.Message; }
        throw new InvalidDataException("Expected the invalid profile operation to fail.");
    }
}
