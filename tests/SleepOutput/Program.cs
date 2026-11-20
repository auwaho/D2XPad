using System.Diagnostics;
using System.IO.Pipes;
using D2XPad.Live;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
var token = timeout.Token;
string name = $"D2XPad.Test.{Guid.NewGuid():N}";
using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
using var peer = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
using var replies = new StreamWriter(peer, leaveOpen: true) { AutoFlush = true };
var output = new LiveOutput(server, new StreamReader(server, leaveOpen: true), Process.GetCurrentProcess());

async Task ExpectFrame(LiveState expected)
{
    var frame = new byte[1 + LiveState.WireSize];
    await peer.ReadExactlyAsync(frame, token);
    if (frame[0] != 1 || LiveState.Read(frame.AsSpan(1)) != expected)
        throw new InvalidDataException($"Expected {expected} as the next virtual frame.");
}

var held = LiveState.Neutral with { Buttons = 0x1000, Rt = 255 };
output.Submit(held);
await ExpectFrame(held);
// Withhold the current packet's acknowledgement and fill the queue with stale gestures.
output.Submit(held with { Lx = 0 });
output.Submit(held with { Lx = 255 });
output.ResetForPowerTransition();
await Task.Delay(100, token);
await replies.WriteLineAsync("OK");
await ExpectFrame(LiveState.Neutral);
await replies.WriteLineAsync("OK");

// Resume must force another neutral even when the last output was already centered.
var resume = Task.Run(output.ResetForPowerTransition, token);
await ExpectFrame(LiveState.Neutral);
if (resume.IsCompleted) throw new InvalidDataException("Power reset returned before the virtual host acknowledged it.");
await replies.WriteLineAsync("OK");
await resume.WaitAsync(token);

var fresh = LiveState.Neutral with { Buttons = 0x2000, Lt = 255 };
output.Submit(fresh);
await ExpectFrame(fresh);
await replies.WriteLineAsync("OK");
var close = output.DisposeAsync().AsTask();
var stop = new byte[1];
await peer.ReadExactlyAsync(stop, token);
if (stop[0] != 2) throw new InvalidDataException("Missing virtual host shutdown command.");
await replies.WriteLineAsync("CLOSED");
await close.WaitAsync(token);
Console.WriteLine("Passed queued-state purge, delayed acknowledgement, forced wake neutral and fresh input on the original pipe.");
await RumbleFeedbackTests.RunAsync();
