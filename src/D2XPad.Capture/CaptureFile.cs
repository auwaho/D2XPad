using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace D2XPad.Capture;

internal sealed class CaptureFile : IDisposable
{
    private readonly StreamWriter writer;
    private readonly Dictionary<nint, string> ids = [];
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly object gate = new();
    private string? step;
    private long reports;
    public long ReportCount { get { lock (gate) return reports; } }

    public CaptureFile(string path, IReadOnlyList<HidCollection> sources, JsonElement profile, string profileHash)
    {
        var collections = sources.Select((source, index) =>
        {
            var id = $"c{index + 1}";
            ids.Add(source.Handle, id);
            return new
            {
                id, transport = source.Transport, usagePage = source.UsagePage, usage = source.Usage,
                vid = source.VendorId, pid = source.ProductId, inputReportLength = source.InputReportLength,
                inputReportIds = source.InputReportIds?.Select(id => (int)id).ToArray(),
                pathHash = Fingerprint(source.Path),
                containerHash = source.ContainerId is null ? null : Fingerprint(source.ContainerId)
            };
        }).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false), 16384);
        try
        {
            Write(new { type = "session", schemaVersion = 1, startedUtc = DateTimeOffset.UtcNow,
                os = Environment.OSVersion.VersionString, tickFrequency = Stopwatch.Frequency,
                profileSha256 = profileHash, profile, collections });
            writer.Flush();
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void Write<T>(T entry) => writer.WriteLine(JsonSerializer.Serialize(entry));

    public void Report(HidCollection source, byte[] data)
    {
        lock (gate)
        {
            Write(new { type = "report", ticks = Stopwatch.GetTimestamp() - origin,
                collection = ids[source.Handle], step, hex = Convert.ToHexString(data) });
            reports++;
        }
    }

    public void SetStep(string next)
    {
        lock (gate)
        {
            if (step is not null)
                Write(new { type = "step", step, phase = "end", ticks = Stopwatch.GetTimestamp() - origin });
            step = next == "idle" ? null : next;
            if (step is not null)
                Write(new { type = "step", step, phase = "start", ticks = Stopwatch.GetTimestamp() - origin });
            writer.Flush();
        }
    }

    // The capture thread has stopped before the footer is written. An interrupted
    // gesture keeps its open marker so replay cannot mistake it for a finished one.
    public void Finish(bool completed, string? reason)
    {
        Write(new { type = "end", completed, reason, reports, ticks = Stopwatch.GetTimestamp() - origin });
        writer.Flush();
        Console.WriteLine($"HID reports received: {reports}.");
    }

    public void Dispose() => writer.Dispose();
}
