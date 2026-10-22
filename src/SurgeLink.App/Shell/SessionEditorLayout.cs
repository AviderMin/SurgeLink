using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Theme-aware form sections using the WinUI type ramp and spacing tokens.</summary>
internal static class SessionEditorLayout
{
    public static Element Section(string title, string description, params Element[] fields) =>
        VStack(AppTheme.SpacingM,
            VStack(AppTheme.SpacingXs,
                BodyStrong(title).HeadingLevel(AutomationHeadingLevel.Level2),
                Caption(description).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)),
            Card(VStack(AppTheme.SpacingL, fields)));

    public static Element Labeled(string label, Element input) =>
        VStack(AppTheme.SpacingS, Body(label), input.HAlign(HorizontalAlignment.Stretch));

    public static Element Pair(Element first, Element second, GridSize secondColumn) =>
        Grid(columns: [GridSize.Star(), secondColumn], rows: [GridSize.Auto], first, second) with
        {
            ColumnSpacing = AppTheme.SpacingL,
        };
}
