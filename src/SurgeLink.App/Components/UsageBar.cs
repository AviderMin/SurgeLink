using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Components;

/// <summary>Props for <see cref="UsageBar"/>.</summary>
/// <param name="Fraction">Used share, 0..1. Values outside the range are clamped.</param>
/// <param name="Label">Optional left hand label, e.g. a mount point.</param>
/// <param name="Detail">Optional right hand caption, e.g. "63% of 512 GB".</param>
/// <param name="Height">Bar thickness in device independent pixels (4px grid).</param>
internal sealed record UsageBarProps(double Fraction, string? Label = null, string? Detail = null, double Height = 8);

/// <summary>
/// A slim usage bar: a two star column grid splits the track into the filled
/// share and the remainder, so the bar scales with its container without any
/// measuring. The fill colour follows <see cref="ChartPalette.Severity"/>.
/// </summary>
internal sealed class UsageBar : Component<UsageBarProps>
{
    public override Element Render()
    {
        _ = UseColorScheme(); // repaint when the theme changes
        var fraction = Math.Clamp(Props.Fraction, 0d, 1d);

        // Star(0) is not a valid grid length, so clamp both sides to a hair.
        var bar = Grid(
            columns: [GridSize.Star(Math.Max(fraction, 0.001)), GridSize.Star(Math.Max(1 - fraction, 0.001))],
            rows: [GridSize.Px(Math.Max(Props.Height, 2))],
            Border(Empty()).Background(ChartPalette.Severity(fraction)).CornerRadius(4).Grid(row: 0, column: 0),
            Border(Empty()).Background(ChartPalette.Track).CornerRadius(4).Grid(row: 0, column: 1));

        if (Props.Label is null && Props.Detail is null)
        {
            return bar;
        }

        var header = Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            TextBlock(Props.Label ?? string.Empty).TextTrimming(TextTrimming.CharacterEllipsis).Grid(row: 0, column: 0),
            Caption(Props.Detail ?? string.Empty)
                .Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.Wrap)
                .Grid(row: 0, column: 1)) with { ColumnSpacing = AppTheme.SpacingS };

        return VStack(AppTheme.SpacingXs, header, bar)
            .AutomationName(Str.F(
                "monitor.usageBar.automation",
                "{0} {1}",
                Props.Label ?? Str.T("monitor.usageBar.fallback", "Usage"),
                DataFormat.Percent(fraction)));
    }
}
