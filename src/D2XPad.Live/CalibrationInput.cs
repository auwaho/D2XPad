using D2XPad.Capture;

namespace D2XPad.Live;

internal interface ICalibrationInput : IDisposable
{
    (long Version, byte[] Report) Read();
    Task<IReadOnlyList<byte[]>> ReadRestSamplesAsync(CancellationToken token);
}

internal sealed class CalibrationInput : ICalibrationInput
{
    private readonly RawInput input;
    private readonly object gate = new();
    private byte[]? latest;
    private long version;
    private string? error;
    private List<byte[]>? restSamples;

    public static HidCollection[] Devices(string? layoutId = null)
    {
        var entries = LayoutStore.Library();
        return RawInput.EnumeratePhysicalGameControllers()
            .Where(d => layoutId == null
                ? !PhysicalSession.Supports(d) && !entries.Any(e => e.IsArchived && PhysicalSession.MatchesDevice(e.Layout, d))
                : entries.Any(e => e.Layout.Id == layoutId && !e.IsArchived && PhysicalSession.MatchesDevice(e.Layout, d)))
            .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CalibrationInput(HidCollection collection)
    {
        if (!ControllerCalibration.IsCandidate(collection))
            throw new InvalidDataException("Select a physical USB or Bluetooth joystick or gamepad.");
        bool known85HF = PhysicalSession.MatchesDevice(DeviceLayout.Bluetooth85HF, collection);
        input = new([collection], (_, report) =>
        {
            if (known85HF && (report.Length != 32 || report[0] != 1)) return;
            lock (gate)
            {
                latest = report;
                version++;
                if (restSamples is { Count: < 256 }) restSamples.Add(report);
            }
        }, reason => { lock (gate) error = reason; });
    }

    public (long Version, byte[] Report) Read()
    {
        lock (gate)
        {
            if (error != null) throw new IOException(error);
            if (latest == null) throw new InvalidOperationException("Waiting for the first controller input report. Move and release a control.");
            return (version, (byte[])latest.Clone());
        }
    }

    public byte[] Snapshot() => Read().Report;

    public async Task<IReadOnlyList<byte[]>> ReadRestSamplesAsync(CancellationToken token)
    {
        lock (gate)
        {
            if (restSamples != null) throw new InvalidOperationException("A resting sample is already being recorded.");
            restSamples = [Read().Report];
        }
        try
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            lock (gate)
            {
                if (error != null) throw new IOException(error);
                return restSamples!;
            }
        }
        finally { lock (gate) restSamples = null; }
    }

    public void Dispose() => input.Dispose();
}
