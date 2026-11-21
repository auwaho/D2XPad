using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace D2XPad.ServiceSetup;

internal static class Program
{
    private const string ServiceName = "D2XPadConversion";
    private const string Marker = "D2XPad.ServiceSetup";
    private const string ServiceExe = "D2XPad.Live.exe";
    private const string LicenseName = "D2XPad-LICENSE";
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly string[] Payload =
    [
        ServiceExe, "D2XPad.Live.dll", "D2XPad.Live.deps.json", "D2XPad.Live.runtimeconfig.json",
        "HIDMaestro.Core.dll", "HIDMaestro/LICENSE", "HIDMaestro/THIRD-PARTY-NOTICES.txt"
    ];
    private static string ProgramRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "D2XPad");
    private static string InstallDirectory => Path.Combine(ProgramRoot, "ConversionService");
    private static bool TrustedOwner(SecurityIdentifier sid) => sid.Equals(Administrators) || sid.Equals(SystemAccount);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint window, string message, string caption, uint type);

    private static int Main(string[] args)
    {
        try
        {
            if (args is not [var action, var owner] || (action != "install" && action != "remove"))
                throw new ArgumentException("Open D2XPad Settings to manage the conversion service.");

            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Windows administrator approval is required.");

            var user = new SecurityIdentifier(owner);
            if (!Equals(user, identity.User) && !Equals(user, InteractiveUser()))
                throw new UnauthorizedAccessException("The service owner must be the user who opened D2XPad.");
            using var mutex = new Mutex(false, @"Global\D2XPad.ServiceSetup");
            bool locked;
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(4)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("Another conversion service setup is running.");
            try
            {
                if (action == "install") Install(user);
                else Remove(user);
            }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }
        catch (Exception error)
        {
            _ = MessageBoxW(0, error.Message, "D2XPad conversion service", 0x10);
            return 1;
        }
    }

    private static SecurityIdentifier? InteractiveUser()
    {
        using var process = Process.GetCurrentProcess();
        string name = ReadSessionString(process.SessionId, 5);
        string domain = ReadSessionString(process.SessionId, 7);
        return name.Length == 0 ? null
            : (SecurityIdentifier)new NTAccount(domain, name).Translate(typeof(SecurityIdentifier));
    }

    private static string ReadSessionString(int session, int informationClass)
    {
        if (!WTSQuerySessionInformationW(0, session, informationClass, out nint data, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the Windows session owner.");
        try { return Marshal.PtrToStringUni(data) ?? ""; }
        finally { WTSFreeMemory(data); }
    }

    private static string ServiceCommand(SecurityIdentifier user) =>
        $"\"{Path.Combine(InstallDirectory, ServiceExe)}\" --conversion-service {user.Value}";

    private static void Install(SecurityIdentifier user)
    {
        using var manager = OpenManager(3);
        using (var existing = OpenServiceW(manager, ServiceName, 4))
        {
            int error = Marshal.GetLastWin32Error();
            if (!existing.IsInvalid) throw new IOException("The conversion service is already installed.");
            if (error != 1060) throw new Win32Exception(error, "Could not check whether the conversion service exists.");
        }
        var hashes = ReadPayloadHashes();
        CreateProtectedDirectory(ProgramRoot);
        if (Directory.Exists(InstallDirectory)) RemovePayload();
        CreateProtectedDirectory(InstallDirectory);
        try
        {
            using (var marker = new StreamWriter(CreateProtectedFile(Path.Combine(InstallDirectory, Marker))))
                marker.Write(Marker);
            CreateProtectedDirectory(Path.Combine(InstallDirectory, "HIDMaestro"));
            foreach (string relativePath in Payload)
                CopyVerifiedFile(relativePath, hashes[relativePath]);
            using (var license = typeof(Program).Assembly.GetManifestResourceStream(LicenseName)!)
            using (var file = CreateProtectedFile(Path.Combine(InstallDirectory, LicenseName)))
                license.CopyTo(file);

            using var service = CreateServiceW(manager, ServiceName, "D2XPad Conversion Service", 0x10034,
                0x10, 2, 1, ServiceCommand(user), null, 0, null, null, null);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the conversion service.");
            try
            {
                if (!StartServiceW(service, 0, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the conversion service.");
                WaitForState(service, 4);
            }
            catch
            {
                try { Stop(service); }
                finally { _ = DeleteService(service); }
                throw;
            }
        }
        catch
        {
            using var remaining = OpenServiceW(manager, ServiceName, 4);
            if (remaining.IsInvalid && Marshal.GetLastWin32Error() == 1060)
            {
                try { RemovePayload(); }
                catch (IOException) { } // The protected setup files can be removed on the next attempt.
            }
            throw;
        }
    }

    private static Dictionary<string, byte[]> ReadPayloadHashes()
    {
        using var manifest = typeof(Program).Assembly.GetManifestResourceStream("ServicePayload.sha256")
            ?? throw new InvalidDataException("Extract the complete D2XPad portable ZIP before installing the service.");
        using var reader = new StreamReader(manifest);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split('|');
            if (fields.Length != 2 || !Payload.Contains(fields[0], StringComparer.Ordinal) || fields[1].Length != 64)
                throw new InvalidDataException("The packaged conversion service manifest is invalid.");
            hashes.Add(fields[0], Convert.FromHexString(fields[1]));
        }
        if (hashes.Count != Payload.Length)
            throw new InvalidDataException("The packaged conversion service manifest is incomplete.");
        return hashes;
    }

    private static void CopyVerifiedFile(string relativePath, byte[] hash)
    {
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, relativePath));
        using var target = CreateProtectedFile(Path.Combine(InstallDirectory, relativePath));
        source.CopyTo(target);
        target.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(target)))
            throw new InvalidDataException($"The package copy of {relativePath} does not match service setup. Re-extract the original ZIP.");
    }

    private static void Remove(SecurityIdentifier user)
    {
        RequireProtectedDirectory(ProgramRoot);
        RequireProtectedDirectory(InstallDirectory);
        using var manager = OpenManager(1);
        using (var service = OpenServiceW(manager, ServiceName, 0x10025))
        {
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the conversion service.");
            if (!string.Equals(ReadServiceCommand(service), ServiceCommand(user), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The installed conversion service belongs to another Windows user or installation.");
            Stop(service);
            if (!DeleteService(service)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not unregister the conversion service.");
        }
        RemovePayload();
    }

    private static void Stop(ServiceHandle service)
    {
        var running = ReadProcessState(service);
        if (running.Status.CurrentState == 1) return;
        using var process = OpenServiceProcess(service, running.ProcessId);
        if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() is not (1061 or 1062))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop the conversion service.");
        WaitForState(service, 1);
        if (process == null) return;

        // SCM reports Stopped before the executable can finish shutting down and release its image.
        uint result = WaitForSingleObject(process, 10000);
        if (result == 258) throw new IOException("The conversion service process has not exited yet. Try removing it again.");
        if (result != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not wait for the conversion service process.");
    }

    private static SafeProcessHandle? OpenServiceProcess(ServiceHandle service, uint processId)
    {
        if (processId == 0) return null;
        const uint synchronizeAndQuery = 0x100000 | 0x1000;
        var process = OpenProcess(synchronizeAndQuery, false, processId);
        int error = Marshal.GetLastWin32Error();
        try
        {
            var current = ReadProcessState(service);
            if (current.ProcessId != processId && current.Status.CurrentState != 1)
                throw new IOException("The conversion service process changed. Try removing it again.");
            if (process.IsInvalid)
            {
                if (error == 87 && current.Status.CurrentState == 1) return null;
                throw new Win32Exception(error, "Could not open the conversion service process.");
            }
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length))
            {
                if (WaitForSingleObject(process, 0) == 0 && current.Status.CurrentState == 1) return null;
                throw new IOException("Could not verify the conversion service process. Try removing it again.");
            }
            if (!string.Equals(path.ToString(), Path.Combine(InstallDirectory, ServiceExe), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The conversion service process is not the installed D2XPad executable.");
            var verified = process;
            process = null;
            return verified;
        }
        finally { process?.Dispose(); }
    }

    private static ServiceStatusProcess ReadProcessState(ServiceHandle service) =>
        QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _) ? status
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the conversion service process state.");

    private static ServiceStatus ReadState(ServiceHandle service) =>
        QueryServiceStatus(service, out var status) ? status
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the conversion service state.");

    private static void WaitForState(ServiceHandle service, uint expected)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            var state = ReadState(service);
            if (state.CurrentState == expected) return;
            if (expected == 4 && state.CurrentState == 1)
                throw new Win32Exception((int)state.Win32ExitCode, "The conversion service exited before it was ready.");
            Thread.Sleep(150);
        }
        throw new IOException("The conversion service did not reach its requested state in time.");
    }

    private static string? ReadServiceCommand(ServiceHandle service)
    {
        _ = QueryServiceConfigW(service, 0, 0, out uint bytes);
        if (Marshal.GetLastWin32Error() != 122 || bytes > 64 * 1024)
            throw new IOException("Could not inspect the existing conversion service.");
        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (!QueryServiceConfigW(service, buffer, bytes, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the existing conversion service.");
            return Marshal.PtrToStringUni(Marshal.PtrToStructure<ServiceConfig>(buffer).BinaryPath);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void CreateProtectedDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            Protect(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            new DirectoryInfo(path).Create(security);
        }
        RequireProtectedDirectory(path);
    }

    private static FileStream CreateProtectedFile(string path)
    {
        var security = new FileSecurity();
        Protect(security, InheritanceFlags.None);
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.None, security);
    }

    private static void Protect(FileSystemSecurity security, InheritanceFlags inheritable)
    {
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Administrators);
        foreach (var sid in new[] { Administrators, SystemAccount, Users })
            security.AddAccessRule(new FileSystemAccessRule(sid,
                TrustedOwner(sid) ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute,
                inheritable, PropagationFlags.None, AccessControlType.Allow));
    }

    private static void RequireProtectedDirectory(string path)
    {
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The conversion service folder is redirected.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !TrustedOwner(owner) ||
            !security.AreAccessRulesProtected ||
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 &&
                !TrustedOwner((SecurityIdentifier)rule.IdentityReference)))
            throw new UnauthorizedAccessException("The conversion service folder is not controlled by Windows administrators.");
    }

    private static void RemovePayload()
    {
        RequireProtectedDirectory(InstallDirectory);
        var marker = Path.Combine(InstallDirectory, Marker);
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(marker) != Marker)
            throw new IOException("An existing unrecognized conversion service folder was preserved.");
        var thirdParty = Path.Combine(InstallDirectory, "HIDMaestro");
        if (Directory.Exists(thirdParty)) RequireProtectedDirectory(thirdParty);
        foreach (string file in Payload.Append(LicenseName))
            File.Delete(Path.Combine(InstallDirectory, file));
        if (Directory.Exists(thirdParty)) Directory.Delete(thirdParty);
        File.Delete(marker);
        Directory.Delete(InstallDirectory);
    }

    private static ServiceHandle OpenManager(uint access)
    {
        var manager = OpenSCManagerW(null, null, access);
        if (!manager.IsInvalid) return manager;
        int error = Marshal.GetLastWin32Error();
        manager.Dispose();
        throw new Win32Exception(error, "Could not open the Windows service manager.");
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, Checkpoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public ServiceStatus Status;
        public uint ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public nint BinaryPath, LoadOrderGroup;
        public uint Tag;
        public nint Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("wtsapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out uint bytes);
    [DllImport("wtsapi32", ExactSpelling = true)]
    private static extern void WTSFreeMemory(nint memory);
    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string displayName, uint access,
        uint type, uint start, uint errorControl, string binary, string? loadOrderGroup, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(ServiceHandle service, uint argumentCount, nint arguments);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, int bytes, out uint needed);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(ServiceHandle service, nint config, uint bytes, out uint needed);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(ServiceHandle service);

    [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
}
