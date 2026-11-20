using System.Diagnostics;
using System.Text.Json;
using D2XPad.SeriesProbe;
using D2XPad.InputGame;

namespace D2XPad.Live;

internal sealed record GameFrame(int Frame, XInputReading? Reading, int Score, GameActions Actions);
internal sealed record BrowserFrame(double Time, bool Visible, BrowserPad? Reading);
internal sealed record ConsumerResult(bool Passed, bool GamePassed, bool? BrowserPassed,
    GameFrame[] GameFrames, BrowserPad? BrowserReading, BrowserFrame[] BrowserFrames, string? BrowserUserAgent, string Detail);

// The independent game and the browser receive a sample request only. Both read their
// platform's controller API and return actual values for the current held position.
internal sealed class LiveVerifier : IAsyncDisposable
{
    private readonly BrowserProbe? browser;
    private readonly ProcessStartInfo gameStart;
    private Process game;
    private bool gameRestarted;
    private BrowserPad? identity;
    private GameActions? priorActions;
    private ushort priorButtons;

    public LiveVerifier(int slot, BrowserProbe? browser, string gameExe)
    {
        this.browser = browser;
        gameStart = new ProcessStartInfo(Path.GetFullPath(gameExe))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        gameStart.ArgumentList.Add(slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        gameStart.ArgumentList.Add("--probe");
        game = StartGame();
    }

    private Process StartGame() => Process.Start(gameStart) ?? throw new IOException("Could not start Beacon Run.");

    public async Task IdentifyBrowserAsync(Func<bool, CancellationToken, Task> setA, CancellationToken token)
    {
        if (browser is null) return;
        await browser.WaitForPageAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token);
        // A click/keypress can expose a previously hidden browser pad. Repeat one full
        // identification cycle in case enumeration finished during the initial release.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await setA(false, token);
            var before = await browser.SampleAsync(token);
            if (!before[^1].Focused)
                throw new InvalidOperationException("The browser tab is hidden or unfocused. Activate the live monitor.");
            BrowserSnapshot[] down;
            try
            {
                await setA(true, token);
                down = await browser.SampleAsync(token);
            }
            finally { await setA(false, CancellationToken.None); }
            var after = await browser.SampleAsync(token);
            identity = BrowserProbe.Identify(before, down, after);
            if (identity is not null)
            {
                browser.SelectedPad = identity;
                return;
            }
        }
        throw new InvalidOperationException("Could not identify one standard browser controller by its A press and release.");
    }

    public async Task<ConsumerResult> SampleAsync(XInputExpectation expected, CancellationToken token, bool checkTransitions = false)
    {
        if (browser is not null && identity is null)
            throw new InvalidOperationException("Identify the virtual browser controller before sampling.");
        var browserTask = browser is null ? Task.FromResult(Array.Empty<BrowserSnapshot>()) : browser.SampleAsync(token);
        var gameTask = SampleGameAsync(token, allowRestart: !checkTransitions);
        try { await Task.WhenAll(browserTask, gameTask); }
        catch when (gameTask.IsFaulted)
        {
            // A backgrounded browser can be retried. A game failure must still reach the guide.
            await gameTask;
            throw;
        }
        var frames = await gameTask;
        var detail = new List<string>();
        bool gamePassed = frames.Length == 3 && frames.Zip(frames.Skip(1)).All(p => p.First.Frame < p.Second.Frame);
        if (!gamePassed) detail.Add("The game returned incomplete or duplicate frames.");
        if (frames.Length != 0)
        {
            GameActions actual = frames[^1].Actions;
            if (frames.Any(f => f.Actions != actual))
            {
                gamePassed = false;
                detail.Add("Game actions changed while the same input was held.");
            }
            if (checkTransitions && priorActions is { } before)
            {
                int NewPress(int bit) => (expected.Buttons & bit) != 0 && (priorButtons & bit) == 0 ? 1 : 0;
                var wanted = new GameActions(before.Fire + NewPress(0x1000), before.Recall + NewPress(0x2000),
                    before.PreviousColor + NewPress(0x0100), before.NextColor + NewPress(0x0200));
                if (actual != wanted)
                {
                    gamePassed = false;
                    detail.Add($"Expected game actions {wanted}; observed {actual}.");
                }
            }
            priorActions = actual;
            priorButtons = expected.Buttons;
        }
        foreach (var frame in frames)
        {
            if (!checkTransitions && frame.Reading is { } physical && MatchesPhysicalReading(physical, expected)) continue;
            string difference = frame.Reading is null ? "XInput slot disconnected" : XInputReader.Compare(frame.Reading, expected);
            if (difference.Length == 0) continue;
            gamePassed = false;
            detail.Add($"Game frame {frame.Frame}: {difference}.");
            break;
        }
        var browserFrames = await browserTask;
        bool? browserPassed = null;
        BrowserPad? reading = null;
        var observedBrowser = new List<BrowserFrame>();
        if (identity is not null)
        {
            browserPassed = browserFrames.Length >= 3;
            foreach (var frame in browserFrames.TakeLast(3))
            {
                reading = frame.Pads.FirstOrDefault(p => p.Index == identity.Index && p.Id == identity.Id && p.Mapping == "standard");
                observedBrowser.Add(new(frame.Time, frame.Visible, reading));
                if (!frame.Visible || reading is null) { browserPassed = false; continue; }
                if (!MatchesStandardPad(reading, expected, physical: !checkTransitions)) browserPassed = false;
            }
            if (browserPassed == false) detail.Add("At least one of the last three browser frames did not match the held position.");
        }
        return new(gamePassed && browserPassed != false, gamePassed, browserPassed, frames, reading, observedBrowser.ToArray(),
            browser?.UserAgent, string.Join(" ", detail));
    }

    // Physical gestures can move between the Raw Input event and the three consumer frames.
    // Keep the guide's released (0..2) and fully pressed (249..255) ranges at the usual precision.
    private static bool PartialTrigger(byte expected) => expected is > 2 and < 249;

    internal static bool MatchesPhysicalReading(XInputReading sample, XInputExpectation expected)
    {
        static byte Target(byte actual, byte target) =>
            PartialTrigger(target) && Math.Abs(actual - target) <= 12 ? actual : target;
        return XInputReader.Compare(sample, expected with
        {
            Lt = Target(sample.Lt, expected.Lt), Rt = Target(sample.Rt, expected.Rt)
        }).Length == 0;
    }

    internal static bool MatchesStandardPad(BrowserPad pad, XInputExpectation expected, bool physical = false)
    {
        // W3C standard-gamepad button order. LT/RT occupy 6/7 and are independent analog values.
        ReadOnlySpan<ushort> bits = [0x1000,0x2000,0x4000,0x8000,0x0100,0x0200,0,0,
            0x0020,0x0010,0x0040,0x0080,1,2,4,8,0x0400];
        if (pad.Buttons.Length < 16 || pad.Axes.Length < 4) return false;
        static bool Close(double value, double target, double tolerance = .02) =>
            double.IsFinite(value) && Math.Abs(value - target) <= tolerance;
        for (int i = 0; i < bits.Length; i++)
        {
            double target = i switch { 6 => expected.Lt / 255d, 7 => expected.Rt / 255d, _ => (expected.Buttons & bits[i]) != 0 ? 1 : 0 };
            bool partial = physical && (i == 6 && PartialTrigger(expected.Lt) || i == 7 && PartialTrigger(expected.Rt));
            if (!Close(i < pad.Buttons.Length ? pad.Buttons[i] : 0, target, partial ? 12 / 255d + 1e-12 : .02)) return false;
        }
        static double Axis(short v) => v < 0 ? v / 32768d : v / 32767d;
        return Close(pad.Axes[0], Axis(expected.Lx)) && Close(pad.Axes[1], -Axis(expected.Ly)) &&
            Close(pad.Axes[2], Axis(expected.Rx)) && Close(pad.Axes[3], -Axis(expected.Ry));
    }

    private async Task<GameFrame[]> SampleGameAsync(CancellationToken token, bool allowRestart)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await ReadGameFramesAsync(token); }
            catch (IOException ex)
            {
                // The handles can close just before Windows signals process termination.
                try { await game.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMilliseconds(500), token); }
                catch (TimeoutException)
                {
                    throw new IOException($"Beacon Run lost its sample channel while still running: {ex.Message}");
                }
                int code = game.ExitCode;
                if (code != 0)
                    throw new IOException($"Beacon Run exited with code {code} (0x{code:X8}).");
                if (!allowRestart)
                    throw new IOException("Beacon Run closed during replay. Continuous game action counters are required.");
                if (gameRestarted)
                    throw new IOException("Beacon Run closed a second time. Keep the game open throughout the live check.");

                Console.WriteLine("Beacon Run closed normally. Reopening it once and resampling the current gesture.");
                var next = StartGame();
                game.Dispose();
                game = next;
                gameRestarted = true;
                priorActions = null;
                priorButtons = 0;
            }
        }
    }

    private async Task<GameFrame[]> ReadGameFramesAsync(CancellationToken token)
    {
        if (game.HasExited) throw new IOException("Beacon Run has exited.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await game.StandardInput.WriteLineAsync("sample".AsMemory(), timeout.Token);
            await game.StandardInput.FlushAsync(timeout.Token);
            string? line = await game.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new EndOfStreamException("Beacon Run closed its sample channel.");
            return JsonSerializer.Deserialize<GameFrame[]>(line) ?? throw new InvalidDataException("Missing native game frames.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Beacon Run did not return its three frames within five seconds.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (game.HasExited) return;
            try { game.StandardInput.Close(); }
            catch (IOException) { /* The child's read handle may already have closed. */ }
            try { await game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { if (!game.HasExited) game.Kill(); }
        }
        finally { game.Dispose(); }
    }
}
