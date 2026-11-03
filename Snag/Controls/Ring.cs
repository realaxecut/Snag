using System.Windows;
using System.Windows.Media;

namespace Snag.Controls;

/// <summary>A circular progress ring (used for the shutdown countdown).</summary>
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));


    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;

        // Theme colours, looked up on every draw (the countdown redraws each second).
        var track = TryFindResource("TrackBrush") as Brush ?? Brushes.Gray;
        var fill = TryFindResource("BrandGradient") as Brush ?? Brushes.MediumPurple;

        var radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(track, Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0) return;

        var pen = new Pen(fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 0.9999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = value * 360;
        var radians = (angle - 90) * Math.PI / 180;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
