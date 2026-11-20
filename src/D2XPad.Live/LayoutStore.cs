using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2XPad.Live;

internal sealed record ControllerEntry(DeviceLayout Layout, bool IsBuiltIn, bool IsCustomized, bool IsArchived, string Revision);

internal static class LayoutStore
{
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false
    };
    private static string? cachedDir;
    private static DateTime cachedWrite;
    private static IReadOnlyList<ControllerEntry>? cached;
    private static string DirectoryName => Environment.GetEnvironmentVariable("D2XPAD_LAYOUT_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D2XPad", "layouts");
    private static ControllerEntry Factory => new(DeviceLayout.Bluetooth85HF, true, false, false, "factory");

    internal static IReadOnlyList<DeviceLayout> All => Library().Where(c => !c.IsArchived).Select(c => c.Layout).ToArray();

    internal static IReadOnlyList<ControllerEntry> Library()
    {
        lock (Gate)
        {
            string dir = DirectoryName;
            DateTime stamp = Directory.GetLastWriteTimeUtc(dir);
            if (cached != null && cachedDir == dir && cachedWrite == stamp) return cached;
            var entries = new Dictionary<string, ControllerEntry> { [Factory.Layout.Id] = Factory };
            if (Directory.Exists(dir))
            {
                foreach (string path in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(path);
                    if (ext is not (".json" or ".archived")) continue;
                    try
                    {
                        var entry = Read(path);
                        entries[entry.Layout.Id] = entry;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                    { System.Diagnostics.Trace.TraceWarning($"Could not load controller layout '{path}': {ex.Message}"); }
                }
            }
            cachedDir = dir;
            cachedWrite = stamp;
            return cached = entries.Values.OrderBy(e => !e.IsBuiltIn)
                .ThenBy(e => e.Layout.DisplayName, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        }
    }

    private static ControllerEntry Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 256 * 1024) throw new InvalidDataException("Layout file is too large.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var layout = new CalibratedLayout(JsonSerializer.Deserialize<LayoutFile>(bytes, Json) ??
            throw new InvalidDataException("Empty layout."));
        bool archived = Path.GetExtension(path) == ".archived";
        if (layout.Id != Path.GetFileNameWithoutExtension(path) || archived && layout.Is85HF)
            throw new InvalidDataException("The controller file does not match its identity.");
        return new(layout, layout.Is85HF, true, archived, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static void Save(CalibratedLayout layout, ControllerEntry? original = null)
    {
        if (original != null && original.Layout.Id != layout.Id)
            throw new InvalidDataException("The calibration belongs to another controller.");
        Change(layout.Id, original, (path, _) =>
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, layout.File, Json);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, Path.ChangeExtension(path, ".bak"));
                else File.Move(temp, path);
            }
            finally { File.Delete(temp); }
        });
    }

    internal static void Forget(ControllerEntry original)
    {
        if (original.IsBuiltIn || original.IsArchived) throw new InvalidDataException("Choose an active user controller to forget.");
        Change(original.Layout.Id, original, (path, archive) => File.Move(path, archive));
    }

    internal static void RestoreArchived(ControllerEntry original)
    {
        if (!original.IsArchived || original.IsBuiltIn) throw new InvalidDataException("Choose an archived controller to restore.");
        Change(original.Layout.Id, original, (path, archive) => File.Move(archive, path));
    }

    internal static void RestoreDefault(ControllerEntry original)
    {
        if (!original.IsBuiltIn || !original.IsCustomized || original.IsArchived)
            throw new InvalidDataException("Choose a customized built-in controller to restore.");
        Change(original.Layout.Id, original, (path, _) => File.Move(path, Path.ChangeExtension(path, ".bak"), overwrite: true));
    }

    private static void Change(string id, ControllerEntry? original, Action<string, string> apply)
    {
        lock (Gate)
        {
            string dir = DirectoryName;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, id + ".json"), archive = Path.ChangeExtension(path, ".archived");
            using var lease = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var current = File.Exists(path) ? Read(path) : File.Exists(archive) ? Read(archive) :
                id == Factory.Layout.Id ? Factory : null;
            if (current?.Revision != original?.Revision || current?.IsArchived != original?.IsArchived)
                throw new IOException("This controller was changed or archived. Reopen the controller library before continuing.");
            apply(path, archive);
            cached = null;
        }
    }
}
