using System.Security.Cryptography;
using System.Text.Json;
using D2XPad.Capture;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

internal sealed record CaseResult(string Id, string RawHex, long? PhysicalReport, XInputExpectation Expected,
    ConsumerResult Consumers, string? ObservedIn = null);

internal sealed class VerificationLog
{
    private readonly string file;
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public string Source { get; }
    public string? Capture { get; }
    public string? CaptureSha256 { get; }
    public JsonElement Bindings { get; }
    public int? Slot { get; set; }
    public BrowserPad? VirtualPad { get; set; }
    public string? UserAgent { get; set; }
    public string? CurrentStep { get; set; }
    public List<CaseResult> Cases { get; } = [];
    public string? ResumedFrom { get; private set; }
    public int InheritedCaseCount { get; private set; }
    public long PhysicalReports { get; set; }
    public long UnrecognizedReports { get; set; }
    public bool ChecksCompleted { get; set; }
    public bool? SlotReleased { get; set; }
    public string? Error { get; set; }
    public bool? ControlsPassed => ChecksCompleted ? Cases.Count > 0 && Cases.All(c => c.Consumers.Passed) : null;
    public bool Passed => ControlsPassed == true && SlotReleased == true && Error == null;

    public VerificationLog(string? capture, JsonElement assignments)
    {
        Source = capture == null ? "Bluetooth Raw Input" : "Selected frames from a labeled Bluetooth capture";
        Capture = capture == null ? null : Path.GetFileName(capture);
        if (capture != null) { using var stream = File.OpenRead(capture); CaptureSha256 = Convert.ToHexStringLower(SHA256.HashData(stream)); }
        Bindings = assignments.Clone();
        var dir = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "Live-runs"));
        string mode = capture == null ? "bluetooth" : "capture";
        file = Path.Combine(dir.FullName, $"{mode}-{Started:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
        Console.WriteLine($"Report: {file}");
    }

    public void Save()
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(temp, file, true);
    }

    public void Resume(string path, IReadOnlyList<Gesture> steps)
    {
        using var stream = File.OpenRead(path);
        using var prior = JsonDocument.Parse(stream);
        var root = prior.RootElement;
        if (root.GetProperty("source").GetString() != "Bluetooth Raw Input"
            || root.GetProperty("capture").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Resume requires a prior live Bluetooth check report.");
        if (!JsonElement.DeepEquals(Bindings, root.GetProperty("bindings")))
            throw new InvalidDataException("The prior live check used different button bindings.");
        var prefix = root.GetProperty("cases").Deserialize<CaseResult[]>(JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The prior report has no case list.");
        if (prefix.Length > steps.Count) throw new InvalidDataException("The prior report contains more cases than this guide.");
        for (int i = 0; i < prefix.Length; i++)
        {
            var item = prefix[i];
            var step = steps[i];
            if (item is null || item.Id != step.Id || item.PhysicalReport is not > 0 || item.RawHex is null)
                throw new InvalidDataException($"Expected recorded Bluetooth step {i + 1} ({step.Id}).");
            byte[] raw = Convert.FromHexString(item.RawHex);
            if (raw.Length != 32 || raw[0] != 1 || !step.Matches(raw) || item.Expected != LiveGestures.Expected(raw, step)
                || !ConfirmedInBothConsumers(item))
                throw new InvalidDataException($"The prior report does not verify {step.Id} in both consumers.");
        }
        if (prefix.Length == steps.Count)
            throw new InvalidDataException("All gestures already passed in that report. Run check for a fresh live measurement.");
        ResumedFrom = Path.GetFileName(path);
        Cases.AddRange(prefix.Select(c => c with { ObservedIn = c.ObservedIn ?? ResumedFrom }));
        InheritedCaseCount = prefix.Length;
        Console.WriteLine($"Retained {InheritedCaseCount} verified gestures from {ResumedFrom}. Next step: {steps[prefix.Length].Id}.");
    }

    private static bool ConfirmedInBothConsumers(CaseResult item)
    {
        if (item.Consumers is not { Passed: true, GamePassed: true, BrowserPassed: true,
            GameFrames.Length: 3, BrowserFrames.Length: 3, BrowserReading: { Mapping: "standard" } pad } c)
            return false;
        return c.GameFrames.All(f => f?.Reading is { } r && LiveVerifier.MatchesPhysicalReading(r, item.Expected))
            && c.GameFrames[0].Frame < c.GameFrames[1].Frame && c.GameFrames[1].Frame < c.GameFrames[2].Frame
            && c.BrowserFrames.All(f => f is { Visible: true, Reading: { Mapping: "standard" } p }
                && p.Index == pad.Index && p.Id == pad.Id && LiveVerifier.MatchesStandardPad(p, item.Expected, physical: true));
    }
}

internal static class LiveChecks
{
    public static async Task RunAsync(LiveOutput output, int slot, ProfileSelection selection, ProfileCatalog catalog,
        HidCollection? physical, IReadOnlyList<ReplayCase>? replay, IReadOnlyList<Gesture>? steps,
        bool keepPlaying, VerificationLog log, CancellationToken token)
    {
        PhysicalFeed? feed = null;
        var bindings = selection.Profile.Bindings;
        try
        {
            await using (var web = new BrowserProbe())
            await using (var verifier = new LiveVerifier(slot, web, Path.Combine(AppContext.BaseDirectory, "D2XPad.InputGame.exe")))
            {
                web.Prompt = "Identifying the virtual Xbox controller. Leave the physical gamepad at rest.";
                BrowserWindow.Open(web.Url);
                Console.WriteLine($"Live Gamepad API monitor: {web.Url}");
                async Task IdentifySignal(bool pressed, CancellationToken ct)
                {
                    ushort button = pressed ? (ushort)0x1000 : (ushort)0;
                    output.Submit(LiveState.Neutral with { Buttons = button });
                    var check = await XInputReader.ObserveAsync(slot, new(0, 0, 0, 0, 0, 0, button), TimeSpan.FromSeconds(1), ct);
                    if (!check.Passed) throw new IOException(check.Detail);
                }
                while (true)
                {
                    try { await verifier.IdentifyBrowserAsync(IdentifySignal, token); break; }
                    catch (Exception ex) when (physical != null && WaitingForBrowser(ex))
                    {
                        web.Prompt = "Bring this window to the foreground. Leave the gamepad at rest while D2XPad identifies it.";
                        await Task.Delay(500, token);
                    }
                }
                log.VirtualPad = web.SelectedPad;
                log.UserAgent = web.UserAgent;
                if (replay != null)
                {
                    foreach (var c in replay)
                    {
                        token.ThrowIfCancellationRequested();
                        web.Prompt = $"Checking recorded 85HF input · {c.Id}";
                        if (!bindings.TryDecode(c.Report, out var state, out _))
                            throw new InvalidDataException($"Unrecognized capture frame for {c.Id}.");
                        output.Submit(state);
                        // The game samples three frames as soon as requested. Wait for the new state to reach the driver.
                        await XInputReader.ObserveAsync(slot, c.Expected, TimeSpan.FromMilliseconds(700), token);
                        Record(log, c.Id, c.Report, null, c.Expected, await verifier.SampleAsync(c.Expected, token, checkTransitions: true));
                    }
                }
                else
                {
                    feed = new PhysicalFeed(physical!, bindings, output);
                    using var guide = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var run = CheckPhysicalAsync(feed, steps!, verifier, web, log, guide.Token);
                    try { await await Task.WhenAny(feed.Failure, run); }
                    finally { await guide.CancelAsync(); try { await run; } catch when (run.IsCanceled) { } }
                }
                log.ChecksCompleted = true;
                log.CurrentStep = null;
                log.Save();
                int passed = log.Cases.Count(c => c.Consumers.Passed);
                string summary = $"Completed {passed}/{log.Cases.Count} checks in the browser and the XInput game.";
                Console.WriteLine(summary);
                await web.FinishAsync(summary, token);
            }
            if (keepPlaying && feed != null) await Program.PlayAsync(feed, output, slot, catalog, selection, token);
        }
        finally
        {
            feed?.Dispose();
            if (feed != null) { log.PhysicalReports = feed.Received; log.UnrecognizedReports = feed.Unexpected; }
        }
    }

    private static async Task CheckPhysicalAsync(PhysicalFeed feed, IReadOnlyList<Gesture> steps,
        LiveVerifier verifier, BrowserProbe web, VerificationLog log, CancellationToken token)
    {
        for (int i = log.InheritedCaseCount; i < steps.Count; i++)
        {
            var step = steps[i];
            string prompt = $"{i + 1}/{steps.Count} · {step.Prompt} Hold until the next instruction.";
            Console.WriteLine(prompt);
            web.Prompt = prompt;
            log.CurrentStep = step.Id;
            log.Save();
            while (true)
            {
                await Task.Delay(80, token);
                var physical = feed.Latest;
                if (physical == null || !step.Matches(physical.Raw)) continue;
                var expected = LiveGestures.Expected(physical.Raw, step);
                ConsumerResult result;
                try { result = await verifier.SampleAsync(expected, token); }
                catch (Exception ex) when (WaitingForBrowser(ex))
                {
                    // The physical bridge stays active while the user switches between the game and the browser.
                    await Task.Delay(400, token);
                    continue;
                }
                if (result.Passed && feed.Latest is { } now && step.Matches(now.Raw))
                {
                    log.PhysicalReports = feed.Received;
                    Record(log, step.Id, physical.Raw, physical.Number, expected, result);
                    break;
                }
                web.Prompt = prompt + " Keep the position steady while both apps read it.";
            }
        }
    }

    private static bool WaitingForBrowser(Exception ex) =>
        ex is InvalidOperationException && ex.Message.Contains("tab is hidden", StringComparison.OrdinalIgnoreCase)
        || ex is TimeoutException && ex.Message.Contains("browser", StringComparison.OrdinalIgnoreCase);

    private static void Record(VerificationLog log, string id, byte[] report, long? number, XInputExpectation expected, ConsumerResult result)
    {
        log.Cases.Add(new(id, Convert.ToHexString(report), number, expected, result));
        log.Save();
        Console.WriteLine($"{id,-20} game {(result.GamePassed ? "PASS" : "FAIL")}, browser {(result.BrowserPassed == true ? "PASS" : "FAIL")} {result.Detail}");
    }
}
