using System.ComponentModel;
using D2XPad.Capture;

namespace D2XPad.Live;

internal interface IPhysicalMotorWriter : IDisposable
{
    ValueTask WriteAsync(byte left, byte right, CancellationToken token);
}

// Only this worker opens/writes a physical HID handle. The Raw Input pump and virtual
// feedback reader only update the desired state, even if Windows stalls a Bluetooth write.
internal sealed class PhysicalRumbleSink : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly Func<HidCollection, IPhysicalMotorWriter> open;
    private readonly Action<string>? onError;
    private readonly Task worker;
    private HidCollection? target;
    private long generation;
    private long feedbackSequence;
    private long effectsWritten;
    private (byte Left, byte Right) desired;
    private CancellationTokenSource? writing;
    private Task cancelWrite = Task.CompletedTask;
    private bool disposed;
    private string? problem;

    public PhysicalRumbleSink(Action<string>? onError = null) : this(RumbleSurvey.OpenMotors, onError) { }

    internal PhysicalRumbleSink(Func<HidCollection, IPhysicalMotorWriter> open, Action<string>? onError = null)
    {
        this.open = open;
        this.onError = onError;
        worker = Task.Run(RunAsync);
    }

    public long Generation { get { lock (gate) return generation; } }
    public long EffectsWritten => Interlocked.Read(ref effectsWritten);
    public string? Problem { get { lock (gate) return problem; } }

    public void SetTarget(HidCollection? device, bool enabled)
    {
        var next = enabled && device != null && RumbleSurvey.Supports(device) ? device : null;
        lock (gate)
        {
            if (disposed || SameInstance(target, next)) return;
            ResetLocked(next);
        }
    }

    private static bool SameInstance(HidCollection? a, HidCollection? b) => ReferenceEquals(a, b) ||
        a != null && b != null && a.Handle == b.Handle &&
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ContainerId, b.ContainerId, StringComparison.OrdinalIgnoreCase);

    private void ResetLocked(HidCollection? next)
    {
        target = next;
        generation++;
        desired = default;
        problem = null;
        if (writing is { IsCancellationRequested: false }) cancelWrite = writing.CancelAsync();
        WakeLocked();
    }

    // A game can hold one XInput effect until it explicitly changes it. Preserve that
    // state between callbacks; every lifecycle boundary clears it before the next bind.
    public void Submit(long expectedGeneration, byte left, byte right)
    {
        lock (gate)
        {
            if (disposed || target == null || expectedGeneration != generation ||
                (desired == (left, right) && problem == null)) return;
            desired = (left, right);
            feedbackSequence++;
            WakeLocked();
        }
    }

    private void WakeLocked()
    {
        if (changed.CurrentCount == 0) changed.Release();
    }

    private async Task RunAsync()
    {
        long bound = -1;
        IPhysicalMotorWriter? writer = null;
        (byte Left, byte Right) sent = default;
        long failedAt = 0;
        bool canRetry = true;
        try
        {
            while (true)
            {
                await changed.WaitAsync().ConfigureAwait(false);
                long current, sequence;
                HidCollection? device;
                bool ending;
                lock (gate) { current = generation; device = target; ending = disposed; sequence = feedbackSequence; }
                bool replaced = current != bound;
                if (replaced)
                {
                    if (writer != null) await ReleaseAsync(writer, bound).ConfigureAwait(false);
                    writer = null;
                    bound = current;
                    sent = default;
                    canRetry = true;
                    if (ending) return;
                }
                try
                {
                    // Retry once per failure episode, on feedback received after the failure.
                    // Never restart the effect that failed just because a wake was already queued.
                    if (writer == null && device != null && (replaced || canRetry && sequence > failedAt))
                    {
                        if (!replaced) canRetry = false;
                        writer = open(device);
                        sent = default;
                        if (!await WriteCurrentAsync(writer, bound, 0, 0).ConfigureAwait(false)) continue;
                    }
                    if (writer == null) continue;
                    (byte Left, byte Right) next;
                    lock (gate) { if (bound != generation) continue; next = desired; }
                    if (next != sent)
                    {
                        if (!await WriteCurrentAsync(writer, bound, next.Left, next.Right).ConfigureAwait(false)) continue;
                        sent = next;
                        if (next != default) Interlocked.Increment(ref effectsWritten);
                    }
                    canRetry = true;
                    lock (gate) { if (bound == generation) problem = null; }
                }
                catch (Exception ex) when (OutputError(ex))
                {
                    failedAt = Error(bound, ex);
                    if (writer != null) await ReleaseAsync(writer, bound).ConfigureAwait(false);
                    writer = null;
                }
            }
        }
        finally { if (writer != null) await ReleaseAsync(writer, bound).ConfigureAwait(false); }
    }

    private async ValueTask<bool> WriteCurrentAsync(IPhysicalMotorWriter writer, long bound, byte left, byte right)
    {
        using var interrupt = new CancellationTokenSource();
        lock (gate)
        {
            if (bound != generation) return false;
            writing = interrupt;
            cancelWrite = Task.CompletedTask;
        }
        try
        {
            await writer.WriteAsync(left, right, interrupt.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested) { return false; }
        finally
        {
            Task cancellation;
            lock (gate) { writing = null; cancellation = cancelWrite; }
            await cancellation.ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync(IPhysicalMotorWriter writer, long bound)
    {
        try { await writer.WriteAsync(0, 0, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (OutputError(ex)) { Error(bound, ex); }
        finally
        {
            try { writer.Dispose(); }
            catch (Exception ex) when (OutputError(ex)) { Error(bound, ex); }
        }
    }

    private static bool OutputError(Exception ex) =>
        ex is IOException or Win32Exception or InvalidDataException or UnauthorizedAccessException or OperationCanceledException;
    private long Error(long bound, Exception ex)
    {
        string message = $"Physical vibration unavailable: {ex.Message} If it persists, turn vibration off and on.";
        long sequence;
        lock (gate)
        {
            if (bound == generation) problem = message;
            sequence = feedbackSequence;
        }
        onError?.Invoke(message);
        return sequence;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ResetLocked(null);
        }
        try { worker.GetAwaiter().GetResult(); }
        finally { changed.Dispose(); }
    }
}
