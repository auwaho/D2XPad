using Microsoft.Win32;

namespace D2XPad.Desktop;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "D2XPad";

    private static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "D2XPad.exe");
    private static string Command => $"\"{ExecutablePath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("D2XPad.exe could not be found.", ExecutablePath);
        var command = Command;
        if (command.Length > 260) throw new InvalidOperationException("The D2XPad path is too long for Windows startup.");

        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        run.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
