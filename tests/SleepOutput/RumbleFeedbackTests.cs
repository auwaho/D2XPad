using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using D2XPad.Live;
using HIDMaestro;

internal static class RumbleFeedbackTests
{
    internal static async Task RunAsync()
    {
        // Raw OutputReceived packets recorded using XInputSetState on the shipped Series X|S profile.
        byte[] left = Convert.FromHexString("00001A00FF00EB");
        byte[] right = Convert.FromHexString("00000048FF00EB");
        byte[] stop = Convert.FromHexString("00000000FF00EB");
        static RumbleFeedback.Levels Decode(byte[] packet, int l, int r)
        {
            long before = Stopwatch.GetTimestamp();
            if (!RumbleFeedback.TryDecode(HMOutputSource.HidOutput, 15, packet, out var levels) ||
                levels.Left != l || levels.Right != r || levels.Observed < before || levels.Observed > Stopwatch.GetTimestamp())
                throw new InvalidDataException("Captured motor levels or observation time were not preserved.");
            return levels;
        }
        var a = Decode(left, 66, 0);
        var b = Decode(right, 0, 184);
        Decode(stop, 0, 0);
        Decode([0, 0, 0, 0, 1, 7, 0], 0, 0);
        Decode([0, 0, 100, 100, 0xff, 0, 0xeb], 255, 255);
        if (RumbleFeedback.TryDecode(HMOutputSource.HidFeature, 15, left, out _) ||
            RumbleFeedback.TryDecode(HMOutputSource.HidOutput, 0, left, out _) ||
            RumbleFeedback.TryDecode(HMOutputSource.HidOutput, 15, left[..6], out _) ||
            RumbleFeedback.TryDecode(HMOutputSource.HidOutput, 15, [0, 0, 32, 0, 10, 0, 0], out _) ||
            RumbleFeedback.TryDecode(HMOutputSource.HidOutput, 15, [0, 0, 101, 2, 0, 0, 0], out _))
            throw new InvalidDataException("Accepted an unsupported feedback packet.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        string session = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(session, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var peer = new NamedPipeClientStream(".", session, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
        using var reply = new StreamWriter(peer, leaveOpen: true) { AutoFlush = true };
        using var listener = new NamedPipeServerStream(RumbleFeedback.PipeName(session), PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var live = new LiveOutput(server, new StreamReader(server, leaveOpen: true), Process.GetCurrentProcess(), listener);
        var observed = Channel.CreateUnbounded<RumbleFeedback.Levels>();
        live.RumbleChanged += (l, r, clock) => observed.Writer.TryWrite(new(l, r, clock));
        var queue = RumbleFeedback.Latest();
        queue.Writer.TryWrite(a);
        queue.Writer.TryWrite(b);
        var send = RumbleFeedback.SendAsync(session, queue.Reader, token);
        live.Submit(LiveState.Neutral);
        var frame = new byte[1 + LiveState.WireSize];
        await peer.ReadExactlyAsync(frame, token);
        if (frame[0] != 1 || await observed.Reader.ReadAsync(token) != b)
            throw new IOException("Feedback did not coalesce or was blocked by an outstanding input acknowledgement.");
        queue.Writer.TryComplete();
        await send;
        var off = await observed.Reader.ReadAsync(token);
        if (off.Left != 0 || off.Right != 0) throw new IOException("Lost feedback connection did not release the motors.");
        if (live.RumbleFeedbackProblem?.Contains("Restart", StringComparison.Ordinal) != true)
            throw new IOException("Lost vibration channel did not surface an actionable diagnosis.");
        await reply.WriteLineAsync("OK");
        live.Submit(LiveState.Neutral with { Lt = 87 });
        await peer.ReadExactlyAsync(frame, token);
        if (frame[0] != 1 || LiveState.Read(frame.AsSpan(1)).Lt != 87)
            throw new IOException("Feedback loss interrupted input conversion.");
        await reply.WriteLineAsync("OK");
        var dispose = live.DisposeAsync().AsTask();
        byte[] opcode = new byte[1];
        await peer.ReadExactlyAsync(opcode, token);
        await reply.WriteLineAsync("CLOSED");
        await dispose.WaitAsync(token);
        var final = await observed.Reader.ReadAsync(token);
        if (final.Left != 0 || final.Right != 0) throw new IOException("Session shutdown did not release the motors.");
        Console.WriteLine("Passed recorded rumble decoding, bounded latest-value delivery, timestamp preservation, independent input and zero on feedback loss.");
    }
}
