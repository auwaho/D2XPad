using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;
using D2XPad.Output;
using HIDMaestro;

namespace D2XPad.Live;

internal sealed class LiveOutput : IAsyncDisposable
{
    private const string Prefix = "D2XPad.Live.";
    private const byte Frame = 1, Stop = 2, Ping = 3;
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader replies;
    private readonly Process host;
    private readonly record struct Packet(LiveState State, ulong Enqueued, TaskCompletionSource? PowerAck = null);
    private readonly Channel<Packet> pending = Channel.CreateBounded<Packet>(64);
    private readonly object gate = new();
    private LiveState? last;
    private volatile bool stopping;
    private string? rumbleFeedbackProblem;

    internal LiveOutput(NamedPipeServerStream pipe, StreamReader replies, Process host, NamedPipeServerStream? feedback = null,
        string? feedbackProblem = null)
    {
        this.pipe = pipe;
        this.replies = replies;
        this.host = host;
        rumbleFeedbackProblem = feedbackProblem;
        Completion = RunAsync(feedback);
    }

    public Task Completion { get; }
    public event Action<byte, byte, long>? RumbleChanged;
    public string? RumbleFeedbackProblem => Volatile.Read(ref rumbleFeedbackProblem);

    private async Task RunAsync(NamedPipeServerStream? feedback)
    {
        using var stopFeedback = new CancellationTokenSource();
        var receive = feedback == null ? Task.CompletedTask :
            RumbleFeedback.ReceiveAsync(feedback, NotifyRumble, ReportFeedbackFailure, stopFeedback.Token);
        try { await PumpAsync(); }
        finally
        {
            await stopFeedback.CancelAsync();
            try { await receive; }
            finally
            {
                feedback?.Dispose();
                NotifyRumble(0, 0, Stopwatch.GetTimestamp());
            }
        }
    }

    private void ReportFeedbackFailure(string message)
    {
        if (stopping) return;
        Volatile.Write(ref rumbleFeedbackProblem, message);
        Console.Error.WriteLine(message);
    }

    private void NotifyRumble(byte left, byte right, long observed)
    {
        if (stopping && (left != 0 || right != 0)) return;
        try { RumbleChanged?.Invoke(left, right, observed); }
        catch (Exception ex) { Console.Error.WriteLine($"Could not apply rumble feedback: {ex.Message}"); }
    }

    public static async Task<LiveOutput> ConnectAsync(CancellationToken token, bool enableRumble = false)
    {
        token.ThrowIfCancellationRequested();
        string session = Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(Prefix + session, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        StreamReader? replies = null;
        Process? host = null;
        string? feedbackProblem = null;
        var feedback = enableRumble ? RumbleFeedback.OpenListener(session, out feedbackProblem) : null;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "D2XPad.Live.exe"))
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory, Arguments = "--driver-host " + session + (feedback != null ? " --rumble" : "")
            };
            try { host = Process.Start(start) ?? throw new IOException("Could not start the HIDMaestro driver host."); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("The Windows permission request was cancelled. Start the converter again to retry.", ex);
            }
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(connect.Token);
            replies = new StreamReader(pipe, leaveOpen: true);
            await ExpectAsync(replies, "READY", TimeSpan.FromSeconds(100), token);
            return new LiveOutput(pipe, replies, host, feedback, feedbackProblem);
        }
        catch
        {
            replies?.Dispose();
            pipe.Dispose();
            feedback?.Dispose();
            host?.Dispose();
            throw;
        }
    }

    // Called from the Raw Input message loop. Never block it on the driver or discard a button edge.
    public void Submit(LiveState state)
    {
        lock (gate)
        {
            if (stopping) throw new IOException("The HIDMaestro output session has closed.");
            if (state == last) return;
            if (!pending.Writer.TryWrite(new(state, AwakeTicks())))
            {
                var error = new IOException("HIDMaestro output fell behind. The virtual controller is being released.");
                stopping = true;
                pending.Writer.TryComplete(error);
                throw error;
            }
            last = state;
        }
    }

    // Serialize this boundary with physical report handling. Only one submitted packet can
    // already be in flight. Its ACK precedes the forced neutral packet on the same pipe.
    public void ResetForPowerTransition()
    {
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (stopping) throw new IOException("The HIDMaestro output session has closed.");
            while (pending.Reader.TryRead(out _)) { }
            if (!pending.Writer.TryWrite(new(LiveState.Neutral, AwakeTicks(), ack)))
                throw new IOException("Could not queue the power-transition neutral state.");
            last = LiveState.Neutral;
        }
        // Windows gives apps a limited time for suspend handling. A neutral that cannot
        // be acknowledged before sleep stays queued and resume also submits a fresh one.
        if (!ack.Task.Wait(TimeSpan.FromMilliseconds(500)))
            Console.WriteLine("HIDMaestro power-transition neutral is awaiting acknowledgement.");
    }

    // Use the same awake-time clock used by .NET's Windows timers for IPC watchdogs.
    // QueryUnbiasedInterruptTime returns units of 100 ns and excludes sleep and hibernation.
    private static ulong AwakeTicks()
    {
        QueryUnbiasedInterruptTime(out ulong ticks);
        return ticks;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong ticks);

    private async Task PumpAsync()
    {
        var frame = new byte[LiveState.WireSize + 1];
        frame[0] = Frame;
        byte[] ping = [Ping];
        try
        {
            while (!stopping)
            {
                if (pending.Reader.TryRead(out var item))
                {
                    if (item.PowerAck == null && AwakeTicks() - item.Enqueued > 500UL * TimeSpan.TicksPerMillisecond)
                        throw new IOException("HIDMaestro output is stale. Releasing the virtual controller.");
                    item.State.Write(frame.AsSpan(1));
                    await pipe.WriteAsync(frame);
                }
                else
                {
                    using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        if (!await pending.Reader.WaitToReadAsync(idle.Token)) break;
                        continue;
                    }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested) { }
                    await pipe.WriteAsync(ping);
                }
                await ExpectAsync(replies, "OK", TimeSpan.FromSeconds(3), CancellationToken.None);
                item.PowerAck?.TrySetResult();
            }
            await pipe.WriteAsync(new byte[] { Stop });
            await ExpectAsync(replies, "CLOSED", TimeSpan.FromSeconds(30), CancellationToken.None);
            while (pending.Reader.TryRead(out _)) { }
            await pending.Reader.Completion;
        }
        finally
        {
            lock (gate)
            {
                stopping = true;
                pending.Writer.TryComplete();
            }
            pipe.Dispose();
            replies.Dispose();
            host.Dispose();
        }
    }

    private static async Task ExpectAsync(StreamReader reader, string expected, TimeSpan timeout,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        string? actual;
        try { actual = await reader.ReadLineAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for the HIDMaestro host to reply {expected}.");
        }
        if (actual != expected)
            throw new IOException(actual ?? "The HIDMaestro driver host disconnected.");
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            stopping = true;
            pending.Writer.TryComplete();
        }
        return new ValueTask(Completion);
    }

    public static async Task<int> RunHostAsync(string session, bool enableRumble = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!Guid.TryParseExact(session, "N", out _) ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        using var pipe = new NamedPipeClientStream(".", Prefix + session, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(15000);
        using var replies = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        try
        {
            using (var controller = new SeriesController())
            {
                bool stopped = enableRumble ? await ServeWithFeedbackAsync(session, controller, pipe, replies)
                    : await ServeControllerAsync(controller, pipe, replies);
                if (!stopped) return 0;
            }
            await replies.WriteLineAsync("CLOSED");
            return 0;
        }
        catch (Exception ex)
        {
            try { await replies.WriteLineAsync("ERROR " + ex.GetBaseException().Message.ReplaceLineEndings(" ")); }
            catch (IOException) { }
            return 1;
        }
    }

    private static async Task<bool> ServeWithFeedbackAsync(string session, SeriesController controller, NamedPipeClientStream pipe, StreamWriter replies)
    {
        using var stop = new CancellationTokenSource();
        var queue = RumbleFeedback.Latest();
        void OnOutput(HMController _, HMOutputPacket packet)
        {
            if (RumbleFeedback.TryDecode(packet.Source, packet.ReportId, packet.Data.Span, out var levels))
                queue.Writer.TryWrite(levels);
        }
        controller.OutputReceived += OnOutput;
        var send = RumbleFeedback.SendAsync(session, queue.Reader, stop.Token);
        try { return await ServeControllerAsync(controller, pipe, replies); }
        finally
        {
            controller.OutputReceived -= OnOutput;
            queue.Writer.TryComplete();
            await stop.CancelAsync();
            await send;
        }
    }

    private static async Task<bool> ServeControllerAsync(SeriesController controller, NamedPipeClientStream pipe, StreamWriter replies)
    {
        await replies.WriteLineAsync("READY");
        var opcode = new byte[1];
        var payload = new byte[LiveState.WireSize];
        while (true)
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (await pipe.ReadAsync(opcode, watchdog.Token) == 0) return false;
            if (opcode[0] == Stop) return true;
            if (opcode[0] == Frame)
            {
                await pipe.ReadExactlyAsync(payload, watchdog.Token);
                var state = LiveState.Read(payload);
                controller.Submit(state.Lx, state.Ly, state.Rx, state.Ry, state.Lt, state.Rt,
                    ToButtons(state.Buttons), ToHat(state.Hat));
            }
            else if (opcode[0] != Ping)
                throw new InvalidDataException("Unknown driver-host command.");
            await replies.WriteLineAsync("OK");
        }
    }

    // HMButton has a different bit assignment from XINPUT_GAMEPAD.
    private static readonly (ushort Mask, HMButton Button)[] ButtonMap =
    [
        (0x0010, HMButton.Start), (0x0020, HMButton.Back), (0x0040, HMButton.LeftStick),
        (0x0080, HMButton.RightStick), (0x0100, HMButton.LeftBumper), (0x0200, HMButton.RightBumper),
        (0x0400, HMButton.Guide), (0x1000, HMButton.A), (0x2000, HMButton.B), (0x4000, HMButton.X),
        (0x8000, HMButton.Y)
    ];

    internal static HMButton ToButtons(ushort bits)
    {
        HMButton buttons = HMButton.None;
        foreach (var (mask, button) in ButtonMap)
            if ((bits & mask) != 0) buttons |= button;
        return buttons;
    }

    internal static HMHat ToHat(byte hat) => hat == 8 ? HMHat.None : (HMHat)(hat + 1);
}
