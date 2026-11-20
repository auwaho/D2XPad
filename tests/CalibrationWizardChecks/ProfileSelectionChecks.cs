using System.IO;
using System.Reflection;
using System.Text.Json;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class ProfileSelectionChecks
{
    public static void Run(string workspace)
    {
        var other = LayoutStore.Library().Single(entry => !entry.IsBuiltIn).Layout;
        var factory = DeviceLayout.Resolve(DeviceLayout.Bluetooth85HF.Id);
        var catalog = new ProfileCatalog();
        var saved = catalog.SaveMappings(catalog.OpenEditor(other.DefaultProfileName), "Fixture custom", []);
        CheckLiveDefaults(catalog, other, factory, saved.Name);
        var preferences = new DesktopPreferences();
        preferences.SaveProfile(saved.Name, other.Id);
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("D2XPAD_PROFILE_DIR")!, "Broken.json"), "{");
        preferences.SaveProfile("Broken", factory.Id);

        var window = new MainWindow();
        try
        {
            Invoke(window, "LoadProfiles", "Broken", factory);
            Check(Chosen(window) == factory.DefaultProfileName &&
                !catalog.NamesForLayout(factory).Contains(saved.Name) && !catalog.NamesForLayout(factory).Contains("Broken"),
                "An unusable remembered profile must fall back within the selected layout.");
            Invoke(window, "LoadProfiles", null, other);
            Check(Chosen(window) == saved.Name &&
                !catalog.NamesForLayout(other).Contains(factory.DefaultProfileName),
                "Selecting the second layout must recall its own profile and exclude factory profiles.");
            Invoke(window, "LoadProfiles", saved.Name, factory);
            Check(Chosen(window) == factory.DefaultProfileName, "An explicit foreign preferred name must be ignored.");

            Field("frame").SetValue(window, new EngineFrame("fixture", "default", 0, true, false, false,
                0, null, null, LayoutId: factory.Id));
            Invoke(window, "LoadProfiles", "default", factory);
            try
            {
                ((Task)Invoke(window, "ApplyProfile", saved.Name)!).GetAwaiter().GetResult();
                throw new InvalidOperationException("A foreign profile reached live selection.");
            }
            catch (InvalidDataException) { }

            string file = Path.Combine(workspace, "foreign-profile.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                name = "Imported second controller",
                device = other.Id,
                mappings = Array.Empty<object>()
            }));
            ((Task)Invoke(window, "ImportProfileFrom", file)!).GetAwaiter().GetResult();
            Check(Chosen(window) == factory.DefaultProfileName &&
                catalog.NamesForLayout(other).Contains("Imported second controller") &&
                !catalog.NamesForLayout(factory).Contains("Imported second controller"),
                "An import for a second layout must commit without altering the live selection.");
            Field("frame").SetValue(window, null);
            Invoke(window, "LoadProfiles", "Imported second controller", other);
            Check(Chosen(window) == "Imported second controller", "The imported profile must be selectable on its own layout.");
        }
        finally { window.Close(); }
        Console.WriteLine("WPF profiles passed layout filtering, corrupt fallback, per-layout recall and foreign live import.");
    }

    private static void CheckLiveDefaults(ProfileCatalog catalog, DeviceLayout learned, DeviceLayout factory, string customName)
    {
        string legacy = Path.Combine(catalog.DirectoryPath, "85hf-bindings.json");
        File.WriteAllText(legacy, """{ "L4": "X", "R4": "RB", "PL": "A", "PR": "B" }""");
        try
        {
            var selected = D2XPad.Live.Program.SelectProfile(catalog, null, learned);
            Check(selected.Selector == learned.DefaultProfileName && selected.Profile.Bindings.Layout.Id == learned.Id,
                "The live CLI must default to the selected calibrated layout even with a legacy 85HF profile present.");
            Check(D2XPad.Live.Program.SelectProfile(catalog, customName, learned).Selector == customName,
                "The live CLI must honor an explicit profile name.");
            Check(D2XPad.Live.Program.SelectProfile(catalog, null, factory).Selector == legacy &&
                D2XPad.Live.Program.SelectProfile(catalog, factory.DefaultProfileName, factory).Selector == factory.DefaultProfileName,
                "The live CLI must keep the legacy 85HF fallback and allow explicitly overriding it.");
        }
        finally { File.Delete(legacy); }
        Console.WriteLine("Live CLI profiles passed calibrated defaults, explicit selection and legacy 85HF compatibility.");
    }

    private static object? Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static FieldInfo Field(string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string? Chosen(MainWindow window) => (string?)Field("chosenProfile").GetValue(window);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
