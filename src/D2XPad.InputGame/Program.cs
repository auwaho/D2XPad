using System.Drawing.Drawing2D;
using System.Text.Json;
using D2XPad.SeriesProbe;

namespace D2XPad.InputGame;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out int slot) || slot is < 0 or > 3 ||
            args.Length == 2 && args[1] != "--probe")
        {
            Console.Error.WriteLine("Usage: D2XPad.InputGame <XInput slot 0..3> [--probe]");
            return 2;
        }
        ApplicationConfiguration.Initialize();
        using var window = new BeaconGame(slot, args.Length == 2);
        Application.Run(window);
        return 0;
    }
}

// Dock the ship at the beacon matching its color, then fire at the beacon to score.
// Every game frame consumes XInputGetState directly, including when the browser has focus.
internal sealed class BeaconGame : Form
{
    private readonly int slot;
    private readonly bool probe;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly Color[] colors = [Color.Aquamarine, Color.Gold, Color.LightCoral];
    private int score, color, frame;
    private float shipX = 0.5f, shipY = 0.5f, aimX, aimY;
    private ushort previous;
    private GameActions actions;
    private XInputReading? input;
    private readonly List<object> sample = [];
    private bool sampling;

    public BeaconGame(int slot, bool probe)
    {
        this.slot = slot;
        this.probe = probe;
        if (probe)
        {
            // The browser presents guided verification; keep the independent game loop in the background.
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
        }
        Text = "D2XPad · Beacon Run";
        ClientSize = new Size(550, 350);
        MinimumSize = new Size(490, 340);
        BackColor = Color.FromArgb(19, 29, 42);
        ForeColor = Color.WhiteSmoke;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(12, 12);
        timer.Tick += (_, _) => Advance();
        Shown += (_, _) =>
        {
            timer.Start();
            if (probe) _ = ReadCommandsAsync();
        };
    }

    protected override bool ShowWithoutActivation => probe;

    private async Task ReadCommandsAsync()
    {
        // The pipe only accepts "sample" or EOF. It never carries expected controller values.
        using var reader = new StreamReader(Console.OpenStandardInput());
        while (!IsDisposed)
        {
            // Redirected standard handles may perform synchronous reads on Windows.
            // Keep a waiting command off the UI thread that advances the game.
            string? command = await Task.Run(reader.ReadLine);
            if (command != "sample") { Close(); return; }
            sample.Clear();
            sampling = true;
        }
    }

    private void Advance()
    {
        frame++;
        input = XInputReader.Read(slot);
        if (input is { } p)
        {
            float speed = (0.005f + p.Rt / 255f * 0.011f) / (1 + p.Lt / 255f * 5);
            shipX = Math.Clamp(shipX + p.Lx / 32768f * speed, 0.02f, 0.98f);
            shipY = Math.Clamp(shipY - p.Ly / 32768f * speed, 0.02f, 0.98f);
            aimX = p.Rx / 32768f * 0.17f;
            aimY = -p.Ry / 32768f * 0.17f;
            int pressed = p.Buttons & ~previous;
            if ((pressed & 0x0100) != 0)
            {
                color = (color + 2) % 3;
                actions = actions with { PreviousColor = actions.PreviousColor + 1 };
            }
            if ((pressed & 0x0200) != 0)
            {
                color = (color + 1) % 3;
                actions = actions with { NextColor = actions.NextColor + 1 };
            }
            if ((pressed & 0x2000) != 0)
            {
                shipX = shipY = 0.5f;
                actions = actions with { Recall = actions.Recall + 1 };
            }
            if ((pressed & 0x1000) != 0)
            {
                actions = actions with { Fire = actions.Fire + 1 };
                if (color == score % 3 && MathF.Pow(shipX + aimX - TargetX, 2) + MathF.Pow(shipY + aimY - TargetY, 2) < 0.01f)
                    score++;
            }
            previous = p.Buttons;
        }
        else previous = 0;
        Invalidate();
        if (!sampling) return;
        sample.Add(new { Frame = frame, Reading = input, Score = score, Actions = actions });
        if (sample.Count == 3)
        {
            Console.WriteLine(JsonSerializer.Serialize(sample));
            Console.Out.Flush();
            sampling = false;
        }
    }

    private float TargetX => ((score * 43 + 23) % 83 + 8) / 100f;
    private float TargetY => ((score * 21 + 67) % 83 + 8) / 100f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var white = new SolidBrush(ForeColor);
        var area = new RectangleF(16, 58, ClientSize.Width - 32, ClientSize.Height - 145);
        g.DrawString($"Beacon Run     Hits {score} / {actions.Fire} shots     XInput slot {slot}", Font, white, 16, 12);
        g.DrawString("Aim at the colored beacon, match its color and fire.", Font, white, 16, 33);
        g.DrawRectangle(Pens.SlateGray, area.X, area.Y, area.Width, area.Height);
        float X(float x) => area.Left + x * area.Width;
        float Y(float y) => area.Top + y * area.Height;
        using var beacon = new Pen(colors[score % 3], 3);
        g.DrawEllipse(beacon, X(TargetX) - 10, Y(TargetY) - 10, 20, 20);
        using var ship = new SolidBrush(colors[color]);
        g.FillEllipse(ship, X(shipX) - 5, Y(shipY) - 5, 10, 10);
        float x = X(shipX + aimX), y = Y(shipY + aimY);
        g.DrawLine(Pens.White, x - 5, y, x + 5, y);
        g.DrawLine(Pens.White, x, y - 5, x, y + 5);
        g.DrawString("LS move   ·   RS aim   ·   LT brake   ·   RT boost", Font, white, 16, area.Bottom + 8);
        g.DrawString("LB / RB select color   ·   A fire   ·   B return to center", Font, white, 16, area.Bottom + 29);
        string details = input is { } p ? $"LT {p.Lt}   RT {p.Rt}   LS {p.Lx},{p.Ly}   RS {p.Rx},{p.Ry}   Buttons 0x{p.Buttons:X4}" : "Controller disconnected";
        g.DrawString(details, Font, white, 16, area.Bottom + 50);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
