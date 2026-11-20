using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace D2XPad.SeriesProbe;

internal static class Program
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed class RunReport
    {
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;
        public string Profile { get; } = SeriesOutput.ProfileId;
        public string Sdk { get; } = typeof(HIDMaestro.HMContext).Assembly.GetName().Version!.ToString();
        public string Windows { get; } = Environment.OSVersion.VersionString;
        public string? UserAgent { get; set; }
        public int? Slot { get; set; }
        public BrowserPad? BrowserDevice { get; set; }
        public List<object> Identification { get; } = [];
        public List<object> Cases { get; } = [];
        public int XInputPassed { get; set; }
        public int BrowserPassed { get; set; }
        public bool? SlotReleased { get; set; }
        public List<string> Errors { get; } = [];
        public bool Passed => Errors.Count == 0 && XInputPassed == ProbeCases.All.Count
            && BrowserPassed == ProbeCases.All.Count && SlotReleased == true;
    }

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args is ["--driver-host", var session]) return await DriverHost.RunAsync(session);
            if (args is ["--self-check"])
            {
                Console.WriteLine(SeriesOutput.CheckEncoding());
                Console.WriteLine($"Occupied XInput slots: [{string.Join(", ", XInputReader.ReadAll().Keys)}].");
                return 0;
            }
            if (args.Length > 0)
            {
                Console.WriteLine("D2XPad.SeriesProbe.exe [--self-check]");
                return args is ["--help"] ? 0 : 2;
            }
            if (DriverHost.IsAdmin()) throw new InvalidOperationException("Run the probe from a standard Windows session. The HIDMaestro driver host will request administrator privileges.");
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            return await VerifyAsync(stop.Token);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 2; }
    }

    private static async Task<int> VerifyAsync(CancellationToken token)
    {
        var report = new RunReport();
        var dir = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "SeriesProbe-runs"));
        string file = Path.Combine(dir.FullName, $"series-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
        try
        {
            var before = XInputReader.ReadAll().Keys.ToArray();
            if (before.Length == 4) throw new InvalidOperationException("All XInput slots are in use.");
            Console.WriteLine("HIDMaestro will create a virtual Xbox Series X|S controller. Approve the Windows prompt if it appears.");
            await using (var host = await DriverHost.Connection.OpenAsync(token))
            {
                int slot = await XInputReader.WaitForNewSlotAsync(before, TimeSpan.FromSeconds(12), token);
                report.Slot = slot;
                Console.WriteLine($"Virtual controller connected to XInput slot {slot}.");
                await using var web = new BrowserProbe();
                BrowserPad? pad = null;
                try
                {
                    BrowserWindow.Open(web.Url);
                    Console.WriteLine($"Test page: {web.Url}");
                    await web.WaitForPageAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
                    pad = await IdentifyAsync(host, web, slot, report, token);
                    report.BrowserDevice = pad;
                    if (pad == null) throw new IOException("Could not identify a single standard-mapping gamepad from the A button press and release.");
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    report.Errors.Add("Gamepad identification failed: " + ex.GetBaseException().Message);
                    Console.Error.WriteLine(report.Errors[^1]);
                }
                foreach (var pose in ProbeCases.All)
                {
                    await host.SendAsync(pose, token);
                    var read = await XInputReader.ObserveAsync(slot, pose.ExpectedXInput, Settle, token);
                    bool browserPassed = false;
                    BrowserPad? actual = null;
                    var b = pose.BrowserAxes;
                    var t = pose.BrowserTriggers;
                    if (pad != null)
                    {
                        try
                        {
                            var frames = await web.SampleAsync(token);
                            (browserPassed, actual) = BrowserProbe.Match(frames, pad, t[0], t[1], b[0], b[1], b[2], b[3], pose.A);
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            report.Errors.Add("Gamepad API: " + ex.GetBaseException().Message);
                            pad = null;
                        }
                    }
                    if (read.Passed) report.XInputPassed++;
                    if (browserPassed) report.BrowserPassed++;
                    report.Cases.Add(new { pose.Id, raw85hf = new { pose.Lt, pose.Rt, pose.Lx, pose.Ly, pose.Rx, pose.Ry },
                        pose.ExpectedXInput, xInput = read, expectedBrowser = new { axes = b, triggers = t },
                        browser = actual, browserPassed });
                    Console.WriteLine($"{pose.Id,-19} XInput {(read.Passed ? "+" : "×")}   Gamepad API {(browserPassed ? "+" : "×")}");
                }
            }
        }
        catch (Exception ex)
        {
            report.Errors.Add(ex.GetBaseException().Message);
            Console.Error.WriteLine(report.Errors[^1]);
        }
        finally
        {
            if (report.Slot is { } slot)
            {
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (XInputReader.ReadAll().ContainsKey(slot) && timer.Elapsed < TimeSpan.FromSeconds(10))
                        await Task.Delay(100);
                    report.SlotReleased = !XInputReader.ReadAll().ContainsKey(slot);
                }
                catch (Exception ex) { report.Errors.Add(ex.Message); }
            }
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(report, Json));
            Console.WriteLine($"XInput {report.XInputPassed}/{ProbeCases.All.Count}, Gamepad API {report.BrowserPassed}/{ProbeCases.All.Count}.\nReport: {file}");
        }
        return report.Passed ? 0 : 1;
    }

    private static async Task<BrowserPad?> IdentifyAsync(DriverHost.Connection host, BrowserProbe web,
        int slot, RunReport report, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var samples = new List<BrowserSnapshot[]>();
            foreach (var pose in new[] { ProbeCases.Neutral, ProbeCases.Identify, ProbeCases.Neutral })
            {
                await host.SendAsync(pose, token);
                var x = await XInputReader.ObserveAsync(slot, pose.ExpectedXInput, Settle, token);
                var frames = await web.SampleAsync(token);
                report.UserAgent = frames[^1].UserAgent;
                report.Identification.Add(new { pose.Id, xInput = x, lastBrowserFrame = frames[^1] });
                if (!x.Passed) throw new IOException("The XInput reading did not match the identification signal. " + x.Detail);
                samples.Add(frames);
            }
            var pad = BrowserProbe.Identify(samples[0], samples[1], samples[2]);
            if (pad != null) return pad;
        }
        return null;
    }
}
