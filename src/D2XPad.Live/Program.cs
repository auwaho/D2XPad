using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using D2XPad.SeriesProbe;

namespace D2XPad.Live;

internal static class Program
{
    internal const string StopEvent = ConverterReservation.StopEvent;

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            if (args is ["--conversion-service", var ownerSid]) return ConversionService.Run(ownerSid);
            if (args is ["--driver-host", var session]) return await LiveOutput.RunHostAsync(session);
            if (args is ["--driver-host", var feedbackSession, "--rumble"])
                return await LiveOutput.RunHostAsync(feedbackSession, enableRumble: true);
            if (args.FirstOrDefault() == "rumble-probe") return await RumbleProbe.RunAsync(args[1..]);
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("""
                    D2XPad · DirectInput to XInput Converter
                    D2XPad.Live [play] [--profile NAME-OR-FILE | --bindings FILE] [--device N | --device-path PATH] [--vibration] [--output auto|portable|service]
                    D2XPad.Live check [--profile NAME-OR-FILE | --bindings FILE] [--device N] [--resume PRIOR-LIVE-REPORT] [--keep-playing] [--output auto|portable|service]
                    D2XPad.Live check-capture FILE
                    D2XPad.Live profiles
                    D2XPad.Live import FILE [NAME]
                    D2XPad.Live switch NAME-OR-FILE
                    D2XPad.Live status [--json]
                    D2XPad.Live check-reconnect
                    D2XPad.Live check-sleep
                    D2XPad.Live rumble-probe survey | virtual | physical
                    D2XPad.Live list | stop

                    Run list to see connected controllers with a calibration. With no profile specified,
                    play uses the selected controller's default. The guided check and replay require the 85HF layout.
                    """);
                return 0;
            }
            if (args is ["list"])
            {
                var devices = PhysicalFeed.Devices();
                for (int i = 0; i < devices.Length; i++)
                {
                    var d = devices[i];
                    Console.WriteLine($"[{i + 1}] {d.Transport} {PhysicalSession.LayoutFor(d)!.ShortName} {d.VendorId:X4}:{d.ProductId:X4}, " +
                        $"{d.UsagePage:X4}/{d.Usage:X4}, {d.InputReportLength} bytes, ID {string.Join(", ", d.InputReportIds ?? [])} · {d.Path}");
                }
                if (devices.Length == 0) Console.WriteLine("No supported controller is connected.");
                return 0;
            }
            if (args is ["stop"])
            {
                if (EventWaitHandle.TryOpenExisting(StopEvent, out var existing))
                {
                    using (existing) existing.Set();
                    Console.WriteLine("Stop requested.");
                }
                else Console.WriteLine("D2XPad is already stopped.");
                return 0;
            }
            var catalog = new ProfileCatalog();
            if (args is ["profiles"])
            {
                foreach (var name in catalog.Names()) Console.WriteLine(name);
                return 0;
            }
            if (args is ["import", _, ..] && args.Length is 2 or 3)
            {
                var imported = catalog.Import(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : null);
                Console.WriteLine($"Imported {imported.Name}. Use switch {imported.Name} to apply it to a running session.");
                return 0;
            }
            if (args is ["status"] or ["status", "--json"] or ["switch", _])
            {
                var status = await SessionControl.SendAsync(args[0],
                    args[0] == "switch" ? ProfileSelection.Normalize(args[1]) : null, CancellationToken.None);
                if (args is ["status", "--json"])
                    Console.WriteLine(JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
                else
                {
                    string connection = !status.Connected ? "waiting for the selected controller to reconnect"
                        : status.WaitingForNeutral ? "connected, release all controls" : "connected";
                    Console.WriteLine($"Profile {status.Profile} · {connection} · XInput slot {status.Slot}");
                    if (status.OutputMode != null) Console.WriteLine($"Virtual output: {status.OutputMode}.");
                    Console.WriteLine($"Physical reports: {status.Received}. Disconnects: {status.Disconnects}. Reconnects: {status.Reconnects}.");
                    if (status.VibrationEnabled)
                    {
                        Console.WriteLine($"Nonzero motor writes completed: {status.VibrationEffects}.");
                        if (status.VibrationProblem is { } problem) Console.WriteLine(problem);
                    }
                }
                return 0;
            }
            if (args is ["check-reconnect"] or ["check-sleep"])
            {
                using var observe = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelCheck = (_, e) => { e.Cancel = true; observe.Cancel(); };
                Console.CancelKeyPress += cancelCheck;
                try
                {
                    return args[0] == "check-sleep"
                        ? await SleepCheck.RunAsync(observe.Token)
                        : await ReconnectCheck.RunAsync(observe.Token);
                }
                finally { Console.CancelKeyPress -= cancelCheck; }
            }
            if (args.FirstOrDefault()?.StartsWith("--", StringComparison.Ordinal) == true) args = ["play", .. args];
            string command = args.FirstOrDefault() ?? "play";
            if (command is not ("play" or "check" or "check-capture")) throw new ArgumentException("Unknown command. Use --help.");
            string? profileSelector = null;
            string? capture = null;
            string? resume = null;
            int? device = null;
            string? devicePath = null;
            bool desktop = false;
            bool keepPlaying = false;
            bool vibration = false;
            LiveOutputMode? outputMode = null;
            if (command == "check-capture")
            {
                if (args.Length != 2) throw new ArgumentException("Use check-capture FILE with a labeled Stage 1A recording.");
                capture = Path.GetFullPath(args[1]);
            }
            else for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--device" when devicePath == null && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n > 0:
                        i++; device = n; break;
                    case "--device-path" when command == "play" && device == null && devicePath == null && i + 1 < args.Length:
                        devicePath = args[++i]; break;
                    case "--desktop" when command == "play" && Console.IsInputRedirected:
                        desktop = true; break;
                    case "--bindings" when profileSelector == null && i + 1 < args.Length:
                        profileSelector = Path.GetFullPath(args[++i]); break;
                    case "--profile" when profileSelector == null && i + 1 < args.Length:
                        profileSelector = args[++i]; break;
                    case "--resume" when command == "check" && resume == null && i + 1 < args.Length:
                        resume = Path.GetFullPath(args[++i]); break;
                    case "--keep-playing" when command == "check": keepPlaying = true; break;
                    case "--vibration" when command == "play": vibration = true; break;
                    case "--output" when outputMode == null && i + 1 < args.Length:
                        outputMode = args[++i] switch
                        {
                            "auto" => LiveOutputMode.Auto,
                            "portable" => LiveOutputMode.Portable,
                            "service" => LiveOutputMode.Service,
                            _ => throw new ArgumentException("Choose auto, portable or service for --output.")
                        };
                        break;
                    default: throw new ArgumentException("Invalid option. Use --help.");
                }
            }
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Start D2XPad from a standard Windows session. The driver host requests elevation separately.");
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, StopEvent, out bool created);
            if (!created) throw new InvalidOperationException("Another D2XPad live session is active. Use stop to end it first.");
            using var stop = new CancellationTokenSource();
            var listener = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => stop.Cancel(), null, -1, true);
            // A desktop launch owns this child's input pipe. Closing it cancels startup, including
            // when the user exits the window during UAC, without signalling another live session.
            if (desktop) _ = Task.Run(() =>
            {
                try { Console.In.ReadLine(); }
                catch (IOException) { } // The owning desktop process can exit abruptly.
                finally
                {
                    try { signal.Set(); }
                    catch (ObjectDisposedException) { }
                }
            });
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            if (!desktop) Console.CancelKeyPress += cancel;
            VerificationLog? log = null;
            try
            {
                var physical = capture == null ? PhysicalFeed.Select(device, devicePath) : null;
                var layout = physical == null ? DeviceLayout.Bluetooth85HF : PhysicalSession.LayoutFor(physical)
                    ?? throw new InvalidDataException("The selected HID collection has no supported layout.");
                if (command == "check" && layout.Id != DeviceLayout.Bluetooth85HF.Id)
                    throw new InvalidDataException("The guided hardware check requires the Bluetooth 85HF layout.");
                var selection = SelectProfile(catalog, profileSelector, layout);
                var map = selection.Profile.Bindings;
                if (capture != null && !map.UsesReplayDefaults)
                    throw new InvalidDataException("The recorded capture expects the built-in 85HF calibration and L4=LB, R4=RB, PL=A and PR=B. " +
                        "Run the live check to test your current calibration and bindings.");
                if (layout.Id != map.Layout.Id)
                    throw new InvalidDataException($"The selected profile is for {map.Layout.DisplayName}. Choose a profile for the connected controller.");
                bool rumble = (command == "play" || keepPlaying) && physical != null && RumbleSurvey.Supports(physical);
                if (vibration && !rumble)
                    throw new InvalidOperationException("Game vibration is unavailable for this controller and connection.");
                var replay = capture != null ? ReplayCases.Load(capture) : null;
                var steps = command == "check" ? LiveGestures.Create(selection.Profile.Assignments) : null;
                var occupied = XInputReader.ReadAll().Keys.ToArray();
                if (occupied.Length == 4) throw new IOException("All four XInput slots are in use.");
                log = command == "play" ? null : new VerificationLog(capture, selection.Profile.Assignments);
                if (resume != null) log!.Resume(resume, steps!);
                int? slot = null;
                try
                {
                    await using var output = await LiveOutput.ConnectAsync(stop.Token, enableRumble: rumble,
                        mode: outputMode ?? LiveOutputMode.Auto);
                    slot = await XInputSlotCheck.IdentifyAsync(output.Submit, XInputReader.ReadAll, Random.Shared,
                        TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1), stop.Token);
                    Console.WriteLine($"Xbox Series X|S connected to XInput slot {slot}.");
                    if (log != null) { log.Slot = slot; log.Save(); }
                    if (command == "play")
                    {
                        using var feed = new PhysicalFeed(physical!, map, output, vibration);
                        await PlayAsync(feed, output, slot.Value, catalog, selection, stop.Token);
                    }
                    else
                    {
                        using var work = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        var verify = LiveChecks.RunAsync(output, slot.Value, selection, catalog, physical, replay, steps, keepPlaying, log!, work.Token);
                        try
                        {
                            var done = await Task.WhenAny(verify, output.Completion);
                            await done;
                            if (done != verify) throw new IOException("The driver host closed unexpectedly.");
                        }
                        finally { await work.CancelAsync(); try { await verify; } catch when (verify.IsCanceled) { } }
                    }
                }
                catch (Exception ex)
                {
                    if (log != null && !(ex is OperationCanceledException && log.ChecksCompleted)) log.Error = ex.GetBaseException().Message;
                    throw;
                }
                finally
                {
                    if (slot is int index)
                    {
                        var timer = Stopwatch.StartNew();
                        while (XInputReader.ReadAll().ContainsKey(index) && timer.Elapsed < TimeSpan.FromSeconds(8))
                            await Task.Delay(100);
                        bool released = !XInputReader.ReadAll().ContainsKey(index);
                        if (log != null) log.SlotReleased = released;
                        Console.WriteLine(released ? "Virtual XInput slot released." : "Virtual XInput slot still occupied.");
                    }
                    log?.Save();
                }
                return log is null || log.Passed ? 0 : 1;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Console.WriteLine(log is { ChecksCompleted: false } ? "Stopped before verification completed." : "Stopped.");
                return command == "play" || log?.Passed == true ? 0 : 1;
            }
            finally { listener.Unregister(null); if (!desktop) Console.CancelKeyPress -= cancel; }
        }
        catch (DriverApprovalCanceledException ex) when (args.Contains("--desktop", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("D2XPad: " + ex.Message);
            return DriverApprovalCanceledException.DesktopExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine("D2XPad: " + ex.Message); return 2; }
    }

    internal static ProfileSelection SelectProfile(ProfileCatalog catalog, string? selector, DeviceLayout layout)
    {
        if (selector != null) return ProfileSelection.Load(catalog, selector);
        if (layout.Id != DeviceLayout.Bluetooth85HF.Id) return ProfileSelection.Load(catalog, layout.DefaultProfileName);
        var legacy = Path.Combine(catalog.DirectoryPath, "85hf-bindings.json");
        if (!File.Exists(legacy))
            legacy = Path.Combine(AppContext.BaseDirectory, "85hf-bindings.json"); // Read an older unpacked install before it is upgraded.
        if (File.Exists(legacy))
        {
            var customized = ProfileSelection.Load(catalog, legacy);
            if (!customized.Profile.Bindings.UsesDefault85HfAssignments) return customized;
        }
        return ProfileSelection.Load(catalog, layout.DefaultProfileName);
    }

    internal static async Task PlayAsync(PhysicalFeed feed, LiveOutput output, int slot, ProfileCatalog catalog,
        ProfileSelection selection, CancellationToken token)
    {
        await using var controls = new SessionControl(feed, slot, catalog, selection, output.OutputMode);
        Console.WriteLine($"Live {feed.Layout.Transport} input is active with profile {selection.Selector}. Use switch NAME to change profiles or stop to exit.");
        var running = Task.WhenAny(feed.Failure, output.Completion, controls.Completion, Task.Delay(Timeout.Infinite, token));
        try
        {
            while (feed.Received == 0 && !running.IsCompleted)
                await Task.WhenAny(running, Task.Delay(150, token));
            if (feed.Received != 0) Console.WriteLine("First physical controller report received.");
            await await running;
        }
        finally { Console.WriteLine($"Physical reports: {feed.Received}. Unrecognized reports: {feed.Unexpected}."); }
    }
}
