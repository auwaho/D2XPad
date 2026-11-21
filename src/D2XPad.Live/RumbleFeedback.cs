using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using HIDMaestro;

namespace D2XPad.Live;

// A separate pipe keeps game feedback off the input frame/acknowledgement path.
// Each message carries normalized grip magnitudes and the host observation time on Windows' shared QPC clock.
internal static class RumbleFeedback
{
    internal static string PipeName(string session) => "D2XPad.Live.Rumble." + session;

    internal static NamedPipeServerStream? OpenListener(string session, out string? error, SecurityIdentifier? owner = null)
    {
        error = null;
        try
        {
            return owner == null
                ? new(PipeName(session), PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous)
                : ConversionServicePipes.OpenListener(PipeName(session), owner, PipeDirection.In);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            error = $"Could not open the vibration channel. Restart the converter to retry. {ex.Message}";
            Console.Error.WriteLine(error);
            return null;
        }
    }

    internal static async Task ReceiveAsync(NamedPipeServerStream listener, Action<byte, byte, long> deliver,
        Action<string> reportFailure, CancellationToken token, uint? host = null)
    {
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await listener.WaitForConnectionAsync(connect.Token);
            if (host is uint process) ConversionServicePipes.VerifyClient(listener, process);
            byte[] packet = new byte[10];
            while (true)
            {
                await listener.ReadExactlyAsync(packet, token);
                deliver(packet[0], packet[1], BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(2)));
            }
        }
        catch (EndOfStreamException)
        {
            if (!token.IsCancellationRequested)
                reportFailure("The driver host closed the vibration channel. Restart the converter to reconnect.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            reportFailure("The driver host did not connect to the vibration channel within 15 seconds. Restart the converter to retry.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) reportFailure($"Lost the vibration channel. Restart the converter to reconnect. {ex.Message}");
        }
        finally { deliver(0, 0, Stopwatch.GetTimestamp()); }
    }

    internal readonly record struct Levels(byte Left, byte Right, long Observed);

    internal static bool TryDecode(HMOutputSource source, byte id, ReadOnlySpan<byte> data, out Levels levels)
    {
        levels = default;
        if (source != HMOutputSource.HidOutput || id != 0x0f || data.Length != 7 || data[2] > 100 || data[3] > 100)
            return false;
        // XInputSetState's observed duration/delay/loop tuple uses explicit stop commands.
        // A different tuple may request a timed effect, which the physical writer cannot schedule.
        if ((data[2] != 0 || data[3] != 0) && data[4..] is not [0xff, 0, 0xeb]) return false;
        levels = new((byte)((data[2] * 255 + 50) / 100), (byte)((data[3] * 255 + 50) / 100), Stopwatch.GetTimestamp());
        return true;
    }

    internal static Channel<Levels> Latest() => Channel.CreateBounded<Levels>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    internal static async Task SendAsync(string session, ChannelReader<Levels> queue, CancellationToken token,
        uint? expectedListener = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(session), PipeDirection.Out,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(15000, token);
            if (expectedListener is uint process) ConversionServicePipes.VerifyServer(client, process);
            byte[] frame = new byte[10];
            await foreach (var value in queue.ReadAllAsync(token))
            {
                frame[0] = value.Left;
                frame[1] = value.Right;
                BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(2), value.Observed);
                await client.WriteAsync(frame, token);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) Console.Error.WriteLine($"Rumble feedback is unavailable: {ex.Message}");
        }
    }
}
