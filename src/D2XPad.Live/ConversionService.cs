using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace D2XPad.Live;

internal enum ConversionServiceStatus { Missing, Stopped, Running, Starting, Unavailable }
internal enum LiveOutputMode { Auto, Portable, Service }

internal static class ConversionService
{
    internal const string Name = "D2XPadConversion";
    internal const int RequestSize = 17;
    private const uint ScmConnect = 1, QueryStatus = 4, OwnProcess = 0x10;
    private const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const uint AcceptStop = 1, AcceptShutdown = 4;
    private const uint ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5;

    internal static ConversionServiceStatus Status() => ReadStatus(out _);

    internal static ConversionServiceStatus ReadStatus(out uint processId)
    {
        processId = 0;
        using var manager = OpenSCManagerW(null, null, ScmConnect);
        if (manager.IsInvalid) return ConversionServiceStatus.Unavailable;
        using var service = OpenServiceW(manager, Name, QueryStatus);
        if (service.IsInvalid)
            return Marshal.GetLastPInvokeError() == 1060 ? ConversionServiceStatus.Missing : ConversionServiceStatus.Unavailable;
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            return ConversionServiceStatus.Unavailable;
        processId = status.ProcessId;
        return status.State switch
        {
            Stopped => ConversionServiceStatus.Stopped,
            StartPending => ConversionServiceStatus.Starting,
            Running when processId != 0 => ConversionServiceStatus.Running,
            _ => ConversionServiceStatus.Unavailable
        };
    }

    internal static int Run(string ownerSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (!identity.IsSystem || process.SessionId != 0) return 2;
        var owner = new SecurityIdentifier(ownerSid);
        if (owner == identity.User) return 2;

        using var host = new ServiceHost(owner);
        ServiceMain main = host.Start;
        ServiceTableEntry[] services =
        [
            new() { Name = Name, Main = Marshal.GetFunctionPointerForDelegate(main) },
            default
        ];
        if (!StartServiceCtrlDispatcherW(services)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        GC.KeepAlive(main);
        return 0;
    }

    private sealed class ServiceHost(SecurityIdentifier owner) : IDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private nint handle;
        private ServiceControl? handler;
        private readonly object gate = new();
        private ServiceStatus status;

        internal void Start(uint argumentCount, nint arguments)
        {
            handler = Control;
            handle = RegisterServiceCtrlHandlerExW(Name, handler, nint.Zero);
            if (handle == nint.Zero) return;
            try
            {
                Report(StartPending);
                using var listener = ConversionServicePipes.OpenServiceListener(owner);
                Report(Running);
                ServeAsync(listener, stop.Token).GetAwaiter().GetResult();
                Report(Stopped);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Report(Stopped); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Report(Stopped, 1066, 2);
            }
            finally { GC.KeepAlive(handler); }
        }

        private async Task ServeAsync(NamedPipeServerStream listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(token);
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = new byte[RequestSize];
                    await listener.ReadExactlyAsync(request, handshake.Token);
                    uint client = ConversionServicePipes.VerifyOwner(listener, owner);
                    if (request[0] > 1) throw new InvalidDataException("Invalid conversion service request.");
                    var session = new Guid(request.AsSpan(1));
                    if (session == Guid.Empty) throw new InvalidDataException("Invalid conversion service session.");
                    await LiveOutput.ServeHostAsync(listener, session.ToString("N"), request[0] == 1, token, client);

                    // DisconnectNamedPipe discards unread replies. The client closes after reading CLOSED or ERROR.
                    using var closing = CancellationTokenSource.CreateLinkedTokenSource(token);
                    closing.CancelAfter(TimeSpan.FromSeconds(2));
                    if (await listener.ReadAsync(request.AsMemory(0, 1), closing.Token) != 0)
                        throw new InvalidDataException("Unexpected data after the conversion session ended.");
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (!token.IsCancellationRequested) Console.Error.WriteLine(ex.Message);
                }
                listener.Disconnect();
            }
        }

        private uint Control(uint command, uint eventType, nint eventData, nint context)
        {
            switch (command)
            {
                case ControlStop:
                case ControlShutdown:
                    Report(StopPending);
                    _ = stop.CancelAsync();
                    return 0;
                case ControlInterrogate:
                    lock (gate) SetServiceStatus(handle, ref status);
                    return 0;
                default: return 120;
            }
        }

        private void Report(uint state, uint exitCode = 0, uint serviceExitCode = 0)
        {
            lock (gate)
            {
                status = new()
                {
                    Type = OwnProcess, State = state, Controls = state == Running ? AcceptStop | AcceptShutdown : 0,
                    ExitCode = exitCode, ServiceExitCode = serviceExitCode,
                    Checkpoint = state is StartPending or StopPending ? 1U : 0,
                    WaitHint = state is StartPending or StopPending ? 30000U : 0
                };
                SetServiceStatus(handle, ref status);
            }
        }

        public void Dispose() => stop.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMain(uint argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControl(uint command, uint eventType, nint eventData, nint context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Name;
        internal nint Main;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint Type, State, Controls, ExitCode, ServiceExitCode, Checkpoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal ServiceStatus Status;
        internal uint ProcessId, Flags;
        internal readonly uint State => Status.State;
    }

    private sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerExW(string name, ServiceControl control, nint context);

    [DllImport("advapi32", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);

    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int infoLevel, out ServiceStatusProcess status,
        int size, out uint needed);

    [DllImport("advapi32", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
