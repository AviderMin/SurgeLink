using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SurgeLink.App.Styling;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Components;

/// <summary>Props for <see cref="Gauge"/>.</summary>
/// <param name="Fraction">Value shown on the ring, 0..1.</param>
/// <param name="ValueText">Large centred text, e.g. "42.3%".</param>
/// <param name="Caption">Small caption underneath the value.</param>
/// <param name="Size">Outer diameter in device independent pixels.</param>
internal sealed record GaugeProps(double Fraction, string? ValueText = null, string? Caption = null, double Size = 120);

/// <summary>
/// A 270 degree radial gauge built from two WinUI shapes: an <see cref="Ellipse"/>
/// for the full track and a <see cref="Path"/> arc for the current value. No
/// XAML, no images, and the colours come from <see cref="ChartPalette"/> so the
/// gauge follows light, dark and High Contrast.
/// </summary>
internal sealed class Gauge : Component<GaugeProps>
{
    /// <summary>Arc starts at the lower left (135 degrees) and sweeps clockwise.</summary>
    private const double StartAngleDegrees = 135;

    private const double SweepDegrees = 270;

    public override Element Render()
    {
        _ = UseColorScheme(); // repaint when the theme changes

        var size = Math.Max(48, Props.Size);
        var fraction = Math.Clamp(Props.Fraction, 0d, 1d);
        var thickness = Math.Max(4, Math.Round(size * 0.09));
        var radius = (size - thickness) / 2;
        var center = size / 2;

        var shapes = new List<Element>
        {
            Ellipse()
                .Set(ellipse =>
                {
                    ellipse.Width = size;
                    ellipse.Height = size;
                    ellipse.Stroke = ChartPalette.Track;
                    ellipse.StrokeThickness = thickness;
                })
                .Canvas(0, 0),
        };

        if (fraction > 0.002)
        {
            shapes.Add(
                Path2D()
                    .Set(path =>
                    {
                        path.Data = BuildArc(center, radius, fraction);
                        path.Stroke = ChartPalette.Severity(fraction);
                        path.StrokeThickness = thickness;
                        path.StrokeStartLineCap = PenLineCap.Round;
                        path.StrokeEndLineCap = PenLineCap.Round;
                    })
                    .Canvas(0, 0));
        }

        var ring = Canvas(shapes.ToArray()).Size(size, size);

        var labels = VStack(
                AppTheme.SpacingXs,
                Props.ValueText is null ? null : SubHeading(Props.ValueText),
                Props.Caption is null ? null : Caption(Props.Caption).Foreground(Theme.SecondaryText))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        return Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Star()],
                ring,
                labels)
            .AutomationName($"Gauge {Props.ValueText ?? DataFormat.Percent(fraction)}");
    }

    /// <summary>Builds a clockwise arc from the start angle covering <paramref name="fraction"/> of the sweep.</summary>
    private static PathGeometry BuildArc(double center, double radius, double fraction)
    {
        var sweep = SweepDegrees * fraction;
        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(center, radius, StartAngleDegrees),
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnCircle(center, radius, StartAngleDegrees + sweep),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweep > 180,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Converts an angle in degrees (0 = east, clockwise) to a point on the circle.</summary>
    private static Point PointOnCircle(double center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(center + (radius * Math.Cos(radians)), center + (radius * Math.Sin(radians)));
    }
}
