using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2XPad.Live;

internal sealed record ControllerProfile(string Name, PadBindings Bindings)
{
    public JsonElement Assignments => Bindings.Assignments;
}

internal sealed record ProfileEditSnapshot(ControllerProfile Profile, string? ManagedPath, string? Revision)
{
    public bool CanReplace => ManagedPath != null;
}

internal sealed class ProfileCatalog
{
    private const string Device = "85hf-bluetooth";
    private static readonly string[] BuiltIns = ["default", "southpaw-buttons"];
    private static readonly StringComparer NamesEqual = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };
    private readonly string directory;
    private readonly bool migrateLegacy;

    public ProfileCatalog(string? userDirectory = null)
    {
        var configured = userDirectory ?? Environment.GetEnvironmentVariable("D2XPAD_PROFILE_DIR");
        migrateLegacy = configured == null;
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured ?? ProfileStorage.ManagedDirectory));
    }

    internal string DirectoryPath
    {
        get
        {
            if (migrateLegacy) ProfileStorage.EnsureMigrated(directory);
            return directory;
        }
    }

    public IReadOnlyList<string> Names() => Names(includeArchived: false);

    private IReadOnlyList<string> Names(bool includeArchived)
    {
        var archived = LayoutStore.Library().Where(c => c.IsArchived).Select(c => c.Layout.Id).ToHashSet();
        var visible = new List<string>(PresetNames(includeArchived));
        foreach (var (name, file) in NamedFiles())
            if (includeArchived || archived.Count == 0 || !BelongsToArchivedController(file, archived)) visible.Add(name);
        return visible.Order(NamesEqual).ToArray();
    }

    public IReadOnlyList<string> NamesForLayout(DeviceLayout layout)
    {
        var visible = new List<string>();
        if (layout.Id == Device) visible.AddRange(BuiltIns);
        else if (DeviceLayout.Supported.Any(candidate => candidate.Id == layout.Id)) visible.Add(layout.DefaultProfileName);
        foreach (var (name, file) in NamedFiles())
        {
            try
            {
                var profile = LoadFile(file, importing: false, out _);
                if (profile.Bindings.Layout.Id == layout.Id && NamesEqual.Equals(profile.Name, name))
                    visible.Add(name);
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                // The desktop picker needs usable profiles. Direct catalog access still reports the specific error.
            }
        }
        return visible.Order(NamesEqual).ToArray();
    }

    public bool IsCompatible(string name, DeviceLayout layout)
    {
        try { return Resolve(name).Bindings.Layout.Id == layout.Id; }
        catch (Exception ex) when (IsUnavailable(ex)) { return false; }
    }

    private static bool IsUnavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException;

    private IEnumerable<(string Name, string Path)> NamedFiles()
    {
        var names = new HashSet<string>(PresetNames(includeArchived: true), NamesEqual);
        foreach (string file in Files())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            ValidateName(name);
            if (!names.Add(name)) throw new InvalidDataException($"Profile name conflict: {name} in {directory}.");
            yield return (name, file);
        }
    }

    private static bool BelongsToArchivedController(string file, HashSet<string> archived)
    {
        using var stream = OpenForRead(file);
        try
        {
            using var doc = JsonDocument.Parse(stream, StrictJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("device", out var id) &&
                id.ValueKind == JsonValueKind.String && archived.Contains(id.GetString()!);
        }
        catch (JsonException) { return false; } // Keep a malformed profile visible so it can be diagnosed.
    }

    public ControllerProfile Resolve(string nameOrPath) => OpenEditor(nameOrPath).Profile;

    public ProfileEditSnapshot OpenEditor(string nameOrPath)
    {
        if (Path.IsPathRooted(nameOrPath) || nameOrPath.Contains('/') || nameOrPath.Contains('\\')
            || nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return new(LoadFile(nameOrPath, importing: false, out _), null, null);

        ValidateName(nameOrPath);
        string? builtIn = BuiltIns.FirstOrDefault(n => NamesEqual.Equals(n, nameOrPath));
        var learned = DeviceLayout.Supported.FirstOrDefault(layout => layout.Id != Device &&
            NamesEqual.Equals(layout.DefaultProfileName, nameOrPath));
        var files = Files().Where(f => NamesEqual.Equals(Path.GetFileNameWithoutExtension(f), nameOrPath)).ToArray();
        if (files.Length + (builtIn == null ? 0 : 1) + (learned == null ? 0 : 1) > 1)
            throw new InvalidDataException($"Profile name conflict: {nameOrPath} in {directory}.");
        if (builtIn != null) return new(ReadBuiltIn(builtIn), null, null);
        if (learned != null) return new(new(learned.DefaultProfileName, PadBindings.CreateMappings(learned, [])), null, null);
        if (files.Length == 0) throw new FileNotFoundException($"Unknown profile '{nameOrPath}'. Run profiles to list available names.");
        var loaded = LoadFile(files[0], importing: false, out string revision);
        if (!NamesEqual.Equals(loaded.Name, nameOrPath))
            throw new InvalidDataException($"The profile name '{loaded.Name}' does not match catalog file '{files[0]}'.");
        return new(loaded, files[0], revision);
    }

    public ControllerProfile Import(string path, string? name = null)
    {
        var loaded = LoadFile(path, importing: true, out _);
        var profile = loaded with { Name = name ?? loaded.Name };
        return Write(profile, original: null);
    }

    public ControllerProfile Save(ProfileEditSnapshot original, string name, IEnumerable<ExtraAssignment> assignments)
    {
        var previous = original.Profile.Bindings;
        return SaveBindings(original, name, PadBindings.Create(previous.Layout, assignments, previous.Deadzones));
    }

    public ControllerProfile SaveMappings(ProfileEditSnapshot original, string name, IEnumerable<InputMapping> mappings) =>
        SaveProfile(original, name, mappings, original.Profile.Bindings.Deadzones);

    public ControllerProfile SaveDeadzones(ProfileEditSnapshot original, string name, StickDeadzones deadzones) =>
        SaveProfile(original, name, original.Profile.Bindings.Mappings, deadzones);

    public ControllerProfile SaveProfile(ProfileEditSnapshot original, string name, IEnumerable<InputMapping> mappings, StickDeadzones deadzones) =>
        SaveBindings(original, name, PadBindings.CreateMappings(original.Profile.Bindings.Layout, mappings, deadzones));

    private ControllerProfile SaveBindings(ProfileEditSnapshot original, string name, PadBindings bindings)
    {
        ValidateName(name);
        bool replace = original.CanReplace && NamesEqual.Equals(name, original.Profile.Name);
        return Write(new(replace ? original.Profile.Name : name, bindings), replace ? original : null);
    }

    public void Delete(ProfileEditSnapshot original, Action? beforeRemove = null)
    {
        string destination = ManagedFile(original);
        ChangeCurrentFile(destination, original, () =>
        {
            string backup = Path.ChangeExtension(destination, ".bak");
            bool replace = File.Exists(backup);
            if (replace)
            {
                try
                {
                    var previous = LoadFile(backup, importing: false, out _);
                    if (!NamesEqual.Equals(previous.Name, original.Profile.Name))
                        throw new InvalidDataException("The backup belongs to another profile.");
                }
                catch (InvalidDataException ex)
                {
                    throw new IOException($"The existing backup '{backup}' is not a valid backup of '{original.Profile.Name}'. Move it before deleting this profile.", ex);
                }
            }
            beforeRemove?.Invoke();
            // A same-directory rename removes the profile from the catalog and retains its last version.
            File.Move(destination, backup, overwrite: replace);
        });
    }

    private string ManagedFile(ProfileEditSnapshot original)
    {
        string name = original.Profile.Name;
        EnsureLayoutAvailable(original.Profile);
        ValidateName(name);
        if (original.ManagedPath == null || PresetNames().Contains(name, NamesEqual))
            throw new InvalidDataException("Only user profiles opened by name from the catalog can be changed.");
        if (!NamesEqual.Equals(original.ManagedPath, Path.Combine(directory, name + ".json")))
            throw new IOException("This profile does not match its catalog file. Reopen it before continuing.");
        return original.ManagedPath;
    }

    private ControllerProfile Write(ControllerProfile profile, ProfileEditSnapshot? original)
    {
        EnsureLayoutAvailable(profile);
        ValidateName(profile.Name);
        Directory.CreateDirectory(DirectoryPath);
        string destination = original == null ? Path.Combine(directory, profile.Name + ".json") : ManagedFile(original);
        if (original == null && Names(includeArchived: true).Contains(profile.Name, NamesEqual))
            throw new IOException($"A profile named '{profile.Name}' already exists. Choose another name to save a copy.");
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var deadzones = profile.Bindings.Deadzones;
                bool hasOuterDeadzones = deadzones.LeftOuter != 0 || deadzones.RightOuter != 0;
                JsonSerializer.Serialize(stream, new
                {
                    schemaVersion = hasOuterDeadzones ? 5 : deadzones == default ? 3 : 4,
                    name = profile.Name,
                    device = profile.Bindings.Layout.Id,
                    mappings = profile.Bindings.Mappings.Select(m => new
                    {
                        source = m.Source,
                        actions = m.Actions.Select(a => new { target = a.Target, strength = a.Strength })
                    }),
                    stickDeadzones = deadzones == default ? null : new
                    {
                        left = deadzones.Left,
                        right = deadzones.Right,
                        leftOuter = hasOuterDeadzones ? (int?)deadzones.LeftOuter : null,
                        rightOuter = hasOuterDeadzones ? (int?)deadzones.RightOuter : null
                    }
                },
                    new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
                stream.Flush(flushToDisk: true);
            }
            if (original == null) File.Move(temporary, destination, overwrite: false);
            else ChangeCurrentFile(destination, original, () => File.Replace(temporary, destination, Path.ChangeExtension(destination, ".bak")));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return profile;
    }

    private static void EnsureLayoutAvailable(ControllerProfile profile)
    {
        if (LayoutStore.Library().Any(e => e.IsArchived && e.Layout.Id == profile.Bindings.Layout.Id))
            throw new InvalidDataException("Restore the archived controller before changing its profiles.");
    }

    private static void ChangeCurrentFile(string destination, ProfileEditSnapshot original, Action change)
    {
        // Keep an edit and a deletion of the same confirmed revision mutually exclusive.
        using var lease = new FileStream(Path.ChangeExtension(destination, ".lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (!File.Exists(destination))
            throw new IOException($"Profile '{original.Profile.Name}' was removed from the catalog.");
        using var existing = OpenForRead(destination);
        string current = Convert.ToHexString(SHA256.HashData(existing));
        if (!string.Equals(current, original.Revision, StringComparison.Ordinal))
            throw new IOException($"Profile '{original.Profile.Name}' changed. Reopen the profile before continuing.");
        change();
    }

    private IEnumerable<string> Files()
    {
        string path = DirectoryPath;
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly) : [];
    }

    private static IEnumerable<string> PresetNames(bool includeArchived = false) => BuiltIns.Concat(LayoutStore.Library()
        .Where(entry => !entry.IsBuiltIn && (includeArchived || !entry.IsArchived)).Select(entry => entry.Layout.DefaultProfileName));

    private static ControllerProfile ReadBuiltIn(string name)
    {
        using var file = typeof(ProfileCatalog).Assembly.GetManifestResourceStream($"D2XPad.Profiles.{name}.json")
            ?? throw new FileNotFoundException($"The built-in profile '{name}' is missing.");
        using var doc = JsonDocument.Parse(file, StrictJson);
        return Parse(doc.RootElement, name, importing: false);
    }

    private static FileStream OpenForRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static ControllerProfile LoadFile(string path, bool importing, out string revision)
    {
        using var file = OpenForRead(path);
        revision = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        try
        {
            using var doc = JsonDocument.Parse(file, StrictJson);
            return Parse(doc.RootElement, Path.GetFileNameWithoutExtension(path), importing);
        }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid profile JSON in '{path}': {ex.Message}", ex); }
    }

    private static ControllerProfile Parse(JsonElement root, string fileName, bool importing)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A profile must be a JSON object.");
        if (root.TryGetProperty("schemaVersion", out var version))
        {
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v is < 1 or > 5)
                throw new InvalidDataException("Unsupported D2XPad profile schemaVersion. Expected a version from 1 to 5.");
            if (root.EnumerateObject().Count() != (v >= 4 ? 5 : 4))
                throw new InvalidDataException("Unexpected or missing controller profile fields.");
            string name = ReadText(root, "name");
            string device = ReadText(root, "device");
            ValidateName(name);
            if (v == 1)
            {
                if (device != Device || !root.TryGetProperty("bindings", out var bindings))
                    throw new InvalidDataException($"Version 1 profiles require device '{Device}' and bindings.");
                return new(name, PadBindings.Parse(bindings));
            }
            var layout = DeviceLayout.Resolve(device);
            if (v >= 3)
            {
                if (!root.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Version {v} profiles require a mappings array.");
                var deadzones = v >= 4 ? ParseDeadzones(root, v) : default;
                return new(name, PadBindings.CreateMappings(layout, mappings.EnumerateArray().Select(ParseMapping), deadzones));
            }
            if (!root.TryGetProperty("extraButtons", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Version 2 profiles require an extraButtons array.");
            var extras = new List<ExtraAssignment>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3)
                    throw new InvalidDataException("Each extra button requires source, name and target.");
                extras.Add(new(ReadText(entry, "source"), ReadText(entry, "name"), ReadText(entry, "target")));
            }
            return new(name, PadBindings.Create(layout, extras));
        }
        if (root.TryGetProperty("reports", out _))
        {
            if (!importing) throw new InvalidDataException("Use import FILE to validate and convert this UR-XD HID map first.");
            UrXd85HfMap.Validate(root);
            string importedName = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : fileName;
            return ReadBuiltIn("default") with { Name = importedName };
        }
        return new(fileName, PadBindings.Parse(root));
    }

    private static StickDeadzones ParseDeadzones(JsonElement root, int version)
    {
        bool hasOuterDeadzones = version == 5;
        if (!root.TryGetProperty("stickDeadzones", out var zones) || zones.ValueKind != JsonValueKind.Object ||
            zones.EnumerateObject().Count() != (hasOuterDeadzones ? 4 : 2))
            throw new InvalidDataException($"Version {version} profiles require a valid stickDeadzones object.");
        int left = ReadInteger(zones, "left");
        int right = ReadInteger(zones, "right");
        return hasOuterDeadzones
            ? new(left, right, ReadInteger(zones, "leftOuter"), ReadInteger(zones, "rightOuter"))
            : new(left, right);
    }

    private static int ReadInteger(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int integer)
            ? integer : throw new InvalidDataException($"Expected an integer for '{key}'.");

    private static InputMapping ParseMapping(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 ||
            !row.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Each mapping requires a source and an array of actions.");
        return new(ReadText(row, "source"), actions.EnumerateArray().Select(action =>
        {
            if (action.ValueKind != JsonValueKind.Object || action.EnumerateObject().Count() != 2 ||
                !action.TryGetProperty("strength", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int strength))
                throw new InvalidDataException("Each action requires a target and an integer strength.");
            return new XboxAction(ReadText(action, "target"), strength);
        }).ToArray());
    }

    private static string ReadText(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException($"Expected a string for '{key}'.");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.')
            || name.Length > 250 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid profile name '{name}'. Use a file name without the .json extension.");
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]))
            throw new InvalidDataException($"'{name}' is a reserved Windows device name.");
    }
}
