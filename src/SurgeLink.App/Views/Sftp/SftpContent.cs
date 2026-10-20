using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SurgeLink.App.Components;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// Stateless pieces of the file manager: the state driven content area (loading,
/// error, empty, list), the "capability missing" page and the filter/sort
/// pipeline of the entry table.
/// </summary>
internal static class SftpContent
{
    /// <summary>Renders whatever the current state should show in place of the list.</summary>
    public static Element Build(
        IReadOnlyList<RemoteFileEntry> rows,
        bool loading,
        string? error,
        Action<IReadOnlyList<RemoteFileEntry>> setSelected,
        Action<RemoteFileEntry> openEntry,
        Action<object, KeyRoutedEventArgs> keyDown,
        Action refresh,
        Action<string> setDialogText,
        Action<SftpDialogState> setDialog)
    {
        if (loading)
        {
            return Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Star()],
                    ProgressRing().HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                .MinHeight(160);
        }

        if (error is not null)
        {
            return Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto],
                (InfoBar("Could not load the folder", error) with
                {
                    Severity = InfoBarSeverity.Error,
                    ActionButtonContent = "Retry",
                    OnActionButtonClick = refresh,
                    IsClosable = false,
                }).Grid(row: 0, column: 0));
        }

        if (rows.Count == 0)
        {
            return Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Star()],
                VStack(
                        AppTheme.SpacingS,
                        Icon(FontIcon("\uE8B7", fontSize: 32)),
                        BodyStrong("This folder is empty"),
                        Caption("Upload a file or create a folder to get started.").Foreground(Theme.SecondaryText),
                        Button("New folder", () =>
                        {
                            setDialogText(string.Empty);
                            setDialog(new SftpDialogState(SftpDialogKind.NewFolder));
                        }).Margin(0, AppTheme.SpacingS, 0, 0))
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0));
        }

        return SftpEntryList.Body(rows, setSelected, openEntry, keyDown);
    }

    /// <summary>Shown when the protocol has no file system at all.</summary>
    public static Element Unavailable() =>
        Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            VStack(
                    AppTheme.SpacingS,
                    Icon(FontIcon("\uE72E", fontSize: 32)),
                    BodyStrong("File transfer is not available"),
                    Caption("This protocol does not expose an SFTP file system, so there is nothing to browse.")
                        .Foreground(Theme.SecondaryText)
                        .MaxWidth(420)
                        .TextWrapping(TextWrapping.Wrap))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center));

    /// <summary>Applies the hidden file filter, the sort column and the directory-first rule.</summary>
    public static IReadOnlyList<RemoteFileEntry> BuildRows(
        IReadOnlyList<RemoteFileEntry> entries,
        bool showHidden,
        SftpSortColumn column,
        bool ascending)
    {
        IEnumerable<RemoteFileEntry> filtered = showHidden
            ? entries
            : entries.Where(entry => !entry.Name.StartsWith('.'));

        IEnumerable<RemoteFileEntry> sorted = column switch
        {
            SftpSortColumn.Size => filtered.OrderBy(entry => entry.Size),
            SftpSortColumn.Modified => filtered.OrderBy(entry => entry.ModifiedUtc),
            SftpSortColumn.Permissions => filtered.OrderBy(entry => entry.PermissionText, StringComparer.Ordinal),
            SftpSortColumn.Owner => filtered.OrderBy(entry => entry.Owner ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };

        if (!ascending)
        {
            sorted = sorted.Reverse();
        }

        // LINQ OrderBy is stable, so this keeps the chosen order inside each group.
        return sorted.OrderByDescending(entry => entry.IsDirectory).ToList();
    }
}
