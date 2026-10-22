using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Components;
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
    /// <summary>Internal grouping key for profiles without a group; never shown as-is.</summary>
    private const string NoGroupBucket = "\u0000ungrouped";

    public override Element Render()
    {
        var services = UseContext(AppServices.ReactorContext)
            ?? throw new InvalidOperationException("The sidebar must be inside the shell service context.");
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
                    AppServices.RequireInitialized.Workspace.SetSearchText(search);
                };
                timer.Start();
                return () => timer.Stop();
            },
            search,
            props.Snapshot.SearchText);

        var filterLabel = Str.T("shell.sidebar.filter", "Filter sessions");
        var searchBox = TextBox(Optional<string>.Of(search), setSearch, placeholderText: filterLabel)
            .AutomationName(filterLabel)
            .AccessKey("F")
            .AutomationId("SessionFilter");

        var newSession = Str.T("shell.sidebar.newSession", "New session");
        var newSessionButton = Button(newSession, () => props.CreateSession(null))
            .AccentButton()
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(newSession)
            .AutomationId("NewSessionButton");

        var demoLabel = Str.T("shell.sidebar.demo", "Open demo session");
        var demoButton = Button(Icon(FontIcon("\uE768", fontSize: 14)), props.OpenDemo)
            .SubtleButton()
            .ToolTip(Str.T("shell.sidebar.demoTooltip", "Open the offline demo session"))
            .AutomationName(demoLabel)
            .AccessKey("D")
            .AutomationId("OpenDemoButton");

        var filtered = props.Snapshot.FilteredProfiles;
        Element body = filtered.Count > 0
            ? ScrollView(VStack(AppTheme.SpacingXs, BuildGroups(props, filtered).ToArray()))
            : BuildEmptyState(props, search, () =>
            {
                setSearch(string.Empty);
                AppServices.RequireInitialized.Workspace.SetSearchText(string.Empty);
            });

        return Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                searchBox.Grid(row: 0),
                (Grid(
                    columns: [GridSize.Star(), GridSize.Auto],
                    rows: [GridSize.Auto],
                    newSessionButton.Grid(column: 0),
                    demoButton.Grid(column: 1)) with { ColumnSpacing = AppTheme.SpacingS }).Grid(row: 1),
                body.Grid(row: 2),
                Component<SidebarMonitor, SidebarMonitorProps>(new SidebarMonitorProps(
                        props.Snapshot.ActiveTab?.Session.Monitor,
                        props.Snapshot.ActiveTab is not null,
                        services.Settings))
                    .Grid(row: 3)) with
        {
            RowSpacing = AppTheme.SpacingM,
            Padding = new Thickness(AppTheme.SpacingM),
        };
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
            // The bucket for ungrouped profiles is labelled; user groups keep their name.
            var header = string.Equals(group.Key, NoGroupBucket, StringComparison.Ordinal)
                ? Str.T("shell.sidebar.group.other", "Sessions")
                : group.Key;

            elements.Add(Caption(header)
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
                    SessionSystemIcon.Render(profile.DetectedOperatingSystem)
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
            .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Stretch)
            .MinHeight(AppTheme.RowMinHeight)
            .ToolTip($"{profile.Name}\n{subtitle}" +
                (string.IsNullOrWhiteSpace(profile.DetectedOperatingSystem) ? string.Empty : "\n" + profile.DetectedOperatingSystem))
            .AutomationName(profile.Name)
            .AutomationId("Session-" + profile.Id)
            .PositionInSet(position, total)
            .WithContextFlyout(MenuItems(
                MenuItem(Str.T("shell.sidebar.menu.connect", "Connect"), () => props.Connect(profile)),
                MenuItem(Str.T("shell.sidebar.menu.edit", "Edit"), () => props.Edit(profile)),
                MenuItem(Str.T("shell.sidebar.menu.duplicate", "Duplicate"), () => props.Duplicate(profile)),
                MenuSeparator(),
                MenuItem(Str.T("shell.sidebar.menu.delete", "Delete"), () => props.Delete(profile))));
    }

    private static Element BuildEmptyState(SidebarProps props, string search, Action clearSearch)
    {
        var isFiltered = !string.IsNullOrWhiteSpace(search) && props.Snapshot.Profiles.Count > 0;
        return VStack(
                AppTheme.SpacingS,
                Icon(FontIcon(isFiltered ? "\uE721" : "\uE774", fontSize: 28)),
                BodyStrong(isFiltered
                    ? Str.T("shell.sidebar.empty.noMatches", "No matching sessions")
                    : Str.T("shell.sidebar.empty.noSessions", "No saved sessions yet")),
                Caption(isFiltered
                        ? Str.F("shell.sidebar.empty.noMatchesHint",
                            "Nothing matches \"{0}\". Clear the filter to see every session.", search.Trim())
                        : Str.T("shell.sidebar.empty.noSessionsHint",
                            "Create a connection to get started, or explore the offline demo session."))
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.Wrap)
                    .TextAlignment(TextAlignment.Center),
                isFiltered
                    ? Button(Str.T("shell.sidebar.clearFilter", "Clear filter"),
                            clearSearch)
                        .AutomationName(Str.T("shell.sidebar.clearFilterAutomation", "Clear session filter"))
                    : Button(Str.T("shell.sidebar.createFirst", "New session"), () => props.CreateSession(null))
                        .AccentButton()
                        .AutomationName(Str.T("shell.sidebar.createFirstAutomation", "Create first session")))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Margin(AppTheme.SpacingL, AppTheme.SpacingXl);
    }
}
