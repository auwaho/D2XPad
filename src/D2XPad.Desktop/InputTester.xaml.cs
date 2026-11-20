using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using D2XPad.Live;
using Path = System.Windows.Shapes.Path;

namespace D2XPad.Desktop;

public partial class InputTester : UserControl
{
    private static readonly Geometry DPadArrow = Geometry.Parse("M 2.75,5.5 L 6,2.25 9.25,5.5 M 6,2.25 V 9.75");
    private readonly Brush physicalAccent, outputAccent, keyFill, keyStroke, keyText, keyInk, unavailable;

    private readonly List<(string Id, ulong Mask, Border Key)> buttons = [];
    private readonly List<(string Id, ulong Mask, Border Key)> rear = [];
    private HashSet<string>? supported;
    private PadReading? physical, output;
    private string? physicalUnavailable, outputUnavailable;
    private bool ready;

    public InputTester()
    {
        InitializeComponent();
        physicalAccent = (Brush)FindResource("Accent");
        outputAccent = (Brush)FindResource("Secondary");
        keyFill = (Brush)FindResource("FieldBackground");
        keyStroke = (Brush)FindResource("SurfaceBorder");
        keyText = (Brush)FindResource("PrimaryText");
        keyInk = (Brush)FindResource("StartInk");
        unavailable = (Brush)FindResource("Stopped");
        LeftPlot.SetPalette(keyFill, keyStroke, unavailable);
        RightPlot.SetPalette(keyFill, keyStroke, unavailable);
        AddKeys(ButtonsPanel, buttons,
        [
            ("A", "A", "A", 0x1000), ("B", "B", "B", 0x2000), ("X", "X", "X", 0x4000), ("Y", "Y", "Y", 0x8000),
            ("LB", "LB", "Left bumper", 0x0100), ("RB", "RB", "Right bumper", 0x0200),
            ("L3", "L3", "Left stick click", 0x0040), ("R3", "R3", "Right stick click", 0x0080),
            ("DPadUp", "", "D-pad up", 1), ("DPadDown", "", "D-pad down", 2),
            ("DPadLeft", "", "D-pad left", 4), ("DPadRight", "", "D-pad right", 8),
            ("Back", "View", "View / Select", 0x0020), ("Start", "Menu", "Menu / Start", 0x0010), ("Guide", "Guide", "Guide / Home", 0x0400)
        ]);
        ready = true;
        Render();
    }

    internal void SetProfile(DeviceLayout? layout, PadBindings? bindings)
    {
        supported = bindings?.Sources.Select(source => source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RearPanel.Children.Clear();
        rear.Clear();
        foreach (var source in layout?.ExtraSources ?? [])
        {
            var actions = bindings?.GetActions(PadBindings.ExtraId(source.Id)) ?? [];
            string description = MappingText.Describe(actions);
            string caption = actions.Count == 0 ? source.Name : $"{source.Name} → {description}";
            AddKeys(RearPanel, rear, [(source.Id, caption, source.Name, source.Mask)]);
            var key = rear[^1].Key;
            key.MaxWidth = 225;
            key.ToolTip = $"{source.Name} · Xbox output: {description}";
            AutomationProperties.SetName(key.Child, caption);
        }
        ExtraSection.Visibility = rear.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Render();
    }

    internal void ShowXboxOutput() => OutputSwitch.IsChecked = true;

    internal void ShowPhysicalOnly()
    {
        PhysicalSwitch.IsChecked = true;
        SourceSwitches.Visibility = Visibility.Collapsed;
    }

    internal void Update(PadReading? physical, PadReading? output, string? physicalUnavailable = null,
        string? outputUnavailable = null)
    {
        if (this.physical == physical && this.output == output && this.physicalUnavailable == physicalUnavailable &&
            this.outputUnavailable == outputUnavailable) return;
        this.physical = physical;
        this.output = output;
        this.physicalUnavailable = physicalUnavailable;
        this.outputUnavailable = outputUnavailable;
        Render();
    }

    private void SourceChanged(object sender, RoutedEventArgs e)
    {
        if (ready) Render();
    }

    private void Render()
    {
        bool showPhysical = PhysicalSwitch.IsChecked == true;
        var sample = showPhysical ? physical : output;
        var color = showPhysical ? physicalAccent : outputAccent;
        SourceStatus.Text = showPhysical
            ? physical != null ? "HID input · before profile remapping" : physicalUnavailable ?? "Waiting for a controller report."
            : output != null ? "XInput · virtual Xbox Series X|S" : outputUnavailable ?? "Waiting for the virtual Xbox controller.";

        bool Supports(string id) => !showPhysical || supported == null || supported.Contains(id);
        double? lx = Supports("LeftStickLeft") || Supports("LeftStickRight") ? sample?.Lx : null;
        double? ly = Supports("LeftStickUp") || Supports("LeftStickDown") ? sample?.Ly : null;
        double? rx = Supports("RightStickLeft") || Supports("RightStickRight") ? sample?.Rx : null;
        double? ry = Supports("RightStickUp") || Supports("RightStickDown") ? sample?.Ry : null;
        LeftPlot.SetPosition(lx, ly, color);
        RightPlot.SetPosition(rx, ry, color);
        LeftValues.Text = Coordinates(lx, ly);
        RightValues.Text = Coordinates(rx, ry);
        SetTrigger(LeftTrigger, LtPercent, LtValue, Supports("LT") ? sample?.Lt : null, color);
        SetTrigger(RightTrigger, RtPercent, RtValue, Supports("RT") ? sample?.Rt : null, color);
        foreach (var (id, mask, key) in buttons)
        {
            // Guide is available on the physical pad. Windows' standard XInputGetState API
            // does not promise a Guide bit; expose only controls reported by that interface.
            key.Visibility = Supports(id) && (showPhysical || mask != 0x0400) ? Visibility.Visible : Visibility.Collapsed;
            SetKey(key, sample != null, sample is { } current && (current.Buttons & mask) != 0, color);
        }
        foreach (var (_, mask, key) in rear)
            SetKey(key, physical != null, physical is { } source && (source.Extras & mask) != 0, physicalAccent);
    }

    private static string Coordinates(double? x, double? y)
    {
        static string Format(double? value) => value?.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) ?? "—";
        return $"X {Format(x)}  Y {Format(y)}";
    }

    private static void SetTrigger(ProgressBar bar, TextBlock percent, TextBlock value, byte? reading, Brush color)
    {
        bar.Foreground = color;
        bar.Value = reading ?? 0;
        percent.Text = reading.HasValue ? $"{Math.Round(reading.Value * 100.0 / 255)}%" : "—";
        value.Text = reading.HasValue ? $"{reading.Value} / 255" : "— / 255";
        AutomationProperties.SetItemStatus(bar, value.Text);
    }

    private static void AddKeys(Panel panel, List<(string Id, ulong Mask, Border Key)> keys,
        (string Id, string Label, string Name, ulong Mask)[] specs)
    {
        foreach (var (id, label, name, mask) in specs)
        {
            var caption = new TextBlock
            {
                Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            int? angle = id switch
            {
                "DPadUp" => 0,
                "DPadRight" => 90,
                "DPadDown" => 180,
                "DPadLeft" => 270,
                _ => null
            };
            if (angle is int rotation)
            {
                var arrow = new Path
                {
                    Data = DPadArrow, Width = 12, Height = 12, StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    RenderTransform = new RotateTransform(rotation), RenderTransformOrigin = new Point(0.5, 0.5)
                };
                arrow.SetBinding(Path.StrokeProperty, new Binding(nameof(TextBlock.Foreground)) { Source = caption });
                caption.Inlines.Add(new InlineUIContainer(arrow) { BaselineAlignment = BaselineAlignment.Center });
            }
            var key = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                MinWidth = 31, Height = 27, Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(7, 0, 7, 0), Child = caption, ToolTip = name
            };
            AutomationProperties.SetName(caption, name);
            AutomationProperties.SetAutomationId(caption, id);
            keys.Add((id, mask, key));
            panel.Children.Add(key);
        }
    }

    private void SetKey(Border key, bool available, bool active, Brush accent)
    {
        key.Background = active ? accent : keyFill;
        key.BorderBrush = active ? accent : keyStroke;
        var caption = (TextBlock)key.Child;
        caption.Foreground = active ? keyInk : available ? keyText : unavailable;
        AutomationProperties.SetItemStatus(caption, available ? active ? "Pressed" : "Released" : "No reading");
    }
}

public sealed class StickPlot : FrameworkElement
{
    private Brush? field;
    private Pen? outline, gridLine;
    private double? x, y;
    private Brush color = Brushes.Transparent;

    internal void SetPalette(Brush background, Brush grid, Brush edge)
    {
        field = background;
        outline = new Pen(edge, 1);
        gridLine = new Pen(grid, 1);
        InvalidateVisual();
    }

    internal void SetPosition(double? nextX, double? nextY, Brush accent)
    {
        if (x == nextX && y == nextY && color == accent) return;
        x = nextX;
        y = nextY;
        color = accent;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Min(center.X, center.Y) - 7;
        if (radius <= 0) return;
        // Each axis can reach its endpoint independently, including both endpoints at a diagonal.
        drawing.DrawRoundedRectangle(field, gridLine,
            new Rect(center.X - radius, center.Y - radius, 2 * radius, 2 * radius), 4, 4);
        drawing.DrawEllipse(null, outline, center, radius, radius);
        drawing.DrawEllipse(null, gridLine, center, radius * 0.5, radius * 0.5);
        drawing.DrawLine(gridLine, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        drawing.DrawLine(gridLine, new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        if (x == null && y == null) return;
        var marker = new Point(center.X + Math.Clamp(x ?? 0, -1, 1) * radius,
            center.Y - Math.Clamp(y ?? 0, -1, 1) * radius);
        drawing.DrawLine(outline, center, marker);
        drawing.DrawEllipse(color, null, marker, 4.5, 4.5);
    }
}
