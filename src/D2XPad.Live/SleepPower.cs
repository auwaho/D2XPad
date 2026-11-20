using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace D2XPad.Live;

// LastSleepTime and LastWakeTime are kernel interrupt-time counts in 100 ns units.
internal sealed record KernelPowerTimes(ulong LastSleep100ns, ulong LastWake100ns)
{
    public bool CompletedCycleSince(KernelPowerTimes before) =>
        LastSleep100ns > before.LastSleep100ns && LastWake100ns > before.LastWake100ns && LastWake100ns >= LastSleep100ns;
}

internal static class SleepPower
{
    internal static KernelPowerTimes ReadTimes() => new(ReadTime(15), ReadTime(14));

    private static ulong ReadTime(int level)
    {
        int status = CallNtPowerInformation(level, 0, 0, out ulong time, sizeof(ulong));
        if (status != 0) throw new IOException($"Windows power information level {level} failed with NTSTATUS 0x{status:X8}.");
        return time;
    }

    internal static void RequestSleep()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
        if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception();
        var enabled = new TokenPrivileges { Count = 1, Privilege = luid, Attributes = 2 };
        bool adjusted = AdjustTokenPrivileges(identity.AccessToken, false, ref enabled,
            (uint)Marshal.SizeOf<TokenPrivileges>(), out var original, out _);
        int error = Marshal.GetLastPInvokeError();
        if (!adjusted || error != 0)
            throw new Win32Exception(error, $"Windows denied the sleep privilege (Win32 error {error}).");
        try
        {
            if (SetSuspendState(0, 0, 0) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            if (original.Count != 0 && !AdjustTokenPrivileges(identity.AccessToken, false, ref original,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out _, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Privilege; public uint Attributes; }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern int CallNtPowerInformation(int level, nint input, uint inputSize, out ulong output, uint outputSize);
    [DllImport("powrprof.dll", ExactSpelling = true, SetLastError = true)]
    private static extern byte SetSuspendState(byte hibernate, byte force, byte disableWakeEvents);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TokenPrivileges change, uint previousSize, out TokenPrivileges previous, out uint returned);
}
