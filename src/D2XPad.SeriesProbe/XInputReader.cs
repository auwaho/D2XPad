using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace D2XPad.SeriesProbe;

internal sealed record XInputExpectation(byte Lt, byte Rt, short Lx, short Ly, short Rx, short Ry,
    ushort Buttons = 0);

internal sealed record XInputReading(uint Packet, byte Lt, byte Rt, short Lx, short Ly, short Rx, short Ry,
    ushort Buttons);

internal sealed record XInputObservation(bool Passed, XInputReading? Actual, string Detail);

internal static class XInputReader
{
    private const int MaxUsers = 4;
    private const uint DeviceNotConnected = 1167;
    private const int PollMilliseconds = 10;

    internal static IReadOnlyDictionary<int, XInputReading> ReadAll()
    {
        var result = new Dictionary<int, XInputReading>();
        for (int slot = 0; slot < MaxUsers; slot++)
            if (Read(slot) is { } value)
                result.Add(slot, value);
        return result;
    }

    internal static async Task<int> WaitForNewSlotAsync(IReadOnlyCollection<int> initialSlots,
        TimeSpan timeout, CancellationToken token)
    {
        if (initialSlots.Count == MaxUsers)
            throw new InvalidOperationException("All four XInput slots are already in use.");

        var timer = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            int[] added = ReadAll().Keys.Except(initialSlots).ToArray();
            if (added.Length == 1)
                return added[0];
            if (added.Length > 1)
                throw new InvalidOperationException("Multiple new XInput slots appeared. Could not identify the D2XPad controller.");
            await Task.Delay(PollMilliseconds, token);
        } while (timer.Elapsed < timeout);
        throw new TimeoutException("Timed out waiting for the virtual Xbox Series X|S controller to appear in a new XInput slot.");
    }

    internal static async Task<XInputObservation> ObserveAsync(int slot, XInputExpectation expected,
        TimeSpan timeout, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        XInputReading? actual;
        string difference;
        int matches = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            actual = Read(slot);
            if (actual is null)
                return new(false, null, $"The controller in XInput slot {slot} disconnected.");
            difference = Compare(actual, expected);
            matches = difference.Length == 0 ? matches + 1 : 0;
            // A steady controller can repeat its packet number. Requiring a new packet at
            // each read would reject held controls and a correctly released neutral pose.
            if (matches >= 2)
                return new(true, actual, $"Two consecutive readings matched on XInput slot {slot}.");
            await Task.Delay(PollMilliseconds, token);
        } while (timer.Elapsed < timeout);

        return new(false, actual, difference.Length > 0
            ? $"XInput slot {slot} returned unexpected values: {difference}."
            : $"Timed out waiting for a second matching reading on XInput slot {slot}.");
    }

    internal static string Compare(XInputReading sample, XInputExpectation expected)
    {
        var differences = new List<string>();
        void Check(string control, int value, int reference, int tolerance)
        {
            if (Math.Abs(value - reference) > tolerance)
                differences.Add($"{control}={value} (expected {reference})");
        }

        // Allow one input byte step for conversion via HID to XInput (-32768..32767).
        // This also covers the half-unit asymmetry at the signed axis midpoint.
        Check("LT", sample.Lt, expected.Lt, 1);
        Check("RT", sample.Rt, expected.Rt, 1);
        Check("LX", sample.Lx, expected.Lx, expected.Lx == 0 ? 0 : 257);
        Check("LY", sample.Ly, expected.Ly, expected.Ly == 0 ? 0 : 257);
        Check("RX", sample.Rx, expected.Rx, expected.Rx == 0 ? 0 : 257);
        Check("RY", sample.Ry, expected.Ry, expected.Ry == 0 ? 0 : 257);
        if (sample.Buttons != expected.Buttons)
            differences.Add($"buttons=0x{sample.Buttons:X4} (expected 0x{expected.Buttons:X4})");
        return string.Join(", ", differences);
    }

    internal static XInputReading? Read(int slot)
    {
        if ((uint)slot >= MaxUsers)
            throw new ArgumentOutOfRangeException(nameof(slot));
        uint status = XInputGetState((uint)slot, out var state);
        if (status == DeviceNotConnected) return null;
        if (status != 0)
            throw new Win32Exception((int)status, $"XInputGetState({slot}) returned error {status}.");
        return new(state.Packet, state.Lt, state.Rt, state.Lx, state.Ly, state.Rx, state.Ry, state.Buttons);
    }

    // XINPUT_STATE followed by its inline XINPUT_GAMEPAD (16 bytes in total).
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeState
    {
        public uint Packet;
        public ushort Buttons;
        public byte Lt, Rt;
        public short Lx, Ly, Rx, Ry;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out NativeState state);
}
