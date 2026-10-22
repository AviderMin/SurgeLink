using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Sessions;
using SurgeLink.App.Styling;
using SurgeLink.App.Services;
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
                PrivacyDisplay.Mask(tab.Title),
                Component<SessionWorkspaceView, SessionViewProps>(SessionWorkspaceView.PropsFor(tab))
                    .WithKey(tab.Id.ToString("n")))
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
            .Set(view =>
            {
                view.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                view.VerticalContentAlignment = VerticalAlignment.Stretch;
                var items = view.TabItems.OfType<Microsoft.UI.Xaml.Controls.TabViewItem>().ToArray();
                for (var index = 0; index < items.Length && index < snapshot.Tabs.Count; index++)
                {
                    var item = items[index];
                    var tab = snapshot.Tabs[index];
                    item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    item.VerticalContentAlignment = VerticalAlignment.Stretch;
                    SessionTabHeader.Apply(item, tab.Title,
                        tab.Session.HostInfo?.OperatingSystem ?? tab.Profile.DetectedOperatingSystem);
                }
            })
            // TabViewItem content is left/top aligned by default, which sizes every
            // session view to its own content and leaves the rest of the pane empty.
            .FillContentArea()
            .IsAddTabButtonVisible(true)
            .AddTabButtonClick(() => props.NewSession(null))
            // Keep visual order aligned with the store used by selection and close callbacks.
            .CanReorderTabs(false)
            .TabCloseRequested(index =>
            {
                if (index >= 0 && index < snapshot.Tabs.Count)
                {
                    props.CloseTab(snapshot.Tabs[index]);
                }
            });
    }

    private static Element BuildEmptyWorkspace(WorkspaceProps props)
    {
        var newSession = Str.T("shell.workspace.newSession", "New session");
        var openDemo = Str.T("shell.workspace.openDemo", "Open demo session");

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            VStack(
                    AppTheme.SpacingM,
                    Icon(FontIcon("\uE756", fontSize: 44)).HAlign(HorizontalAlignment.Center),
                    Title(Str.T("shell.workspace.emptyTitle", "No session open")).HAlign(HorizontalAlignment.Center),
                    Body(Str.T(
                            "shell.workspace.emptyBody",
                            "Pick a saved session on the left, create a new connection, or explore the offline demo."))
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.Wrap)
                        .TextAlignment(TextAlignment.Center),
                    HStack(
                            AppTheme.SpacingS,
                            Button(newSession, () => props.NewSession(null))
                                .AccentButton()
                                .AutomationName(newSession),
                            Button(openDemo, props.OpenDemo)
                                .AutomationName(openDemo)
                                .AutomationId("EmptyStateOpenDemo"))
                        .HAlign(HorizontalAlignment.Center)
                        .Margin(0, AppTheme.SpacingM, 0, 0))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .MaxWidth(560)
                .Margin(AppTheme.SpacingXl)
                .Grid(row: 0));
    }
}
