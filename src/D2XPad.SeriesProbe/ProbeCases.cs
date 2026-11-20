namespace D2XPad.SeriesProbe;

// Raw values follow the measured 85HF Bluetooth layout. Expectations are named
// in the consumer's XInput coordinates, independently of the SDK encoder.
internal sealed record ProbeCase(
    string Id, XInputExpectation ExpectedXInput,
    byte Lx = 128, byte Ly = 128, byte Rx = 128, byte Ry = 128,
    byte Lt = 0, byte Rt = 0, bool A = false)
{
    private static double Normalize(short value) => value < 0 ? value / 32768d : value / 32767d;
    public double XInputLx => Normalize(ExpectedXInput.Lx);
    public double XInputLy => Normalize(ExpectedXInput.Ly);
    public double XInputRx => Normalize(ExpectedXInput.Rx);
    public double XInputRy => Normalize(ExpectedXInput.Ry);
    public double[] BrowserAxes => [XInputLx, -XInputLy, XInputRx, -XInputRy];
    public double[] BrowserTriggers => [ExpectedXInput.Lt / 255d, ExpectedXInput.Rt / 255d];
}

internal static class ProbeCases
{
    private static readonly XInputExpectation Center = new(0, 0, 0, 0, 0, 0);
    public static readonly ProbeCase Neutral = new("neutral", Center);
    public static readonly ProbeCase Identify = new("identify", Center with { Buttons = 0x1000 }, A: true);

    public static IReadOnlyList<ProbeCase> All { get; } =
    [
        Neutral,
        new("lt-quarter", Center with { Lt = 64 }, Lt: 64),
        new("rt-three-quarters", Center with { Rt = 192 }, Rt: 192),
        new("lt-full", Center with { Lt = 255 }, Lt: 255),
        new("rt-full", Center with { Rt = 255 }, Rt: 255),
        new("lt-low-rt-high", Center with { Lt = 64, Rt = 192 }, Lt: 64, Rt: 192),
        new("lt-high-rt-low", Center with { Lt = 192, Rt = 64 }, Lt: 192, Rt: 64),
        new("both-full", Center with { Lt = 255, Rt = 255 }, Lt: 255, Rt: 255),
        new("ls-left", Center with { Lx = short.MinValue }, Lx: 0),
        new("ls-right", Center with { Lx = short.MaxValue }, Lx: 255),
        new("ls-up", Center with { Ly = short.MaxValue }, Ly: 0),
        new("ls-down", Center with { Ly = short.MinValue }, Ly: 255),
        new("rs-left", Center with { Rx = short.MinValue }, Rx: 0),
        new("rs-right", Center with { Rx = short.MaxValue }, Rx: 255),
        new("rs-up", Center with { Ry = short.MaxValue }, Ry: 0),
        new("rs-down", Center with { Ry = short.MinValue }, Ry: 255),
        new("release", Center)
    ];
}
