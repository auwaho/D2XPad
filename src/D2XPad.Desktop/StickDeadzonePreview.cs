using System.Windows;
using System.Windows.Media;
using D2XPad.Live;

namespace D2XPad.Desktop;

public sealed class StickDeadzonePreview : FrameworkElement
{
    private int inner;
    private int outer = StickDeadzones.FullTravel;
    private Brush? surface, innerFill, outerFill, accent;
    private Pen? grid, guide, threshold, innerThreshold, physicalMarker;
    private GeometryGroup? outerBand;
    private Size outerBandSize;
    private int outerBandThreshold;

    internal Point? PhysicalPosition { get; private set; }
    internal Point? FilteredPosition { get; private set; }

    internal void SetState(int inner, int outer, double? x, double? y)
    {
        Point? physical = x.HasValue && y.HasValue ? new Point(x.Value, y.Value) : null;
        if (this.inner == inner && this.outer == outer && PhysicalPosition == physical) return;
        this.inner = inner;
        this.outer = outer;
        PhysicalPosition = physical;
        FilteredPosition = null;
        if (physical is { } point)
        {
            var filtered = StickDeadzones.Filter(point.X, point.Y, inner, StickDeadzones.FullTravel - outer);
            FilteredPosition = new Point(filtered.X, filtered.Y);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 16) return;
        if (grid == null)
        {
            surface = (Brush)FindResource("FieldBackground");
            innerFill = (Brush)FindResource("ActiveTab");
            accent = (Brush)FindResource("Accent");
            outerFill = innerFill.CloneCurrentValue();
            outerFill.Opacity *= 0.8;
            grid = new((Brush)FindResource("SurfaceBorder"), 1);
            guide = new((Brush)FindResource("Stopped"), 1.15);
            threshold = new(accent, 1);
            var innerStroke = accent.CloneCurrentValue();
            innerStroke.Opacity *= 0.55;
            innerThreshold = new(innerStroke, 1);
            physicalMarker = new((Brush)FindResource("PrimaryText"), 1.5);
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = side / 2 - 8;
        var square = new Rect(center.X - side / 2 + 0.5, center.Y - side / 2 + 0.5, side - 1, side - 1);
        double outerRadius = radius * outer / StickDeadzones.FullTravel;
        context.DrawRoundedRectangle(surface, grid, square, 8, 8);
        if (outer < StickDeadzones.FullTravel)
            context.DrawGeometry(outerFill, null, OuterBand(center, radius, outerRadius));
        context.DrawLine(grid, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        context.DrawLine(grid, new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        context.DrawEllipse(null, guide, center, radius, radius);
        if (outer < StickDeadzones.FullTravel)
            context.DrawEllipse(null, threshold, center, outerRadius, outerRadius);
        if (inner > 0)
        {
            double innerRadius = radius * inner / StickDeadzones.FullTravel;
            context.DrawEllipse(innerFill, innerThreshold, center, innerRadius, innerRadius);
        }

        Point Project(Point point) => new(center.X + point.X * radius, center.Y - point.Y * radius);
        if (FilteredPosition is { } filtered) context.DrawEllipse(accent, null, Project(filtered), 4, 4);
        if (PhysicalPosition is { } raw) context.DrawEllipse(null, physicalMarker, Project(raw), 5.5, 5.5);
    }

    private Geometry OuterBand(Point center, double radius, double outerRadius)
    {
        if (outerBand != null && outerBandSize == RenderSize && outerBandThreshold == outer) return outerBand;

        var band = new GeometryGroup { FillRule = FillRule.EvenOdd };
        band.Children.Add(new EllipseGeometry(center, radius, radius));
        band.Children.Add(new EllipseGeometry(center, outerRadius, outerRadius));
        band.Freeze();
        outerBand = band;
        outerBandSize = RenderSize;
        outerBandThreshold = outer;
        return band;
    }
}
