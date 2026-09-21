using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EitHost.App.Views;

/// <summary>Scales plot coordinates, while keeping stroke width in screen pixels.</summary>
public sealed class PlotTrace : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(PlotTrace), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(PlotTrace), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(PlotTrace), new FrameworkPropertyMetadata(2.2, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsDashedProperty = DependencyProperty.Register(
        nameof(IsDashed), typeof(bool), typeof(PlotTrace), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool IsDashed { get => (bool)GetValue(IsDashedProperty); set => SetValue(IsDashedProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is null || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var geometry = new GeometryGroup { Transform = new ScaleTransform(RenderSize.Width / 520.0, RenderSize.Height / 220.0) };
        geometry.Children.Add(Data);
        var pen = new Pen(Stroke, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            DashStyle = IsDashed ? DashStyles.Dash : DashStyles.Solid
        };
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }
}

/// <summary>Axis labels are native-sized text, positioned against the same 220-unit Y scale.</summary>
public sealed class PlotTickPanel : Panel
{
    public static readonly DependencyProperty CoordinateProperty = DependencyProperty.RegisterAttached(
        "Coordinate", typeof(double), typeof(PlotTickPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static void SetCoordinate(DependencyObject item, double value) => item.SetValue(CoordinateProperty, value);
    public static double GetCoordinate(DependencyObject item) => (double)item.GetValue(CoordinateProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 80, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var occupied = new List<double>();
        // Keep the zero line first; hide overlapping labels instead of shrinking their font.
        var children = InternalChildren.Cast<UIElement>().OrderBy(child =>
            child is FrameworkElement { DataContext: ViewModels.RealtimeDemodulationAxisTick { IsZero: true } } ? 0 : 1);
        foreach (var child in children)
        {
            var center = GetCoordinate(child) * finalSize.Height / 220.0;
            var height = child.DesiredSize.Height;
            var show = occupied.All(previous => Math.Abs(previous - center) >= height + 2);
            child.Opacity = show ? 1 : 0;
            if (show) occupied.Add(center);
            child.Arrange(new Rect(0, center - height / 2, finalSize.Width, height));
        }
        return finalSize;
    }
}
