using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Everything the SFTP toolbar displays and invokes.</summary>
/// <param name="CurrentPath">Path shown in the breadcrumb.</param>
/// <param name="CanGoUp">Enables the Up button (never at the root).</param>
/// <param name="HasSelection">Enables selection based commands.</param>
/// <param name="SingleSelection">Exactly one entry is selected.</param>
/// <param name="SupportsPermissions">The protocol can read and write POSIX modes.</param>
/// <param name="ShowHiddenFiles">State of the hidden file toggle.</param>
internal sealed record SftpToolbarProps(
    string CurrentPath,
    bool CanGoUp,
    bool HasSelection,
    bool SingleSelection,
    bool SupportsPermissions,
    bool ShowHiddenFiles,
    Action<string> Navigate,
    Action GoUp,
    Action GoHome,
    Action Refresh,
    Action NewFolder,
    Action Upload,
    Action Download,
    Action Rename,
    Action Delete,
    Action EditPermissions,
    Action<bool> ToggleHidden);

/// <summary>
/// Path breadcrumb plus the action commands of the file manager. Commands live
/// in a WinUI <see cref="CommandBar"/>, so overflow and keyboard accelerators
/// follow the platform behaviour; the hidden file toggle sits next to it.
/// </summary>
internal static class SftpToolbar
{
    public static Element Render(SftpToolbarProps props)
    {
        var breadcrumb = BreadcrumbBar(
            SftpPath.Segments(props.CurrentPath)
                .Select(segment => Breadcrumb(segment.Label, segment.Path))
                .ToArray(),
            item =>
            {
                if (item?.Tag is string path)
                {
                    props.Navigate(path);
                }
            })
            .AutomationName("Current path");

        var navigation = HStack(
            AppTheme.SpacingXs,
            IconButton("Up one level", "\uE74A", props.CanGoUp ? props.GoUp : null),
            IconButton("Home directory", "\uE80F", props.GoHome),
            IconButton("Refresh listing", "\uE72C", props.Refresh));

        var commands = CommandBar(
                primaryCommands:
                [
                    AppBarButton("New folder", props.NewFolder, "\uE8F4"),
                    AppBarButton("Upload", props.Upload, "\uE898"),
                    AppBarButton("Download", props.Download, "\uE896") with { IsEnabled = props.HasSelection },
                    AppBarSeparator(),
                    AppBarButton("Rename", props.Rename, "\uE8AC") with { IsEnabled = props.SingleSelection },
                    AppBarButton("Delete", props.Delete, "\uE74D") with { IsEnabled = props.HasSelection },
                    AppBarButton("Permissions", props.EditPermissions, "\uE72E") with
                    {
                        IsEnabled = props.SupportsPermissions && props.SingleSelection,
                    },
                ])
            .Set(bar => bar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right);

        var hiddenToggle = ToggleButton("Hidden files", props.ShowHiddenFiles, props.ToggleHidden)
            .ToolTip("Show dot files")
            .AutomationName("Show hidden files")
            .VAlign(VerticalAlignment.Center)
            .Margin(AppTheme.SpacingS, 0, 0, 0);

        return VStack(
            AppTheme.SpacingS,
            Grid(
                columns: [GridSize.Auto, GridSize.Star()],
                rows: [GridSize.Auto],
                navigation.Grid(row: 0, column: 0),
                breadcrumb.Grid(row: 0, column: 1).Margin(AppTheme.SpacingS, 0, 0, 0)),
            Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto],
                commands.Grid(row: 0, column: 0),
                hiddenToggle.Grid(row: 0, column: 1)));
    }

    private static Element IconButton(string label, string glyph, Action? onClick) =>
        Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .IsEnabled(onClick is not null)
            .ToolTip(label)
            .AutomationName(label);
}
