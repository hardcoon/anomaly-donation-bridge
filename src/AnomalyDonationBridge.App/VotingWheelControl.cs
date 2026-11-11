using System.Windows;
using System.Windows.Media;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public sealed class VotingWheelControl : FrameworkElement
{
    public static readonly DependencyProperty RoundProperty = DependencyProperty.Register(nameof(Round),
        typeof(VotingRound), typeof(VotingWheelControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle),
        typeof(double), typeof(VotingWheelControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public VotingRound? Round { get => (VotingRound?)GetValue(RoundProperty); set => SetValue(RoundProperty, value); }
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public static Brush Color(int index) => new SolidColorBrush(new[] {
        System.Windows.Media.Color.FromRgb(190, 113, 103), System.Windows.Media.Color.FromRgb(145, 172, 117),
        System.Windows.Media.Color.FromRgb(208, 187, 119), System.Windows.Media.Color.FromRgb(134, 175, 192),
        System.Windows.Media.Color.FromRgb(171, 145, 190), System.Windows.Media.Color.FromRgb(204, 155, 109)
    }[index % 6]);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size * .43; var inner = radius * .74;
        var options = Round?.Options.Where(option => option.Enabled && option.Preset is not null).ToList() ?? [];
        var total = options.Sum(option => option.Amount);
        if (total > 0)
        {
            drawing.PushTransform(new RotateTransform(Angle, center.X, center.Y));
            // An explicit annulus clip keeps the centre transparent for every
            // sector, including a full circle or a very small slice.
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(center, radius, radius));
            ring.Children.Add(new EllipseGeometry(center, inner, inner));
            ring.Freeze(); drawing.PushClip(ring);
            var boundaries = new List<double>();
            var start = 0d;
            for (var index = 0; index < options.Count; index++)
            {
                if (options[index].Amount <= 0) continue;
                boundaries.Add(start);
                var end = start + (double)(options[index].Amount / total) * 360;
                // Two arcs also handle a single paid choice / a full 360° ring.
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(PointAt(center, radius, start), true, true);
                    var middle = (start + end) / 2;
                    context.ArcTo(PointAt(center, radius, middle), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                    context.ArcTo(PointAt(center, radius, end), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                    context.LineTo(PointAt(center, inner, end), true, false);
                    context.ArcTo(PointAt(center, inner, middle), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
                    context.ArcTo(PointAt(center, inner, start), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                geometry.Freeze(); drawing.DrawGeometry(Color(index), null, geometry); start = end;
            }
            if (boundaries.Count > 1)
            {
                var separator = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 28, 30, 28)), 2.5);
                separator.Freeze();
                foreach (var boundary in boundaries)
                    drawing.DrawLine(separator, PointAt(center, inner, boundary), PointAt(center, radius, boundary));
            }
            drawing.Pop();
            drawing.Pop();
        }
        else drawing.DrawEllipse(null, new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 160, 165, 155)), radius - inner), center, (radius + inner) / 2, (radius + inner) / 2);
        var pointer = new StreamGeometry();
        using (var context = pointer.Open())
        {
            context.BeginFigure(new Point(center.X - 10, center.Y - radius - 12), true, true);
            context.LineTo(new Point(center.X + 10, center.Y - radius - 12), true, false);
            context.LineTo(new Point(center.X, center.Y - radius + 19), true, false);
        }
        pointer.Freeze(); drawing.DrawGeometry(Brushes.White, null, pointer);
    }

    private static Point PointAt(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + Math.Sin(radians) * radius, center.Y - Math.Cos(radians) * radius);
    }
}
