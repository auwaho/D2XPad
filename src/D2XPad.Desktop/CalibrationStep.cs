using D2XPad.Live;

namespace D2XPad.Desktop;

internal enum CaptureKind { Axis, Trigger, Dpad, Button, Extra }

internal sealed class CalibrationStep(string source, string label, CaptureKind kind, params string[] poses)
{
    public string Source { get; } = source;
    public string Label { get; } = label;
    public CaptureKind Kind { get; } = kind;
    public string[] Poses { get; } = poses;
    public byte[][]? Samples { get; set; }
    public bool Skipped { get; set; }
    public override string ToString() => Samples != null ? $"{Label} ✓" : Skipped ? $"{Label} (skipped)" : Label;

    public void Bind(ControllerCalibration calibration, byte[][] reports)
    {
        switch (Kind)
        {
            case CaptureKind.Axis:
                calibration.BindAxis(Source, reports[0], reports[2], reports[1], reports[3]); break;
            case CaptureKind.Trigger:
                calibration.BindTrigger(Source, reports[0], reports[2], reports[1], reports[3]); break;
            case CaptureKind.Dpad:
                calibration.BindDpad(reports[..4], reports[4..]); break;
            case CaptureKind.Button:
                calibration.BindButton(Source, reports[0], reports[1]); break;
            case CaptureKind.Extra:
                calibration.BindExtra(Source, reports[0], reports[1]); break;
        }
    }

    public static CalibrationStep Extra(string name) =>
        new(name, name, CaptureKind.Extra, $"Hold {name}.", $"Hold {name} again.");

    public static List<CalibrationStep> Standard()
    {
        static CalibrationStep Axis(string source, string stick, string negative, string positive) =>
            new(source, $"{stick} · {negative}/{positive}", CaptureKind.Axis, $"Hold the {stick.ToLowerInvariant()} fully {negative}.",
                $"Hold it fully {positive}.", $"Hold it fully {negative} again.", $"Hold it fully {positive} again.");
        static CalibrationStep Trigger(string source) => new(source, source, CaptureKind.Trigger,
            $"Hold {source} halfway. For a digital trigger, press it fully.", $"Hold {source} fully down.",
            $"Hold {source} halfway again. For a digital trigger, press it fully.", $"Hold {source} fully down again.");
        List<CalibrationStep> steps =
        [
            Axis("LX", "Left stick", "left", "right"), Axis("LY", "Left stick", "up", "down"),
            Axis("RX", "Right stick", "left", "right"), Axis("RY", "Right stick", "up", "down"),
            Trigger("LT"), Trigger("RT"), new("DPad", "D-pad", CaptureKind.Dpad,
                "Hold D-pad up.", "Hold D-pad right.", "Hold D-pad down.", "Hold D-pad left.",
                "Hold D-pad up again.", "Hold D-pad right again.", "Hold D-pad down again.", "Hold D-pad left again.")
        ];
        foreach (var (id, name) in new[] { ("A","A"), ("B","B"), ("X","X"), ("Y","Y"),
            ("LB","LB"), ("RB","RB"), ("LS","Left stick click"), ("RS","Right stick click"),
            ("View","View / Select"), ("Menu","Menu / Start"), ("Guide","Guide / Home") })
            steps.Add(new(id, name, CaptureKind.Button, $"Hold {name}.", $"Hold {name} again."));
        return steps;
    }
}
