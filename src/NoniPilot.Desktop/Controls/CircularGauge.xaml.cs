using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoniPilot.Desktop.Controls;

/// <summary>
/// A hand-built ring gauge (no gauge/charting NuGet exists anywhere in this solution - confirmed
/// by repo-wide search). Track and value are two overlaid arcs built from ArcSegment geometry via
/// simple polar-to-cartesian math, not a third-party control.
/// </summary>
public partial class CircularGauge : UserControl
{
    public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
        nameof(Percentage), typeof(double), typeof(CircularGauge), new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(Brush), typeof(CircularGauge), new PropertyMetadata(Brushes.Cyan, OnVisualChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CircularGauge), new PropertyMetadata(string.Empty, OnLabelChanged));

    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public Brush AccentColor
    {
        get => (Brush)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public CircularGauge()
    {
        InitializeComponent();
        Loaded += (_, _) => Redraw();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CircularGauge)d).Redraw();

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (CircularGauge)d;
        if (gauge.LabelText is not null)
        {
            gauge.LabelText.Text = (string)e.NewValue;
        }
    }

    private void Redraw()
    {
        if (TrackPath is null || ValuePath is null || PercentText is null)
        {
            return;
        }

        const double cx = 50, cy = 50, r = 40;

        // Not a literal 360 - two coincident start/end points at exactly 0/360 degenerate the
        // arc segment into nothing rendering at all, so both the fixed track ring and a "100%"
        // value ring stop just short of a full turn.
        TrackPath.Data = BuildArc(cx, cy, r, 0, 359.999);

        var clamped = Math.Clamp(Percentage, 0, 100);
        var valueAngle = Math.Min(clamped * 3.6, 359.999);
        ValuePath.Data = clamped <= 0 ? null : BuildArc(cx, cy, r, 0, valueAngle);
        ValuePath.Stroke = AccentColor;

        PercentText.Text = $"{clamped:F0}%";
    }

    private static Geometry BuildArc(double cx, double cy, double r, double startAngleDeg, double endAngleDeg)
    {
        var start = PolarToCartesian(cx, cy, r, startAngleDeg);
        var end = PolarToCartesian(cx, cy, r, endAngleDeg);
        var isLargeArc = endAngleDeg - startAngleDeg > 180;

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, isLargeArc, SweepDirection.Clockwise, isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>0 degrees is 12 o'clock, sweeping clockwise - matches how a percentage ring reads.</summary>
    private static Point PolarToCartesian(double cx, double cy, double r, double angleDeg)
    {
        var rad = Math.PI / 180.0 * (angleDeg - 90);
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}
