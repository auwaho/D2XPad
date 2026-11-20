using System.Threading.Channels;
using D2XPad.Capture;
using D2XPad.Live;

internal static class PhysicalRumbleTests
{
    private sealed class Writer : IPhysicalMotorWriter
    {
        private readonly Channel<(byte Left, byte Right)> packets = Channel.CreateUnbounded<(byte, byte)>();
        public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? BlockNext;
        public bool FailNext;

        public async ValueTask WriteAsync(byte left, byte right, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var block = Interlocked.Exchange(ref BlockNext, null);
            bool fail = FailNext;
            FailNext = false;
            packets.Writer.TryWrite((left, right));
            if (block != null) await block.Task.WaitAsync(token);
            if (fail) throw new IOException("Simulated Bluetooth failure.");
        }

        public async Task Expect(byte left, byte right)
        {
            var packet = await packets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(packet == (left, right), $"Expected physical motors {left}/{right}, received {packet.Left}/{packet.Right}.");
        }

        public void Dispose() => Closed.SetResult();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    public static async Task Run()
    {
        CheckExclusiveReservation();
        var a = new HidCollection(1, "unit-a", 0x2dc8, 0x2067, 1, 5, "Bluetooth", "a", 32, [1]);
        var b = a with { Handle = 2, Path = "unit-b", ContainerId = "b" };
        var first = new Writer();
        var second = new Writer();
        var recovered = new Writer();
        var rebound = new Writer();
        var errors = Channel.CreateUnbounded<string>();
        int opened = 0;
        // Only fake handles reach this test. Neither enabling the sink nor reporting a pad opens Windows HID.
        using var sink = new PhysicalRumbleSink(device =>
        {
            int n = Interlocked.Increment(ref opened);
            return (device.Path, n) switch
            {
                ("unit-a", 1) => first, ("unit-b", 2) => second, ("unit-b", 3) => recovered,
                ("unit-b", 4) => throw new IOException("Simulated reopen failure."),
                ("unit-b", 5) => rebound,
                _ => throw new InvalidDataException("An unexpected device or binding was opened.")
            };
        }, message => errors.Writer.TryWrite(message));
        Check(RumbleSurvey.Magnitudes(0, 250).SequenceEqual(new byte[] { 0, 250, 0, 0 }) &&
            RumbleSurvey.Magnitudes(44, 0).SequenceEqual(new byte[] { 44, 0, 0, 0 }), "Separate handle motor fields");
        sink.SetTarget(a, false);
        sink.SetTarget(a with { Transport = "USB" }, true);
        sink.SetTarget(a with { ProductId = 0x0b13 }, true);
        sink.SetTarget(a with { InputReportLength = 5 }, true);
        Check(sink.Generation == 0 && opened == 0, "Unsupported or disabled device opened.");
        sink.SetTarget(a, true);
        await first.Expect(0, 0);
        long epochA = sink.Generation;
        var blocked = first.BlockNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Submit(epochA, 25, 0);
        await first.Expect(25, 0);
        sink.Submit(epochA, 100, 0);
        sink.Submit(epochA, 7, 36);
        sink.Submit(epochA, 0, 184);
        blocked.SetResult();
        await first.Expect(0, 184);
        sink.Submit(epochA, 0, 184); // Repeated state is already on the device.
        first.BlockNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Submit(epochA, 90, 92);
        await first.Expect(90, 92);
        sink.Submit(epochA, 255, 255); // This queued command belongs only to the old binding.
        first.FailNext = true; // The old pad vanishes before accepting its final zero.
        sink.SetTarget(null, false);
        sink.SetTarget(b, true);
        sink.Submit(epochA, 123, 210);
        sink.Submit(sink.Generation, 16, 27);
        await first.Expect(0, 0); // In-flight write was cancelled and the writer got a final stop.
        await first.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await second.Expect(0, 0);
        await second.Expect(16, 27);
        Check(sink.Problem == null, "A late stop error from the previous device marked the new one unavailable.");
        await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        second.FailNext = true;
        sink.Submit(sink.Generation, 2, 3);
        await second.Expect(2, 3);
        await second.Expect(0, 0);
        await second.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check((await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Contains("Simulated") &&
            sink.Problem?.Contains("Simulated") == true,
            "Failed write did not report its cause.");
        Check(opened == 2 && sink.EffectsWritten == 3, "A failed or canceled effect was counted or retried without fresh feedback.");
        sink.Submit(sink.Generation, 2, 3); // A fresh command can renew an identical failed effect.
        await recovered.Expect(0, 0);
        await recovered.Expect(2, 3);
        recovered.FailNext = true;
        sink.Submit(sink.Generation, 50, 50);
        await recovered.Expect(50, 50);
        await recovered.Expect(0, 0);
        await recovered.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(opened == 3 && sink.EffectsWritten == 4, "Recovery did not count the successful effect exactly once.");
        sink.Submit(sink.Generation, 60, 60);
        Check((await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Contains("Simulated reopen failure") &&
            sink.Problem?.Contains("turn vibration off and on") == true,
            "An unsuccessful retry did not report how to recover.");
        sink.Submit(sink.Generation, 64, 64);
        await Task.Delay(100);
        Check(opened == 4 && sink.EffectsWritten == 4, "An exhausted binding continued to reopen or report writes.");
        sink.SetTarget(null, false);
        sink.SetTarget(b, true);
        await rebound.Expect(0, 0);
        sink.Submit(sink.Generation, 70, 90);
        await rebound.Expect(70, 90);
        sink.Dispose();
        await rebound.Expect(0, 0);
        Check(rebound.Closed.Task.IsCompleted && opened == 5 && sink.EffectsWritten == 5, "Stopping the sink did not close its final writer.");
        Console.WriteLine("Passed exclusive vibration probe reservation, physical rumble isolation, neutralization, bounded recovery and motor write accounting.");
    }

    private static void CheckExclusiveReservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = $@"Local\D2XPad.RumbleReservationTest.{Guid.NewGuid():N}";
        const string busy = "Reserved for a simulated conversion.";
        using (ConverterReservation.ReserveStopped(busy, name))
        {
            try
            {
                using var unexpected = ConverterReservation.ReserveStopped(busy, name);
                throw new InvalidDataException("A second caller reserved the same converter.");
            }
            catch (InvalidOperationException ex) when (ex.Message == busy) { }
        }
        using var released = ConverterReservation.ReserveStopped(busy, name);
    }
}
