using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using D2XPad.Output;
using D2XPad.SeriesProbe;
using HIDMaestro;

namespace D2XPad.Live;

internal static class RumbleProbe
{
    private const string Prefix = "D2XPad.RumbleProbe.";
    private sealed record Feedback(uint Sequence, string Source, byte ReportId, string Hex);
    private static readonly XInputExpectation Marker = new(11, 24, 0, 0, 0, 0);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["survey"]) return RumbleSurvey.Run();
        if (args is ["host", var key] && Guid.TryParseExact(key, "N", out _)) return await Host(key);
        if (args is not (["virtual"] or ["physical"])) throw new ArgumentException("Use rumble-probe survey, virtual or physical.");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try { return args[0] == "physical" ? await RumbleSurvey.PulseAsync(cancel.Token) : await Observe(cancel.Token); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static async Task<int> Observe(CancellationToken token)
    {
        int[] occupied = XInputReader.ReadAll().Keys.ToArray();
        if (occupied.Length == 4) throw new IOException("All four XInput slots are occupied. The virtual probe needs a free slot.");
        string session = Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(Prefix + session, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Console.WriteLine("Creating an isolated Xbox Series X|S. Approve the HIDMaestro host prompt if Windows requests it.");
        using var host = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "D2XPad.Live.exe"))
        {
            Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory, Arguments = "rumble-probe host " + session
        }) ?? throw new IOException("Could not start the virtual rumble host.");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(30));
        await pipe.WaitForConnectionAsync(connect.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        async Task<string> Reply(TimeSpan timeout)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            string? line = await reader.ReadLineAsync(deadline.Token);
            if (line == null || line.StartsWith("ERROR ", StringComparison.Ordinal))
                throw new IOException(line ?? "The rumble host disconnected.");
            return line;
        }
        if (await Reply(TimeSpan.FromSeconds(120)) != "READY") throw new IOException("The rumble host did not initialize.");
        int slot = await XInputSlotCheck.IdentifyAsync(s => writer.WriteLine($"POSE {s.Lt} {s.Rt}"),
            XInputReader.ReadAll, Random.Shared, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2), token);
        Console.WriteLine($"Verified probe identity by three trigger challenges on XInput slot {slot}.");
        await writer.WriteLineAsync($"POSE {Marker.Lt} {Marker.Rt}");
        if (!(await XInputReader.ObserveAsync(slot, Marker, TimeSpan.FromSeconds(2), token)).Passed)
            throw new IOException("Lost the verified probe slot.");

        bool IsOurSlot() => XInputReader.ReadAll().TryGetValue(slot, out var sample) && XInputReader.Compare(sample, Marker).Length == 0;
        bool changed = false;
        async Task<Feedback[]> Phase(string name, ushort left, ushort right)
        {
            await writer.WriteLineAsync("CLEAR");
            if (await Reply(TimeSpan.FromSeconds(3)) != "OK" || !IsOurSlot()) throw new IOException("Lost the verified probe slot.");
            uint result = XInputSetState(slot, new(left, right));
            Console.WriteLine($"{name}: XInputSetState({left}, {right}) returned {result}.");
            if (result != 0) throw new Win32Exception((int)result);
            changed = left != 0 || right != 0;
            await Task.Delay(450, token);
            await writer.WriteLineAsync("READ");
            var events = JsonSerializer.Deserialize<Feedback[]>(await Reply(TimeSpan.FromSeconds(3))) ??
                throw new IOException("Invalid reply from the feedback observer.");
            foreach (var packet in events)
                Console.WriteLine($"  #{packet.Sequence} {packet.Source} ID 0x{packet.ReportId:X2} data {packet.Hex}");
            if (events.Length == 0) Console.WriteLine("  No HIDMaestro output packet during this phase.");
            return events;
        }
        bool matched;
        try
        {
            var left = await Phase("Left only", 0x4400, 0);
            var right = await Phase("Right only", 0, 0xb900);
            var repeat = await Phase("Repeat left", 0x4400, 0);
            var stopped = await Phase("Stop", 0, 0);
            matched = Matches(left, right, repeat, stopped);
        }
        finally
        {
            if (changed && IsOurSlot()) XInputSetState(slot, new(0, 0));
        }
        await writer.WriteLineAsync("STOP");
        if (await Reply(TimeSpan.FromSeconds(30)) != "CLOSED") throw new IOException("The virtual probe host did not close cleanly.");
        var wait = Stopwatch.StartNew();
        while (XInputReader.ReadAll().ContainsKey(slot) && wait.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(100, token);
        var remaining = XInputReader.ReadAll().Keys.ToArray();
        bool released = !remaining.Contains(slot);
        bool othersPresent = occupied.Where(i => i != slot).All(remaining.Contains);
        Console.WriteLine($"Probe slot removed: {released}. Previously occupied other slots present: {othersPresent}.");
        Console.WriteLine(matched ? "Virtual feedback verified for left, right, repeat and stop." :
            "Virtual feedback was not verified. Inspect the captured packets for each phase.");
        return matched && released && othersPresent ? 0 : 2;
    }

    // Compare the raw feedback packets across single-motor commands, including a repeated
    // command and a final all-zero command.
    private static bool Matches(Feedback[] left, Feedback[] right, Feedback[] repeat, Feedback[] stopped)
    {
        foreach (var a in left.Where(p => p.Source is "HidOutput" or "XInput"))
        {
            if (!repeat.Any(p => p.Source == a.Source && p.ReportId == a.ReportId && p.Hex == a.Hex)) continue;
            var l = Convert.FromHexString(a.Hex);
            foreach (var b in right.Where(p => p.Source == a.Source && p.ReportId == a.ReportId && p.Hex.Length == a.Hex.Length))
            foreach (var z in stopped.Where(p => p.Source == a.Source && p.ReportId == a.ReportId && p.Hex.Length == a.Hex.Length))
            {
                var r = Convert.FromHexString(b.Hex);
                var zero = Convert.FromHexString(z.Hex);
                bool leftOnly = false, rightOnly = false;
                for (int i = 0; i < l.Length; i++)
                {
                    leftOnly |= l[i] > 0 && r[i] == 0 && zero[i] == 0;
                    rightOnly |= r[i] > 0 && l[i] == 0 && zero[i] == 0;
                }
                if (leftOnly && rightOnly) return true;
            }
        }
        return false;
    }

    private static async Task<int> Host(string session)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        using var pipe = new NamedPipeClientStream(".", Prefix + session, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(15000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        try
        {
            using (var pad = new SeriesController("D2XPad.rumble-probe"))
            {
                var events = new List<Feedback>();
                bool overflow = false;
                pad.OutputReceived += (_, p) =>
                {
                    lock (events)
                    {
                        if (events.Count == 128) overflow = true;
                        else events.Add(new(p.SeqNo, p.Source.ToString(), p.ReportId, Convert.ToHexString(p.Data.Span)));
                    }
                };
                await writer.WriteLineAsync("READY");
                while (true)
                {
                    string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    if (line is null or "STOP") break;
                    switch (line.Split(' '))
                    {
                        case ["POSE", var a, var b] when byte.TryParse(a, out byte lt) && byte.TryParse(b, out byte rt) && lt < 30 && rt < 30:
                            pad.Submit(128, 128, 128, 128, lt, rt, HMButton.None, HMHat.None);
                            break;
                        case ["CLEAR"]:
                            lock (events) { events.Clear(); overflow = false; }
                            await writer.WriteLineAsync("OK");
                            break;
                        case ["READ"]:
                            Feedback[] snapshot;
                            lock (events)
                            {
                                if (overflow) throw new IOException("Feedback capture overflow. Retry when other consumers are idle.");
                                snapshot = events.ToArray();
                            }
                            await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot));
                            break;
                        default: throw new InvalidDataException("Unknown rumble probe command.");
                    }
                }
            }
            await writer.WriteLineAsync("CLOSED");
            return 0;
        }
        catch (Exception ex)
        {
            try { await writer.WriteLineAsync("ERROR " + ex.GetBaseException().Message); } catch (IOException) { }
            return 2;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Motors(ushort left, ushort right)
    {
        public readonly ushort Left = left;
        public readonly ushort Right = right;
    }
    [DllImport("xinput1_4", ExactSpelling = true)]
    private static extern uint XInputSetState(int slot, in Motors motors);
}
