using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Components;

/// <summary>Props for <see cref="MetricCard"/>.</summary>
internal sealed record MetricCardProps(string Title, string? Caption, Element Content);

/// <summary>
/// A dashboard card: WinUI card surface (background, hairline stroke, 8px
/// radius, 16px padding) with a title row and arbitrary content. Clipped to the
/// content so charts and bars line up across the dashboard grid.
/// </summary>
internal sealed class MetricCard : Component<MetricCardProps>
{
    public override Element Render() =>
        Card(
                VStack(
                    AppTheme.SpacingM,
                    VStack(
                        AppTheme.SpacingXs,
                        BodyStrong(Props.Title)
                            .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2),
                        Props.Caption is null
                            ? null
                            : Caption(Props.Caption)
                                .Foreground(Theme.SecondaryText)
                                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.Wrap)),
                    Props.Content))
            .AutomationName(Props.Title);
}
