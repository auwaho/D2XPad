using HIDMaestro;

namespace D2XPad.Output;

// Shared by the live converter and the independent Stage 1B output probe.
internal sealed class SeriesController : IDisposable
{
    public const string ProfileId = "xbox-series-xs-bt";
    private readonly HMContext context = new();
    private readonly HMController controller;
    private bool disposed;

    internal event Action<HMController, HMOutputPacket> OutputReceived
    {
        add => controller.OutputReceived += value;
        remove => controller.OutputReceived -= value;
    }

    public SeriesController(string? identityKey = null)
    {
        try
        {
            var profile = LoadProfile(context);
            context.InstallDriver();
            controller = context.CreateController(profile, identityKey);
            Neutral();
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    internal static HMProfile LoadProfile(HMContext context)
    {
        context.LoadDefaultProfiles();
        var profile = context.GetProfile(ProfileId)
            ?? throw new InvalidOperationException($"HIDMaestro does not include the {ProfileId} profile.");
        if (profile.VendorId != 0x045E || profile.ProductId != 0x0B13 ||
            profile.DriverMode != "xinputhid" || profile.TriggerMode != "separate")
            throw new InvalidDataException($"The {ProfileId} profile has unexpected device settings.");
        return profile;
    }

    internal static HMGamepadState Encode(HMProfile profile, byte lx, byte ly, byte rx, byte ry,
        byte lt, byte rt, HMButton buttons, HMHat hat) => new()
    {
        // Series HID and 85HF both increase Y downward. xinputhid publishes positive Y upward.
        Axes = HMGamepadStateHelpers.StandardAxes(profile, leftStickX: HidStick(lx, 32768),
            leftStickY: HidStick(ly, 32767), rightStickX: HidStick(rx, 32768),
            rightStickY: HidStick(ry, 32767), leftTrigger: lt / 255f, rightTrigger: rt / 255f),
        Buttons = buttons,
        Hat = hat
    };

    // Different middle codes produce an exact signed XInput zero after Y reversal.
    private static float HidStick(byte value, int center)
    {
        float code = value <= 128 ? value / 128f * center
            : center + (value - 128) / 127f * (ushort.MaxValue - center);
        return code / ushort.MaxValue;
    }

    public void Submit(byte lx, byte ly, byte rx, byte ry, byte lt, byte rt, HMButton buttons, HMHat hat)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        controller.SubmitState(Encode(controller.Profile, lx, ly, rx, ry, lt, rt, buttons, hat));
    }

    private void Neutral() => Submit(128, 128, 128, 128, 0, 0, HMButton.None, HMHat.None);

    public void Dispose()
    {
        if (disposed) return;
        try { Neutral(); }
        finally
        {
            disposed = true;
            context.Dispose();
        }
    }
}
