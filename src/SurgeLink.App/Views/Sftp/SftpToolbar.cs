using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Everything the SFTP toolbar displays and invokes.</summary>
/// <param name="CurrentPath">Path shown in the breadcrumb.</param>
/// <param name="CanGoUp">Enables the Up button (never at the root).</param>
/// <param name="CanDownload">At least one file is selected.</param>
/// <param name="CanModify">A current folder listing is available.</param>
/// <param name="SingleSelection">Exactly one entry is selected.</param>
/// <param name="SupportsPermissions">The protocol can read and write POSIX modes.</param>
/// <param name="CanEdit">Exactly one regular file is selected and can be opened in the text editor.</param>
internal sealed record SftpToolbarProps(
    string CurrentPath,
    bool CanGoUp,
    bool CanDownload,
    bool CanModify,
    bool SingleSelection,
    bool SupportsPermissions,
    bool CanEdit,
    Action<string> Navigate,
    Action GoUp,
    Action GoHome,
    Action Refresh,
    Action NewFile,
    Action NewFolder,
    Action Upload,
    Action Download,
    Action Rename,
    Action Delete,
    Action EditPermissions,
    Action Edit);

/// <summary>
/// Path breadcrumb plus the action commands of the file manager. Commands live
/// in a WinUI <see cref="CommandBar"/>, so overflow and keyboard accelerators
/// follow the platform behaviour; all file actions, including Edit, are primary commands.
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
            .AutomationName(Str.T("sftp.breadcrumb.automation", "Current path"));

        var navigation = HStack(
            AppTheme.SpacingXs,
            IconButton(Str.T("sftp.nav.up", "Up one level"), "\uE74A", props.CanGoUp ? props.GoUp : null),
            IconButton(Str.T("sftp.nav.home", "Home directory"), "\uE80F", props.GoHome),
            IconButton(Str.T("sftp.nav.refresh", "Refresh listing"), "\uE72C", props.Refresh));

        var commands = CommandBar(
                primaryCommands:
                [
                    AppBarButton(Str.T("sftp.command.newFile", "New file"), props.NewFile, "\uE8A5") with
                    {
                        IsEnabled = props.CanModify,
                    },
                    AppBarButton(Str.T("sftp.command.newFolder", "New folder"), props.NewFolder, "\uE8F4") with
                    {
                        IsEnabled = props.CanModify,
                    },
                    AppBarButton(Str.T("sftp.command.upload", "Upload"), props.Upload, "\uE898") with
                    {
                        IsEnabled = props.CanModify,
                    },
                    AppBarButton(Str.T("sftp.command.download", "Download"), props.Download, "\uE896") with
                    {
                        IsEnabled = props.CanDownload,
                    },
                    AppBarSeparator(),
                    AppBarButton(Str.T("sftp.command.rename", "Rename"), props.Rename, "\uE8AC") with
                    {
                        IsEnabled = props.SingleSelection,
                    },
                    AppBarButton(Str.T("sftp.command.delete", "Delete"), props.Delete, "\uE74D") with
                    {
                        IsEnabled = props.SingleSelection,
                    },
                    AppBarButton(Str.T("sftp.command.permissions", "Permissions"), props.EditPermissions, "\uE72E") with
                    {
                        IsEnabled = props.SupportsPermissions && props.SingleSelection,
                    },
                    AppBarSeparator(),
                    AppBarButton(Str.T("sftp.command.edit", "Edit"), props.Edit, "\uE70F") with
                    {
                        IsEnabled = props.CanEdit,
                    },
                ])
            .Set(bar => bar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right);

        return VStack(
            AppTheme.SpacingS,
            Grid(
                columns: [GridSize.Auto, GridSize.Star()],
                rows: [GridSize.Auto],
                navigation.Grid(row: 0, column: 0),
                breadcrumb.Grid(row: 0, column: 1).Margin(AppTheme.SpacingS, 0, 0, 0)),
            commands);
    }

    private static Element IconButton(string label, string glyph, Action? onClick) =>
        Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .IsEnabled(onClick is not null)
            .ToolTip(label)
            .AutomationName(label);
}
