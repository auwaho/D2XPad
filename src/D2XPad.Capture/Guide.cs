using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace D2XPad.Capture;

internal static class Guide
{
    private sealed record Cue(string Text, int Milliseconds);
    private sealed record Step(string Id, string Group, string Title, Cue[] Cues);

    private static readonly IReadOnlyList<Step> Steps = CreateSteps();
    public static IReadOnlyList<string> StepIds { get; } = Steps.Select(step => step.Id).ToArray();

    public static void Print()
    {
        Console.WriteLine("85HF Bluetooth gamepad capture guide");
        Console.WriteLine("Press Enter at the start of each group. You can then use both hands on the gamepad.");
        Console.WriteLine("Move only the control shown and hold it until the release prompt.");
        int number = 0;
        foreach (var group in Steps.GroupBy(step => step.Group))
        {
            Console.WriteLine($"\n{group.Key}");
            foreach (var step in group)
            {
                Console.WriteLine($"  {++number,2}. {step.Title} — {string.Join(" ", step.Cues.Select(cue => cue.Text))}");
            }
        }
    }

    public static async Task RunAsync(Action<string> setStep, Func<long> reportCount, CancellationToken token)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Open an interactive terminal to record. Press Enter at the start of each group.");

        Console.WriteLine("Allow about five minutes. Keep the 85HF within reach of the keyboard.");
        Console.WriteLine("Press and release A once to wake the gamepad, then leave the controls at rest.");
        Console.WriteLine("Press Enter at the start of each group, then follow the on-screen prompts.");
        Console.WriteLine("Press Esc or Ctrl+C to end the capture early.");
        int number = 0;
        foreach (var group in Steps.GroupBy(step => step.Group))
        {
            Console.WriteLine($"\n{group.Key}. Release all buttons and triggers and center both sticks.");
            Console.WriteLine("Press Enter when ready.");
            await WaitForEnterAsync(token);
            while (number == 0 && reportCount() == 0)
            {
                Console.WriteLine("No reports received yet. Press and release A on the 85HF, then press Enter. Press Esc to exit.");
                await WaitForEnterAsync(token);
            }
            foreach (var step in group)
            {
                Console.WriteLine($"\n[{++number}/{Steps.Count}] Get ready for {step.Title}");
                await WaitAsync(2000, token);
                setStep(step.Id);
                foreach (var cue in step.Cues)
                {
                    Console.WriteLine($"  {cue.Text}");
                    await WaitAsync(cue.Milliseconds, token);
                }
                setStep("idle");
            }
        }
    }

    private static async Task WaitForEnterAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryReadKey() == ConsoleKey.Enter)
                return;
            await Task.Delay(50, token);
        }
    }

    private static async Task WaitAsync(int milliseconds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            token.ThrowIfCancellationRequested();
            TryReadKey();
            await Task.Delay(50, token);
        }
    }

    private static ConsoleKey? TryReadKey()
    {
        if (!Console.KeyAvailable)
            return null;
        var key = Console.ReadKey(intercept: true).Key;
        if (key == ConsoleKey.Escape)
            throw new OperationCanceledException("Capture stopped with Esc.");
        return key;
    }

    private static IReadOnlyList<Step> CreateSteps()
    {
        const string neutral = "At rest";
        const string buttons = "Buttons";
        const string dpad = "D-pad";
        const string sticks = "Sticks";
        const string triggers = "Triggers";
        const string pairs = "Button combinations";
        var release = new Cue("Release and wait for the next prompt.", 1400);
        var steps = new List<Step>
        {
            new("neutral", neutral, "Initial neutral position", [new("Leave the gamepad at rest.", 3000)])
        };

        (string Id, string Title)[] buttonNames =
        [
            ("a", "A"), ("b", "B"), ("x", "X"), ("y", "Y"), ("lb", "LB"), ("rb", "RB"),
            ("select", "Select / Back"), ("start", "Start / Menu"), ("home", "Home / Guide"),
            ("l3", "L3 (press the centered left stick)"), ("r3", "R3 (press the centered right stick)"),
            ("l4", "L4"), ("r4", "R4"), ("pl", "PL"), ("pr", "PR")
        ];
        foreach (var (id, title) in buttonNames)
            steps.Add(new($"button_{id}", buttons, title, [new($"Press and hold {title}.", 1800), release]));

        (string Id, string Title)[] directions =
        [
            ("up", "up"), ("up_right", "up-right"), ("right", "right"), ("down_right", "down-right"),
            ("down", "down"), ("down_left", "down-left"), ("left", "left"), ("up_left", "up-left")
        ];
        foreach (var (id, title) in directions)
            steps.Add(new($"dpad_{id}", dpad, $"D-pad {title}", [new($"Press {title} on the D-pad and hold.", 1800), release]));

        foreach (var (stickId, name) in new[] { ("ls", "Left"), ("rs", "Right") })
        {
            foreach (var (id, direction) in new[] { ("left", "left"), ("right", "right"), ("up", "up"), ("down", "down") })
            {
                steps.Add(new($"{stickId}_{id}", sticks, $"{name} stick {direction}",
                    [new($"Move the stick all the way {direction} and hold.", 2200), release]));
            }
        }

        foreach (string trigger in new[] { "LT", "RT" })
        {
            steps.Add(new($"{trigger.ToLowerInvariant()}_sweep", triggers, $"{trigger} sweep",
            [
                new($"Slowly squeeze {trigger} about a quarter of the way.", 1500),
                new("Continue to halfway.", 1500),
                new("Continue to three quarters.", 1500),
                new("Squeeze fully and hold.", 1800),
                new("Slowly release to zero.", 2200),
                release
            ]));
        }
        steps.Add(new("both_full", triggers, "Both triggers fully pressed",
            [new("Fully press LT and hold.", 2000), new("Fully press RT as well. Hold both.", 2300), release]));
        steps.Add(new("lt_low_rt_high", triggers, "LT 25%, RT 75%",
            [new("Hold LT about a quarter of the way down.", 2000), new("Press RT about three quarters of the way. Hold both.", 3000), release]));
        steps.Add(new("lt_high_rt_low", triggers, "LT 75%, RT 25%",
            [new("Hold LT about three quarters of the way down.", 2000), new("Press RT about a quarter of the way. Hold both.", 3000), release]));

        steps.Add(new("pl_a", pairs, "PL and A",
            [new("Press and hold PL.", 1700), new("Press A as well. Hold both buttons.", 1900), new("Release PL while holding A.", 1700), release]));
        steps.Add(new("r4_rb", pairs, "R4 and RB",
            [new("Press and hold R4.", 1700), new("Press RB as well. Hold both buttons.", 1900), new("Release R4 while holding RB.", 1700), release]));
        steps.Add(new("neutral_end", pairs, "Final neutral position", [new("Leave the gamepad at rest.", 3000)]));
        return steps;
    }
}
