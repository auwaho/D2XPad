using System.Text.Json;
using D2XPad.Live;

namespace D2XPad.Desktop;

internal sealed class DesktopPreferences
{
    private sealed record Settings(string? Profile = null, bool StartMinimized = false, bool RememberProfile = true,
        string? DevicePath = null, string? LayoutId = null, Dictionary<string, string>? ProfilesByLayout = null,
        Dictionary<string, bool>? VibrationByLayout = null);

    private readonly string path = Path.GetFullPath(Environment.GetEnvironmentVariable("D2XPAD_DESKTOP_SETTINGS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D2XPad", "desktop.json"));
    private Settings? loaded;
    private Settings Current => loaded ??= Load();
    public bool StartMinimized => Current.StartMinimized;
    public bool RememberProfile => Current.RememberProfile;
    public string? DevicePath => Current.DevicePath;
    public string? LayoutId => Current.LayoutId;

    internal static bool SupportsVibration(string? layoutId) => layoutId == DeviceLayout.Bluetooth85HF.Id;

    public bool VibrationEnabled(string layoutId) =>
        SupportsVibration(layoutId) && (Current.VibrationByLayout?.GetValueOrDefault(layoutId, true) ?? true);

    public void SaveVibration(string layoutId, bool enabled)
    {
        if (!SupportsVibration(layoutId) || VibrationEnabled(layoutId) == enabled) return;
        var settings = Current;
        var values = new Dictionary<string, bool>(settings.VibrationByLayout ?? []) { [layoutId] = enabled };
        Save(settings with { VibrationByLayout = values });
    }

    public string? ReadProfile(string? layoutId = null)
    {
        var preferences = Current;
        if (!preferences.RememberProfile) return null;
        if (layoutId == null) return preferences.Profile;
        if (preferences.ProfilesByLayout is { Count: > 0 } profiles)
            return profiles.GetValueOrDefault(layoutId);
        // Desktop settings written before per-layout recall stored only the last profile.
        return preferences.LayoutId == null || preferences.LayoutId == layoutId ? preferences.Profile : null;
    }

    private Settings Load()
    {
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (JsonSerializer.Deserialize<Settings>(File.ReadAllText(candidate)) is { } value) return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return new();
    }

    public void SaveProfile(string profile, string? layoutId = null)
    {
        var preferences = Current;
        if (!preferences.RememberProfile) return;
        if (preferences.Profile == profile && (layoutId == null ||
            preferences.ProfilesByLayout?.GetValueOrDefault(layoutId) == profile)) return;
        var profiles = new Dictionary<string, string>(preferences.ProfilesByLayout ?? [], StringComparer.OrdinalIgnoreCase);
        if (layoutId != null) profiles[layoutId] = profile;
        Save(preferences with { Profile = profile, ProfilesByLayout = profiles });
    }

    public void SaveDevice(string devicePath, string layoutId)
    {
        var preferences = Current;
        if (preferences.DevicePath == devicePath && preferences.LayoutId == layoutId) return;
        Save(preferences with { DevicePath = devicePath, LayoutId = layoutId });
    }

    public void SaveOptions(bool startMinimized, bool rememberProfile)
    {
        var preferences = Current;
        if (preferences.StartMinimized == startMinimized && preferences.RememberProfile == rememberProfile) return;
        Save(preferences with { StartMinimized = startMinimized, RememberProfile = rememberProfile });
    }

    private void Save(Settings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
            loaded = value;
        }
        finally { File.Delete(temp); }
    }
}
