using HIDMaestro;
using D2XPad.Output;

namespace D2XPad.SeriesProbe;

internal sealed class SeriesOutput : IDisposable
{
    public const string ProfileId = SeriesController.ProfileId;
    private readonly SeriesController controller = new();

    public static string CheckEncoding()
    {
        using var ctx = new HMContext();
        var profile = SeriesController.LoadProfile(ctx);
        HMAxis[] usages = [HMAxis.X, HMAxis.Y, HMAxis.Rx, HMAxis.Ry, HMAxis.Z, HMAxis.Rz];
        foreach (var frame in ProbeCases.All.Prepend(ProbeCases.Identify))
        {
            var state = Encode(profile, frame);
            // Both the Series HID descriptor and standard browser axes have downward Y travel.
            var expected = frame.BrowserAxes.Select(axis => (axis + 1) / 2)
                .Concat(frame.BrowserTriggers).ToArray();
            for (int i = 0; i < usages.Length; i++)
            {
                if (state.Axes is null || !state.Axes.TryGetValue(usages[i], out float value) ||
                    Math.Abs(value - expected[i]) > 0.00001)
                    throw new InvalidDataException($"HIDMaestro axis {usages[i]} has an unexpected value in test state {frame.Id}.");
            }
            HMButton buttons = frame.ExpectedXInput.Buttons == 0x1000 ? HMButton.A : default;
            if (state.Buttons != buttons || state.Hat != HMHat.None)
                throw new InvalidDataException($"HIDMaestro returned unexpected button or D-pad values in test state {frame.Id}.");
        }
        return $"{ProfileId} ({profile.VendorId:X4}:{profile.ProductId:X4}): all six SDK axis values matched for {ProbeCases.All.Count} test states and the A button identification signal.";
    }

    public void Submit(ProbeCase frame) => controller.Submit(frame.Lx, frame.Ly, frame.Rx,
        frame.Ry, frame.Lt, frame.Rt, frame.A ? HMButton.A : default, HMHat.None);

    internal static HMGamepadState Encode(HMProfile profile, ProbeCase frame) => SeriesController.Encode(
        profile, frame.Lx, frame.Ly, frame.Rx, frame.Ry, frame.Lt, frame.Rt, frame.A ? HMButton.A : default, HMHat.None);

    public void Dispose() => controller.Dispose();
}
