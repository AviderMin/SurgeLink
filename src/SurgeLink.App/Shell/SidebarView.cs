using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.App.Styling;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for <see cref="SidebarView"/>; rebuilt whenever the workspace changes.</summary>
internal sealed record SidebarProps(
    WorkspaceSnapshot Snapshot,
    Action<SessionProfile> Connect,
    Action<SessionProfile?> CreateSession,
    Action<SessionProfile> Edit,
    Action<SessionProfile> Duplicate,
    Action<SessionProfile> Delete,
    Action OpenDemo);

/// <summary>
/// Saved-session list shown in the navigation pane: debounced search, profiles
/// grouped by <see cref="SessionProfile.Group"/>, a right-click menu per row and
/// an empty state that explains what to do next.
/// </summary>
internal sealed class SidebarView : Component<SidebarProps>
{
    private const string NoGroupBucket = "Sessions";

    public override Element Render()
    {
        var props = Props;
        var (search, setSearch) = UseState(props.Snapshot.SearchText);

        // Debounce filtering by 250 ms so typing does not re-filter the whole list
        // on every keystroke.
        UseEffect(
            () =>
            {
                if (string.Equals(search, props.Snapshot.SearchText, StringComparison.Ordinal))
                {
                    return () => { };
                }

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    AppServices.EnsureInitialized().Workspace.SetSearchText(search);
                };
                timer.Start();
                return () => timer.Stop();
            },
            search,
            props.Snapshot.SearchText);

        var searchBox = TextBox(Optional<string>.Of(search), setSearch, placeholderText: "Filter sessions")
            .AutomationName("Filter sessions")
            .AccessKey("F")
            .AutomationId("SessionFilter");

        var newSessionButton = Button("New session", () => props.CreateSession(null))
            .AccentButton()
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName("New session")
            .AutomationId("NewSessionButton")
            .Flex(grow: 1, basis: 0);

        var demoButton = Button(Icon(FontIcon("\uE768", fontSize: 14)), props.OpenDemo)
            .SubtleButton()
            .ToolTip("Open the offline demo session")
            .AutomationName("Open demo session")
            .AccessKey("D")
            .AutomationId("OpenDemoButton");

        var filtered = props.Snapshot.FilteredProfiles;
        Element body = filtered.Count > 0
            ? ScrollView(VStack(AppTheme.SpacingXs, BuildGroups(props, filtered).ToArray()))
            : BuildEmptyState(props, search);

        return FlexColumn(
                searchBox,
                HStack(AppTheme.SpacingS, newSessionButton, demoButton),
                body.Flex(grow: 1, basis: 0),
                Caption("Right-click a session for edit, duplicate and delete.")
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.Wrap))
            .FlexPadding(AppTheme.SpacingM, AppTheme.SpacingS);
    }

    private static List<Element> BuildGroups(SidebarProps props, IReadOnlyList<SessionProfile> profiles)
    {
        var elements = new List<Element>(profiles.Count + 4);
        var groups = profiles
            .GroupBy(profile => string.IsNullOrWhiteSpace(profile.Group) ? NoGroupBucket : profile.Group!.Trim())
            .OrderBy(group => string.Equals(group.Key, NoGroupBucket, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            elements.Add(Caption(group.Key)
                .SemiBold()
                .Foreground(Theme.SecondaryText)
                .Margin(AppTheme.SpacingS, AppTheme.SpacingS, 0, AppTheme.SpacingXs)
                .WithKey("GroupHeader:" + group.Key));

            var rows = group.ToList();
            for (var index = 0; index < rows.Count; index++)
            {
                elements.Add(BuildRow(props, rows[index], index + 1, rows.Count).WithKey(rows[index].Id));
            }
        }

        return elements;
    }

    private static Element BuildRow(SidebarProps props, SessionProfile profile, int position, int total)
    {
        var subtitle = $"{profile.Username}@{profile.Host}" +
                       (profile.Port > 0 ? ":" + profile.Port.ToString() : string.Empty);

        return Button(
                Grid(
                    columns: [GridSize.Auto, GridSize.Star()],
                    rows: [GridSize.Auto, GridSize.Auto],
                    Icon(FontIcon("\uE774", fontSize: 16))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, rowSpan: 2, column: 0)
                        .Margin(0, 0, AppTheme.SpacingS, 0),
                    TextBlock(profile.Name)
                        .SemiBold()
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .Grid(row: 0, column: 1),
                    Caption(subtitle)
                        .Foreground(Theme.SecondaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .Grid(row: 1, column: 1)),
                () => props.Connect(profile))
            .SubtleButton()
            .HAlign(HorizontalAlignment.Stretch)
            .MinHeight(AppTheme.RowMinHeight)
            .ToolTip($"{profile.Name}\n{subtitle}")
            .AutomationName(profile.Name)
            .AutomationId("Session-" + profile.Id)
            .PositionInSet(position, total)
            .WithContextFlyout(MenuItems(
                MenuItem("Connect", () => props.Connect(profile)),
                MenuItem("Edit", () => props.Edit(profile)),
                MenuItem("Duplicate", () => props.Duplicate(profile)),
                MenuSeparator(),
                MenuItem("Delete", () => props.Delete(profile))));
    }

    private static Element BuildEmptyState(SidebarProps props, string search)
    {
        var isFiltered = !string.IsNullOrWhiteSpace(search) && props.Snapshot.Profiles.Count > 0;
        return VStack(
                AppTheme.SpacingS,
                Icon(FontIcon(isFiltered ? "\uE721" : "\uE774", fontSize: 28)),
                BodyStrong(isFiltered ? "No matching sessions" : "No saved sessions yet"),
                Caption(isFiltered
                        ? $"Nothing matches \"{search.Trim()}\". Clear the filter to see every session."
                        : "Create a connection to get started, or explore the offline demo session.")
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.Wrap)
                    .TextAlignment(TextAlignment.Center),
                isFiltered
                    ? Button("Clear filter", () => AppServices.EnsureInitialized().Workspace.SetSearchText(string.Empty))
                        .AutomationName("Clear session filter")
                    : Button("New session", () => props.CreateSession(null))
                        .AccentButton()
                        .AutomationName("Create first session"))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Margin(AppTheme.SpacingL, AppTheme.SpacingXl);
    }
}
