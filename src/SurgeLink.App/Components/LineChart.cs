using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SurgeLink.App.Localization;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Components;

/// <summary>One line of a <see cref="LineChart"/>; values are ordered oldest to newest.</summary>
internal sealed record ChartSeries(string Name, IReadOnlyList<double> Values);

/// <summary>Props for <see cref="LineChart"/>.</summary>
/// <param name="Series">Series to draw, one stroke each.</param>
/// <param name="Height">Plot height in device independent pixels.</param>
/// <param name="WindowSize">Number of samples the x axis represents; fewer samples are right aligned.</param>
/// <param name="MinValue">Fixed lower bound; when null <c>0</c> is used.</param>
/// <param name="MaxValue">Fixed upper bound; when null the largest sample (or 100) is used.</param>
/// <param name="ShowGrid">Draws three horizontal gridlines.</param>
internal sealed record LineChartProps(
    IReadOnlyList<ChartSeries> Series,
    double Height = 96,
    int WindowSize = 60,
    double? MinValue = null,
    double? MaxValue = null,
    bool ShowGrid = true);

/// <summary>
/// A multi series rolling line chart built only from WinUI shapes: one
/// <see cref="Path"/> per series inside a <see cref="Canvas"/>. The control
/// measures itself (the parent decides the width) and repaints on resize and on
/// theme change, which keeps it usable in responsive dashboard cards.
/// </summary>
/// <remarks>
/// The chart draws values in a fixed number of slots (<see cref="LineChartProps.WindowSize"/>);
/// callers keep the rolling window and pass the last N samples, so the line
/// scrolls from left to right without allocating history here.
/// </remarks>
internal sealed class LineChart : Component<LineChartProps>
{
    private const double PlotPadding = 2;

    public override Element Render()
    {
        _ = UseColorScheme(); // repaint on light/dark/high contrast change

        var (measuredWidth, setMeasuredWidth) = UseState(160d);
        var height = Math.Max(24, Props.Height);
        var width = Math.Max(32, measuredWidth);
        var slots = Math.Max(2, Props.WindowSize);

        var min = Props.MinValue ?? 0;
        var max = Props.MaxValue ?? ResolveMax();
        if (max - min < 0.001)
        {
            max = min + 1;
        }

        var children = new List<Element>();

        if (Props.ShowGrid)
        {
            for (var i = 1; i <= 3; i++)
            {
                var y = Math.Round(height * i / 4d) + 0.5;
                children.Add(Line(0, y, width, y).Stroke(ChartPalette.GridLine).StrokeThickness(1));
            }
        }

        for (var index = 0; index < Props.Series.Count; index++)
        {
            var values = Props.Series[index].Values;
            if (values.Count == 0)
            {
                continue;
            }

            var geometry = BuildGeometry(values, slots, min, max, width, height);
            var stroke = ChartPalette.Series(index);
            children.Add(Path2D().Set(path =>
            {
                path.Data = geometry;
                path.Stroke = stroke;
                path.StrokeThickness = 1.6;
                path.StrokeLineJoin = PenLineJoin.Round;
            }));
        }

        // The canvas stretches to the card column; the measured width only
        // drives the path geometry and must not become a desired-size floor.
        var canvas = Canvas(children.ToArray()).Height(height).HAlign(HorizontalAlignment.Stretch);

        return Border(canvas)
            .OnSizeChanged((_, args) =>
            {
                var next = args.NewSize.Width;
                if (next > 8 && Math.Abs(next - measuredWidth) > 1)
                {
                    setMeasuredWidth(next);
                }
            })
            .AutomationName(DescribeSeries());
    }

    private string DescribeSeries() =>
        Props.Series.Count == 0
            ? Str.T("monitor.chart.empty", "Chart, no data")
            : Str.F(
                "monitor.chart.title",
                "Chart: {0}",
                string.Join(", ", Props.Series.Select(series => series.Name)));

    private double ResolveMax()
    {
        var max = 0d;
        foreach (var series in Props.Series)
        {
            foreach (var value in series.Values)
            {
                if (value > max)
                {
                    max = value;
                }
            }
        }

        return max <= 0 ? 100 : max * 1.05;
    }

    /// <summary>Maps a sample window onto a path geometry in pixel space.</summary>
    private static PathGeometry BuildGeometry(
        IReadOnlyList<double> values,
        int slots,
        double min,
        double max,
        double width,
        double height)
    {
        var usableWidth = Math.Max(1, width - (PlotPadding * 2));
        var usableHeight = Math.Max(1, height - (PlotPadding * 2));
        var step = usableWidth / (slots - 1);
        var offset = Math.Max(0, slots - values.Count);
        var range = max - min;

        var figure = new PathFigure
        {
            IsClosed = false,
            IsFilled = false,
        };

        for (var i = 0; i < values.Count; i++)
        {
            var x = PlotPadding + ((offset + i) * step);
            var normalized = Math.Clamp((values[i] - min) / range, 0d, 1d);
            var y = PlotPadding + ((1 - normalized) * usableHeight);
            var point = new Point(x, y);

            if (i == 0)
            {
                figure.StartPoint = point;
            }
            else
            {
                figure.Segments.Add(new LineSegment { Point = point });
            }
        }

        if (values.Count == 1)
        {
            // Keep the first sample at its actual height on the right edge.
            var point = figure.StartPoint;
            figure.StartPoint = new Point(point.X - 1, point.Y);
            figure.Segments.Add(new LineSegment { Point = point });
        }

        return new PathGeometry { Figures = { figure } };
    }
}
