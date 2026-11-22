using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using D2XPad.Live;

namespace D2XPad.Desktop;

public partial class DeadzoneRangeSlider : UserControl
{
    private const int Scale = StickDeadzones.FullTravel;
    private const double HandleRadius = 7;
    private Slider? dragging;
    private double dragOffset;
    private bool updating = true;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string),
        typeof(DeadzoneRangeSlider), new PropertyMetadata("Stick"));
    private static readonly DependencyPropertyKey InnerPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Inner), typeof(int),
        typeof(DeadzoneRangeSlider), new PropertyMetadata(0));
    private static readonly DependencyPropertyKey OuterPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Outer), typeof(int),
        typeof(DeadzoneRangeSlider), new PropertyMetadata(Scale));
    public static readonly DependencyProperty InnerProperty = InnerPropertyKey.DependencyProperty;
    public static readonly DependencyProperty OuterProperty = OuterPropertyKey.DependencyProperty;

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public int Inner => (int)GetValue(InnerProperty);
    public int Outer => (int)GetValue(OuterProperty);
    public event EventHandler? RangeChanged;

    public DeadzoneRangeSlider()
    {
        InitializeComponent();
        SetRange(0, Scale);
    }

    public void SetRange(int inner, int outer)
    {
        inner = Math.Clamp(inner, 0, StickDeadzones.Maximum);
        outer = Math.Clamp(outer, inner + 1, Scale);
        bool changed = inner != Inner || outer != Outer;
        updating = true;
        try
        {
            InnerHandle.Maximum = outer - 1;
            OuterHandle.Minimum = inner + 1;
            InnerHandle.Value = inner;
            OuterHandle.Value = outer;
            SetValue(InnerPropertyKey, inner);
            SetValue(OuterPropertyKey, outer);
        }
        finally { updating = false; }
        UpdateRail();
        if (changed) RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating || InnerHandle == null || OuterHandle == null) return;
        SetRange(Convert.ToInt32(InnerHandle.Value), Convert.ToInt32(OuterHandle.Value));
    }

    private void RailSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRail();

    private void HandleFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        Panel.SetZIndex(InnerHandle, ReferenceEquals(sender, InnerHandle) ? 1 : 0);
        Panel.SetZIndex(OuterHandle, ReferenceEquals(sender, OuterHandle) ? 1 : 0);
    }

    protected override void OnAccessKey(AccessKeyEventArgs e) => InnerHandle.Focus();

    protected override AutomationPeer OnCreateAutomationPeer() => new RangeAutomationPeer(this);

    private void UpdateRail()
    {
        Canvas.SetLeft(Selection, Position(Inner));
        Selection.Width = Position(Outer - Inner);
    }

    private double Position(double value) => Rail.ActualWidth * value / Scale;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!IsEnabled || Rail.ActualWidth == 0) return;
        double x = e.GetPosition(Rail).X;
        double innerDistance = Math.Abs(x - Position(Inner));
        double outerDistance = Math.Abs(x - Position(Outer));
        dragging = innerDistance < outerDistance || innerDistance == outerDistance && InnerHandle.IsKeyboardFocused
            ? InnerHandle : OuterHandle;
        dragging.Focus();
        if (!CaptureMouse())
        {
            dragging = null;
            return;
        }
        dragOffset = Math.Min(innerDistance, outerDistance) <= HandleRadius ? x - Position(dragging.Value) : 0;
        MoveHandle(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging == null || !IsMouseCaptured) return;
        MoveHandle(e.GetPosition(Rail).X);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging == null) return;
        MoveHandle(e.GetPosition(Rail).X);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragging = null;
    }

    private void MoveHandle(double x)
    {
        if (dragging == null || Rail.ActualWidth == 0) return;
        double value = Math.Round((x - dragOffset) * Scale / Rail.ActualWidth, MidpointRounding.AwayFromZero);
        dragging.Value = Math.Clamp(value, dragging.Minimum, dragging.Maximum);
    }

    private sealed class RangeAutomationPeer(DeadzoneRangeSlider slider) : FrameworkElementAutomationPeer(slider)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(DeadzoneRangeSlider);
    }
}
