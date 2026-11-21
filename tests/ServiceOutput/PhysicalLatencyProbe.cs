using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using D2XPad.SeriesProbe;
using Microsoft.Win32.SafeHandles;

namespace D2XPad.Live;

// Compiled only into the diagnostic build. Start at the converter's own Raw Input
// callback, then observe XInput independently of the converter and driver-host ACKs.
internal sealed class PhysicalLatencyProbe : IDisposable
{
    private readonly record struct Position(byte X, byte Y)
    {
        public static Position From(XInputReading reading) => new(ToByte(reading.Lx), ToByte(-reading.Ly));

        private static byte ToByte(int axis)
        {
            double offset = axis < 0 ? axis / 256d : axis * (127d / short.MaxValue);
            return (byte)Math.Clamp(128 + (int)Math.Round(offset), byte.MinValue, byte.MaxValue);
        }
    }

    private readonly record struct Edge(long Tick, Position Position);
    private sealed record MatchResult(double[] Milliseconds, int Unobserved, int AmbiguousReadings, int UnexpectedReadings);

    [ThreadStatic]
    private static long rawReportTimestamp;

    private static readonly long InitialWait = Stopwatch.Frequency * 120;
    private static readonly long MatchGrace = Stopwatch.Frequency;
    private const int PollMicroseconds = 500;
    private const int WarmupSeconds = 2;
    private readonly string report;
    private readonly string label;
    private readonly int slot;
    private readonly int seconds;
    private readonly long captureTicks;
    private readonly object gate = new();
    private readonly List<Edge> submitted = new(4096);
    private readonly Thread observer;
    private long measuringAt;
    private Position lastPosition = new(128, 128);
    private volatile bool stopping;

    public static long RawReportTimestamp
    {
        get => rawReportTimestamp;
        set => rawReportTimestamp = value;
    }

    public static PhysicalLatencyProbe? Start(string layoutId)
    {
        string? path = Environment.GetEnvironmentVariable("D2XPAD_LATENCY_REPORT");
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (layoutId != "85hf-bluetooth")
            throw new ArgumentException("The latency comparison requires the Bluetooth 85HF.");
        if (!Path.IsPathFullyQualified(path) || File.Exists(path))
            throw new ArgumentException("Set D2XPAD_LATENCY_REPORT to an absolute, unused report path.");

        string? specifiedSeconds = Environment.GetEnvironmentVariable("D2XPAD_LATENCY_SECONDS");
        int seconds = 25;
        if (specifiedSeconds is not null &&
            (!int.TryParse(specifiedSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 5 or > 90))
            throw new ArgumentException("Set D2XPAD_LATENCY_SECONDS to a duration from 5 to 90 seconds.");

        var slots = XInputReader.ReadAll().Keys;
        // The virtual slot was identified immediately before constructing PhysicalFeed.
        // Require a single slot so this independent observer cannot measure the wrong pad.
        if (slots.Count() != 1)
            throw new IOException("Disconnect other XInput controllers before the latency comparison.");
        string label = Environment.GetEnvironmentVariable("D2XPAD_LATENCY_LABEL") ?? "unlabeled";
        return new(path, label, slots.Single(), seconds);
    }

    private PhysicalLatencyProbe(string path, string label, int slot, int seconds)
    {
        report = path;
        this.label = label;
        this.slot = slot;
        this.seconds = seconds;
        captureTicks = Stopwatch.Frequency * seconds;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        observer = new Thread(Observe) { IsBackground = true, Name = "D2XPad XInput latency observer" };
        observer.Start();
    }

    public void Submitted(LiveState state)
    {
        var position = new Position(state.Lx, state.Ly);
        if (position == lastPosition) return;
        lastPosition = position; // PhysicalSession serializes submissions.
        long received = RawReportTimestamp;
        if (received == 0 || stopping) return;
        lock (gate)
        {
            if (measuringAt == 0)
            {
                if (Math.Abs(position.X - 128) < 16 && Math.Abs(position.Y - 128) < 16) return;
                Interlocked.Exchange(ref measuringAt, received + WarmupSeconds * Stopwatch.Frequency);
            }
            if (received >= measuringAt && received - measuringAt < captureTicks)
                submitted.Add(new(received, position));
        }
    }

    private void Observe()
    {
        var measurements = new List<Edge>(4096);
        var intervals = new List<long>(seconds * 1000);
        string? problem = null;
        try
        {
            using var timer = CreateWaitableTimerExW(0, null, 2, 0x00100002);
            if (timer.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a high-resolution XInput polling timer.");
            Position previous = ReadPosition();
            long waitingSince = Stopwatch.GetTimestamp();
            long previousPoll = 0;
            bool measuring = false;
            Console.WriteLine("Latency probe ready. After releasing the controls, steadily circle the left stick.");
            while (!stopping)
            {
                Delay(timer);
                // Mark the upper bound after XInputGetState returns so slow calls are
                // reflected honestly alongside scheduling gaps in the poll statistics.
                Position actual = ReadPosition();
                long tick = Stopwatch.GetTimestamp();
                long first = Interlocked.Read(ref measuringAt);
                if (first != 0)
                {
                    if (!measuring)
                    {
                        measuring = true;
                        Console.WriteLine($"Started a {WarmupSeconds}-second warmup, then {seconds} seconds of measurement. Keep circling the left stick.");
                    }
                    if (previousPoll >= first && tick - first <= captureTicks)
                        intervals.Add(tick - previousPoll);
                    if (tick >= first && actual != previous) measurements.Add(new(tick, actual));
                    if (tick - first >= captureTicks + MatchGrace) break;
                }
                else if (tick - waitingSince >= InitialWait)
                {
                    problem = "Timed out waiting for left-stick movement from the Bluetooth 85HF.";
                    break;
                }
                previous = actual;
                previousPoll = tick;
            }
            if (stopping) problem = "Conversion stopped before the measurement window ended.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            problem = ex.Message;
        }

        Edge[] inputEdges;
        lock (gate) inputEdges = submitted.ToArray();
        var result = Match(inputEdges, measurements);
        double[] pollTimes = intervals.Select(Milliseconds).ToArray();
        double coverage = inputEdges.Length == 0 ? 0 : (double)result.Milliseconds.Length / inputEdges.Length;
        try
        {
            var payload = new
            {
                Label = label, XInputSlot = slot, ConverterProcessId = Environment.ProcessId, CaptureSeconds = seconds, WarmupSeconds,
                Start = "D2XPad Raw Input callback", End = "First matching XInputGetState return",
                StopwatchFrequency = Stopwatch.Frequency, PollTargetMicroseconds = PollMicroseconds,
                SubmittedChanges = inputEdges.Length, MatchedChanges = result.Milliseconds.Length,
                result.Unobserved, result.AmbiguousReadings, result.UnexpectedReadings, Coverage = coverage,
                MedianMilliseconds = Percentile(result.Milliseconds, .50),
                P99Milliseconds = Percentile(result.Milliseconds, .99),
                PollMedianMilliseconds = Percentile(pollTimes, .50), PollP99Milliseconds = Percentile(pollTimes, .99),
                SufficientSample = problem == null && result.Milliseconds.Length >= 200 && coverage >= .99,
                SamplesMilliseconds = result.Milliseconds, Problem = problem
            };
            string pending = report + ".tmp";
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.Move(pending, report);
            Console.WriteLine($"Latency report saved to {report}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Couldn't save the latency report: {ex.Message}");
        }
    }

    private Position ReadPosition() => XInputReader.Read(slot) is { } state
        ? Position.From(state) : throw new IOException($"XInput slot {slot} disconnected during the measurement.");

    private static MatchResult Match(Edge[] expected, List<Edge> readings)
    {
        var matches = new List<double>(expected.Length);
        int index = 0, ambiguous = 0, unexpected = 0;
        foreach (var reading in readings)
        {
            while (index < expected.Length && reading.Tick - expected[index].Tick > MatchGrace) index++;
            if (index == expected.Length) break;
            int found = -1, candidateCount = 0;
            for (int next = index; next < expected.Length && expected[next].Tick <= reading.Tick; next++)
            {
                if (expected[next].Position != reading.Position) continue;
                found = next;
                candidateCount++;
            }
            if (candidateCount == 0)
            {
                unexpected++;
                continue;
            }
            if (candidateCount == 1) matches.Add(Milliseconds(reading.Tick - expected[found].Tick));
            else ambiguous++;
            // XInput may coalesce intermediate states. Count skipped or ambiguous
            // states in coverage and exclude them from the latency distribution.
            index = found + 1;
        }
        return new(matches.ToArray(), expected.Length - matches.Count, ambiguous, unexpected);
    }

    private static double Milliseconds(long ticks) => 1_000d * ticks / Stopwatch.Frequency;

    private static double? Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return null;
        var sorted = values.Order().ToArray();
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)position;
        return sorted[lower] + (sorted[(int)Math.Ceiling(position)] - sorted[lower]) * (position - lower);
    }

    private static void Delay(SafeWaitHandle timer)
    {
        long due = -10 * PollMicroseconds; // Relative, in units of 100 nanoseconds.
        if (!SetWaitableTimer(timer, ref due, 0, 0, 0, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not arm the XInput polling timer.");
        if (WaitForSingleObject(timer, 2_000) != 0)
            throw new IOException("The XInput polling timer did not signal.");
    }

    public void Dispose()
    {
        stopping = true;
        observer.Join(TimeSpan.FromSeconds(3));
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint security, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
