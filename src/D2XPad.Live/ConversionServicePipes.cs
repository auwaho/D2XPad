using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace D2XPad.Live;

internal static class ConversionServicePipes
{
    private const uint FirstInstance = 0x00080000, Overlapped = 0x40000000, RejectRemote = 8;
    private const int WtsConnectState = 8, WtsActive = 0;

    // FILE_GENERIC_WRITE grants FILE_APPEND_DATA, which also grants creation of more pipe server instances.
    private const PipeAccessRights ClientRights = PipeAccessRights.ReadData | PipeAccessRights.WriteData |
        PipeAccessRights.ReadAttributes | PipeAccessRights.WriteAttributes | PipeAccessRights.ReadPermissions |
        PipeAccessRights.Synchronize;

    private static string Name(SecurityIdentifier owner) => "D2XPad.Conversion.v1." + owner.Value;

    internal static NamedPipeServerStream OpenServiceListener(SecurityIdentifier owner) =>
        OpenListener(Name(owner), owner, PipeDirection.InOut);

    internal static NamedPipeClientStream OpenServiceConnection(SecurityIdentifier owner) =>
        new(".", Name(owner), ClientRights, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification,
            HandleInheritability.None);

    internal static NamedPipeServerStream OpenListener(string name, SecurityIdentifier owner, PipeDirection direction)
    {
        string sddl = $"D:P(A;;GA;;;SY)(A;;0x{(int)ClientRights:X8};;;{owner.Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, (uint)direction | FirstInstance | Overlapped,
                RejectRemote, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, "Could not create the local conversion service channel.");
            }
            try { return new NamedPipeServerStream(direction, true, false, handle); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { LocalFree(descriptor); }
    }

    internal static uint VerifyOwner(NamedPipeServerStream pipe, SecurityIdentifier allowedOwner)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint process) || process == 0 ||
            !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint session) || session == 0 ||
            !IsActiveSession(session))
            throw new UnauthorizedAccessException("Only the authorized user in an active Windows session can use the conversion service.");

        bool matches = false;
        pipe.RunAsClient(() =>
        {
            using var client = WindowsIdentity.GetCurrent(true);
            matches = client?.User == allowedOwner;
        });
        if (!matches) throw new UnauthorizedAccessException("This Windows account is not authorized for the conversion service.");
        return process;
    }

    internal static void VerifyServer(NamedPipeClientStream pipe, uint expectedProcess)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint found) || found != expectedProcess)
            throw new UnauthorizedAccessException("The conversion channel did not belong to the expected process.");
    }

    internal static void VerifyClient(NamedPipeServerStream pipe, uint expectedProcess)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint found) || found != expectedProcess)
            throw new UnauthorizedAccessException("The vibration channel did not belong to the conversion service.");
    }

    private static bool IsActiveSession(uint session)
    {
        if (!WTSQuerySessionInformationW(nint.Zero, session, WtsConnectState, out nint state, out uint size))
            return false;
        try { return size >= sizeof(int) && Marshal.ReadInt32(state) == WtsActive; }
        finally { WTSFreeMemory(state); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Size;
        internal nint Descriptor;
        internal int Inherit;
    }

    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out nint descriptor, out uint size);

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);

    [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);

    [DllImport("wtsapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint session, int information,
        out nint buffer, out uint bytes);

    [DllImport("wtsapi32", ExactSpelling = true)]
    private static extern void WTSFreeMemory(nint buffer);
}
