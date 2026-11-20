using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace D2XPad.Capture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Console.OutputEncoding = new UTF8Encoding(false);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            if (args is ["guide"]) { Guide.Print(); return 0; }
            if (args is ["analyze", var file]) return ProfileCheck.Run(file, Console.Out);
            if (args is ["--help"] or ["-h"]) { Usage(); return 0; }
            if (args.Length == 0) args = ["record-raw"];
            if (args[0] is not ("list" or "record" or "probe" or "list-raw" or "record-raw"))
                throw new ArgumentException("Unknown command. Use --help to list commands.");
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new InvalidOperationException("Reading HID reports requires 64-bit Windows.");

            var options = Options.Parse(args);
            if (args[0] is "list-raw" or "record-raw")
                return await RunRaw(args[0], options, stop);
            var devices = RawInput.Enumerate(static (vid, pid, _, _) => vid == 0x2dc8 && pid == 0x2067)
                .GroupBy(d => d.ContainerId ?? d.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray()).ToArray();
            for (int i = 0; i < devices.Length; i++)
            {
                Console.WriteLine($"[{i + 1}] 2DC8:2067 · {Transport(devices[i])}");
                foreach (var d in devices[i])
                    PrintCollection(d);
            }
            if (devices.Length == 0)
                Console.WriteLine("Windows Raw Input found no device with VID/PID 2DC8:2067. Disconnect the USB cable and receiver, connect the 85HF over Bluetooth, then run list again.");
            if (args[0] == "list") return 0;
            if (devices.Length == 0) return 1;
            var selected = Select(devices, options.Device);
            if (args[0] == "probe")
            {
                int count = 0;
                string? error = null;
                using (new RawInput(selected, (_, _) => Interlocked.Increment(ref count),
                    message => { Interlocked.Exchange(ref error, message); stop.Cancel(); }))
                {
                    Console.WriteLine("Listening for HID reports for three seconds. Move a stick now.");
                    try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { }
                }
                Console.WriteLine($"HID reports received: {count}.");
                if (error is not null) throw new IOException(error);
                return count > 0 && !stop.IsCancellationRequested ? 0 : 1;
            }
            return await Record(selected, options, stop);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Capture stopped."); return 1; }
        catch (Exception ex) { Console.Error.WriteLine($"D2XPad: {ex.Message}"); return 2; }
    }

    private static void PrintCollection(HidCollection device) =>
        Console.WriteLine($"    Usage {device.UsagePage:X4}/{device.Usage:X4} · HID {device.InputReportLength?.ToString() ?? "?"} bytes · ID {string.Join(",", device.InputReportIds ?? [])} · {device.Path}");

    private static async Task<int> RunRaw(string command, Options options, CancellationTokenSource stop)
    {
        var devices = RawInput.EnumeratePhysicalGameControllers()
            .OrderBy(device => device.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < devices.Length; i++)
        {
            var device = devices[i];
            Console.WriteLine($"[{i + 1}] {device.VendorId:X4}:{device.ProductId:X4} · {device.Transport}");
            PrintCollection(device);
        }
        if (devices.Length == 0)
            Console.WriteLine("Windows Raw Input found no physical USB or Bluetooth gamepad or joystick.");
        if (command == "list-raw") return 0;
        if (devices.Length == 0) return 1;
        if (options.Device is null && devices.Length != 1)
            throw new InvalidOperationException("Select a collection from the list with --device N.");
        int index = options.Device.GetValueOrDefault(1) - 1;
        if (index >= devices.Length)
            throw new ArgumentException("The --device number is outside the collection list.");

        var path = Path.GetFullPath(options.Output ?? Path.Combine("captures",
            $"raw-hid-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.ndjson"));
        int seconds = options.Seconds ?? 30;
        string? error = null;
        using var capture = new CaptureFile(path, [devices[index]]);
        Console.WriteLine($"Capture: {path}");
        try
        {
            using var input = new RawInput([devices[index]], capture.Report,
                message => { Interlocked.Exchange(ref error, message); stop.Cancel(); });
            Console.WriteLine($"Recording collection {index + 1} for {seconds} seconds. Move the controls or press Ctrl+C to finish.");
            await Task.Delay(seconds * 1000, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { error = ex.Message; }

        if (capture.ReportCount == 0) error ??= "No HID reports were received.";
        capture.Finish(error is null, error);
        if (error is not null) Console.Error.WriteLine(error);
        return error is null ? 0 : 1;
    }

    private static string Transport(HidCollection[] sources) =>
        sources.Select(c => c.Transport).Distinct().Count() == 1 ? sources[0].Transport : "Unknown";

    private static HidCollection[] Select(HidCollection[][] groups, int? number)
    {
        if (number is { } index)
        {
            if (index < 1 || index > groups.Length)
                throw new ArgumentException("The --device number is outside the device list.");
            var selected = groups[index - 1];
            if (selected.Any(c => c.Transport == "USB"))
                throw new InvalidOperationException("The selected device uses USB. Connect the 85HF over Bluetooth for this test.");
            if (Transport(selected) != "Bluetooth")
                Console.WriteLine("Windows could not identify the transport. Check that you selected the Bluetooth 85HF before continuing.");
            return selected;
        }
        var bluetooth = groups.Where(g => Transport(g) == "Bluetooth").ToArray();
        if (bluetooth.Length == 1) return bluetooth[0];
        throw new InvalidOperationException("Automatic selection requires exactly one Bluetooth device. Check the list and add --device N. If the transport is Unknown, disconnect the USB cable and receiver first.");
    }

    private static async Task<int> Record(HidCollection[] selected, Options options, CancellationTokenSource stop)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Run record in an interactive terminal to follow the prompts and mark each step.");
        byte[] bytes = LoadProfile(options.Profile);
        using var document = JsonDocument.Parse(bytes.AsMemory().TrimStartBom());
        var profile = document.RootElement;
        if (!profile.TryGetProperty("vid", out var vid) || !string.Equals(vid.GetString(), "2DC8", StringComparison.OrdinalIgnoreCase)
            || !profile.TryGetProperty("pid", out var pid) || pid.GetString() != "2067"
            || !profile.TryGetProperty("reports", out var maps) || !maps.TryGetProperty("0", out _))
            throw new InvalidDataException("Expected a 2DC8:2067 HID profile with report key 0. Check the --profile path.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = Path.GetFullPath(options.Output ?? Path.Combine("captures",
            $"85HF-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.ndjson"));
        string profileName = options.Profile == null ? "Built-in 85HF map" : Path.GetFullPath(options.Profile);
        Console.WriteLine($"Profile: {profileName}\nSHA-256: {hash}\nCapture: {path}");

        bool completed = false;
        string? error = null;
        using (var capture = new CaptureFile(path, selected, (profile, hash)))
        {
            try
            {
                using var input = new RawInput(selected, capture.Report,
                    message => { Interlocked.Exchange(ref error, message); stop.Cancel(); });
                await Guide.RunAsync(capture.SetStep, () => capture.ReportCount, stop.Token);
                completed = !stop.IsCancellationRequested;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex.Message; }
            completed &= error is null && !stop.IsCancellationRequested;
            capture.Finish(completed, error ?? (completed ? null : "cancelled"));
        }
        if (!completed)
        {
            Console.Error.WriteLine(error ?? "Capture stopped early. The file contains the reports received so far.");
            return 1;
        }
        using var report = new StringWriter();
        int result = ProfileCheck.Run(path, report);
        Console.WriteLine(report.ToString());
        string summary = path + ".report.txt";
        using var text = new StreamWriter(new FileStream(summary, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        text.Write(report.ToString());
        Console.WriteLine($"Capture: {path}\nVerification report: {summary}");
        return result;
    }

    private static byte[] LoadProfile(string? path)
    {
        if (path != null) return File.ReadAllBytes(path);
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("D2XPad.Capture.85hf-capture-map.json")
            ?? throw new InvalidOperationException("The built-in 85HF capture map is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record Options(int? Device, string? Profile, string? Output, int? Seconds)
    {
        public static Options Parse(string[] args)
        {
            int? device = null;
            string? profile = null;
            string? output = null;
            int? seconds = null;
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}.");
                var value = args[i + 1];
                switch (args[i])
                {
                    case "--device" when args[0] is "record" or "probe" or "record-raw"
                        && int.TryParse(value, out var n) && n > 0: device = n; break;
                    case "--profile" when args[0] == "record": profile = value; break;
                    case "--output" when args[0] is "record" or "record-raw": output = value; break;
                    case "--seconds" when args[0] == "record-raw" && int.TryParse(value, out var duration)
                        && duration is > 0 and <= int.MaxValue / 1000: seconds = duration; break;
                    default: throw new ArgumentException($"Invalid option or value: {args[i]} {value}. Use --help to list valid options.");
                }
            }
            return new Options(device, profile, output, seconds);
        }
    }

    private static ReadOnlyMemory<byte> TrimStartBom(this Memory<byte> bytes) =>
        bytes.Span.StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;

    private static void Usage() => Console.WriteLine("""
        D2XPad · Raw HID capture
          list-raw                                    List physical USB and Bluetooth HID game controller collections
          record-raw [--device N] [--seconds N] [--output FILE]
                                                      Record raw reports for 30 seconds by default. Ctrl+C finishes early.
        With no arguments, D2XPad Capture starts a raw recording.

        85HF Bluetooth diagnostics
          list                                        List devices and HID collections
          guide                                       Show the capture sequence
          probe [--device N]                          Check for incoming HID reports
          record [--device N] [--profile FILE] [--output FILE]
          analyze FILE.ndjson                         Verify a saved capture
        """);
}
