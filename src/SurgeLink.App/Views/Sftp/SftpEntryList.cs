using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// The file table: a sortable header row and a WinUI <see cref="ListView"/> of
/// entries. Columns are shared grid sizes so header and rows line up; the list
/// keeps the platform's keyboard navigation and multiple selection.
/// </summary>
internal static class SftpEntryList
{
    // Includes the shared cell gutters and row-container padding.
    public const double MinimumWidth = 724;

    private static readonly GridSize[] Columns =
    [
        GridSize.Px(24),
        GridSize.Star().MinSize(180),
        GridSize.Px(88),
        GridSize.Px(152),
        GridSize.Px(108),
        GridSize.Px(96),
    ];

    private static readonly Lazy<Style?> RowStyle = new(CreateRowStyle);

    /// <summary>Sortable header row.</summary>
    public static Element Header(SftpSortColumn sort, bool ascending, Action<SftpSortColumn> sortBy) =>
        Grid(
            columns: Columns,
            rows: [GridSize.Auto],
            SortHeader(Str.T("sftp.column.name", "Name"), SftpSortColumn.Name, sort, ascending, sortBy).Grid(row: 0, column: 1),
            SortHeader(Str.T("sftp.column.size", "Size"), SftpSortColumn.Size, sort, ascending, sortBy)
                .HAlign(HorizontalAlignment.Right)
                .Grid(row: 0, column: 2),
            SortHeader(Str.T("sftp.column.modified", "Modified"), SftpSortColumn.Modified, sort, ascending, sortBy).Grid(row: 0, column: 3),
            SortHeader(Str.T("sftp.column.permissions", "Permissions"), SftpSortColumn.Permissions, sort, ascending, sortBy).Grid(row: 0, column: 4),
            SortHeader(Str.T("sftp.column.owner", "Owner"), SftpSortColumn.Owner, sort, ascending, sortBy).Grid(row: 0, column: 5))
        .Set(grid => grid.ColumnSpacing = AppTheme.SpacingM)
        .Padding(AppTheme.SpacingS, 0);

    /// <summary>The file list. Selection and key handling are owned by the caller.</summary>
    public static Element Body(
        IReadOnlyList<RemoteFileEntry> entries,
        Action<IReadOnlyList<RemoteFileEntry>> selectionChanged,
        Action<RemoteFileEntry> openEntry,
        Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> keyDown) =>
        ListView<RemoteFileEntry>(entries, entry => entry.FullPath, (entry, _) => Row(entry))
            .SelectionChanged(selectionChanged)
            .OnDoubleTapped((sender, args) =>
            {
                // Resolve the row under the pointer: double-tapping empty space
                // must not open the previously selected directory.
                if (sender is Microsoft.UI.Xaml.Controls.ListView list
                    && args.OriginalSource is DependencyObject source)
                {
                    for (DependencyObject? current = source; current is not null && current != list;
                         current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
                    {
                        if (current is Microsoft.UI.Xaml.Controls.ListViewItem container
                            && list.ItemFromContainer(container) is RemoteFileEntry entry)
                        {
                            openEntry(entry);
                            args.Handled = true;
                            break;
                        }
                    }
                }
            })
            .OnKeyDown(keyDown)
            .Set(list =>
            {
                list.SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Extended;
                list.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (RowStyle.Value is { } style)
                {
                    list.ItemContainerStyle = style;
                }
            })
            .AutomationName(Str.T("sftp.list.automation", "Files in the current folder"));

    private static Element SortHeader(
        string label,
        SftpSortColumn column,
        SftpSortColumn active,
        bool ascending,
        Action<SftpSortColumn> sortBy)
    {
        var isActive = column == active;
        Element content = HStack(
            AppTheme.SpacingXs,
            Caption(label).SemiBold(),
            isActive ? Icon(FontIcon(ascending ? "\uE70E" : "\uE70D", fontSize: 10)) : null);

        return Button(content, () => sortBy(column))
            .SubtleButton()
            .Padding(0)
            .HAlign(HorizontalAlignment.Left)
            .AutomationName(Str.F("sftp.column.sortBy", "Sort by {0}", label))
            .MinHeight(32);
    }

    private static Element Row(RemoteFileEntry entry)
    {
        var name = TextBlock(entry.Name).TextTrimming(TextTrimming.CharacterEllipsis);
        if (!string.IsNullOrEmpty(entry.LinkTarget))
        {
            name = (TextBlockElement)name.ToolTip(
                Str.F("sftp.list.linkTarget", "{0} -> {1}", entry.Name, entry.LinkTarget));
        }

        return Grid(
                columns: Columns,
                rows: [GridSize.Auto],
                Icon(FontIcon(Glyph(entry), fontSize: 14)).Grid(row: 0, column: 0),
                name.Grid(row: 0, column: 1),
                TextBlock(entry.IsDirectory ? string.Empty : DataFormat.Bytes(entry.Size))
                    .HAlign(HorizontalAlignment.Right)
                    .Grid(row: 0, column: 2),
                TextBlock(entry.ModifiedUtc == default ? string.Empty : entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 3),
                TextBlock(entry.PermissionText)
                    .FontFamily(AppTheme.Monospace)
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 4),
                TextBlock(entry.Owner ?? string.Empty)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 5))
            .Set(grid => grid.ColumnSpacing = AppTheme.SpacingM)
            .Padding(0, 4, 0, 4)
            .AutomationName(entry.Name);
    }

    private static string Glyph(RemoteFileEntry entry) => entry.Kind switch
    {
        RemoteEntryKind.Directory => "\uE8B7",
        RemoteEntryKind.SymbolicLink => "\uE71B",
        _ => "\uE8A5",
    };

    /// <summary>
    /// Row container style: stretches the row content so the columns line up
    /// with the header. Based on the WinUI default container style so selection,
    /// hover and High Contrast visuals stay intact; returns <c>null</c> when the
    /// platform style cannot be resolved, in which case the default look wins
    /// over perfect column alignment.
    /// </summary>
    private static Style? CreateRowStyle()
    {
        try
        {
            var style = new Style(typeof(Microsoft.UI.Xaml.Controls.ListViewItem));
            var resources = Application.Current.Resources;
            if (resources.ContainsKey("DefaultListViewItemStyle")
                && resources["DefaultListViewItemStyle"] is Style basedOn)
            {
                style.BasedOn = basedOn;
            }
            else
            {
                return null;
            }

            style.Setters.Add(new Setter
            {
                Property = Microsoft.UI.Xaml.Controls.Control.HorizontalContentAlignmentProperty,
                Value = HorizontalAlignment.Stretch,
            });
            style.Setters.Add(new Setter
            {
                Property = FrameworkElement.MinHeightProperty,
                Value = 32d,
            });
            style.Setters.Add(new Setter
            {
                Property = Microsoft.UI.Xaml.Controls.Control.PaddingProperty,
                Value = new Thickness(AppTheme.SpacingS, 0, AppTheme.SpacingS, 0),
            });
            return style;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
