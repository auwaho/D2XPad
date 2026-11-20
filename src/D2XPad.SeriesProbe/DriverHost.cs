using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace D2XPad.SeriesProbe;

// The observer owns the pipe. The elevated host accepts only built-in poses.
// EOF, a failed observer or an idle timeout releases the controller.
internal static class DriverHost
{
    private const string Prefix = "D2XPad.SeriesProbe.";
    public static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public static async Task<int> RunAsync(string session)
    {
        if (!Guid.TryParseExact(session, "N", out _) || !IsAdmin()) return 2;
        using var client = new NamedPipeClientStream(".", Prefix + session, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(12000);
        using var reader = new StreamReader(client);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        try
        {
            using (var output = new SeriesOutput())
            {
                await writer.WriteLineAsync("READY");
                while (true)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120));
                    if (line is null or "STOP") break;
                    var pose = line == ProbeCases.Identify.Id ? ProbeCases.Identify :
                        ProbeCases.All.FirstOrDefault(f => f.Id == line);
                    if (pose == null) throw new InvalidDataException("Unknown test state.");
                    output.Submit(pose);
                    await writer.WriteLineAsync("OK");
                }
            }
            await writer.WriteLineAsync("CLOSED");
            return 0;
        }
        catch (Exception ex)
        {
            if (client.IsConnected)
                await writer.WriteLineAsync("ERROR " + ex.GetBaseException().Message.ReplaceLineEndings(" "));
            return 1;
        }
    }

    internal sealed class Connection : IAsyncDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly Process host;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private bool closed;

        private Connection(NamedPipeServerStream pipe, Process host)
        {
            this.pipe = pipe;
            this.host = host;
            reader = new StreamReader(pipe, leaveOpen: true);
            writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        }

        public static async Task<Connection> OpenAsync(CancellationToken token)
        {
            string session = Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(Prefix + session, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Process? host = null;
            try
            {
                string appHost = Path.Combine(AppContext.BaseDirectory, "D2XPad.SeriesProbe.exe");
                host = Process.Start(new ProcessStartInfo(appHost)
                {
                    Verb = "runas", UseShellExecute = true, Arguments = "--driver-host " + session,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory
                }) ?? throw new IOException("Could not start the HIDMaestro driver host.");
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(connectTimeout.Token);
                var result = new Connection(pipe, host);
                await result.ExpectAsync("READY", TimeSpan.FromSeconds(100), token);
                return result;
            }
            catch { pipe.Dispose(); host?.Dispose(); throw; }
        }

        public async Task SendAsync(ProbeCase frame, CancellationToken token)
        {
            await writer.WriteLineAsync(frame.Id);
            await ExpectAsync("OK", TimeSpan.FromSeconds(10), token);
        }

        private async Task ExpectAsync(string expected, TimeSpan timeout, CancellationToken token)
        {
            var line = await reader.ReadLineAsync(token).AsTask().WaitAsync(timeout, token);
            if (line != expected)
                throw new IOException(line ?? "The HIDMaestro driver host exited without replying.");
        }

        public async ValueTask DisposeAsync()
        {
            if (closed) return;
            closed = true;
            try
            {
                if (pipe.IsConnected)
                {
                    await writer.WriteLineAsync("STOP");
                    await ExpectAsync("CLOSED", TimeSpan.FromSeconds(30), CancellationToken.None);
                }
            }
            finally
            {
                pipe.Dispose();
                reader.Dispose();
                host.Dispose();
            }
        }
    }
}
