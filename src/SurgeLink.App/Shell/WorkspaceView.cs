using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Sessions;
using SurgeLink.App.Styling;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for <see cref="WorkspaceView"/>.</summary>
internal sealed record WorkspaceProps(
    WorkspaceSnapshot Snapshot,
    Action<SessionProfile?> NewSession,
    Action<SessionProfile> Connect,
    Action<WorkspaceTab> CloseTab,
    Action<WorkspaceTab> ActivateTab,
    Action OpenDemo);

/// <summary>
/// The content area: one tab per open session, or a friendly empty state when
/// nothing is connected.
/// </summary>
internal static class WorkspaceView
{
    public static Element Render(WorkspaceProps props)
    {
        var snapshot = props.Snapshot;
        if (snapshot.Tabs.Count == 0)
        {
            return BuildEmptyWorkspace(props);
        }

        var tabs = snapshot.Tabs
            .Select(tab => new TabViewItemData(
                tab.Title,
                Component<SessionWorkspaceView, SessionViewProps>(SessionWorkspaceView.PropsFor(tab)))
            {
                IsClosable = true,
            })
            .ToArray();

        var activeIndex = 0;
        for (var index = 0; index < snapshot.Tabs.Count; index++)
        {
            if (snapshot.Tabs[index].Id == snapshot.ActiveTabId)
            {
                activeIndex = index;
                break;
            }
        }

        return TabView(
                Optional<int>.Of(activeIndex),
                index =>
                {
                    if (index >= 0 && index < snapshot.Tabs.Count)
                    {
                        props.ActivateTab(snapshot.Tabs[index]);
                    }
                },
                tabs)
            .IsAddTabButtonVisible(true)
            .AddTabButtonClick(() => props.NewSession(null))
            .CanReorderTabs(true)
            .TabCloseRequested(index =>
            {
                if (index >= 0 && index < snapshot.Tabs.Count)
                {
                    props.CloseTab(snapshot.Tabs[index]);
                }
            });
    }

    private static Element BuildEmptyWorkspace(WorkspaceProps props) =>
        Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            VStack(
                    AppTheme.SpacingM,
                    Icon(FontIcon("\uE756", fontSize: 44)),
                    Title("No session open").HAlign(HorizontalAlignment.Center),
                    Body("Pick a saved session on the left, create a new connection, or explore the offline demo.")
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.Wrap)
                        .TextAlignment(TextAlignment.Center),
                    HStack(
                            AppTheme.SpacingS,
                            Button("New session", () => props.NewSession(null))
                                .AccentButton()
                                .AutomationName("New session"),
                            Button("Open demo session", props.OpenDemo)
                                .AutomationName("Open demo session")
                                .AutomationId("EmptyStateOpenDemo"))
                        .HAlign(HorizontalAlignment.Center)
                        .Margin(0, AppTheme.SpacingM, 0, 0))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .MaxWidth(560)
                .Grid(row: 0));
}
