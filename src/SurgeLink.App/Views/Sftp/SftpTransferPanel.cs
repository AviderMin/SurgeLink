using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Components;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// The transfer queue panel: one row per file with progress and a cancel
/// button. Transfers are sequential, so the panel doubles as the queue view.
/// </summary>
internal static class SftpTransferPanel
{
    /// <summary>Renders the panel; returns <c>null</c> when there is nothing to show.</summary>
    public static Element? Render(IReadOnlyList<TransferItem> items, Action<string> cancel, Action clearFinished)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var active = items.Count(item => item.IsActive);
        var header = Grid(
            columns: [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            BodyStrong("Transfers").Grid(row: 0, column: 0),
            Caption(active > 0 ? active + " active" : "idle").Foreground(Theme.SecondaryText)
                .Margin(AppTheme.SpacingS, 0, 0, 0)
                .Grid(row: 0, column: 1),
            items.Any(item => !item.IsActive)
                ? Button("Clear finished", clearFinished).SubtleButton().Grid(row: 0, column: 3)
                : null);

        var rows = items
            .Select(item => SftpTransferRow.Render(item, cancel).WithKey(item.Id))
            .ToArray();

        return Border(
                VStack(
                    AppTheme.SpacingS,
                    header,
                    Divider(),
                    VStack(AppTheme.SpacingXs, rows)))
            .Padding(AppTheme.SpacingM)
            .Background(Theme.LayerFill)
            .WithBorder(Theme.DividerStroke, 1)
            .CornerRadius(AppTheme.CardRadius)
            .AutomationName("Transfers");
    }

    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.DividerStroke);
}

/// <summary>Renders one transfer row.</summary>
internal static class SftpTransferRow
{
    public static Element Render(TransferItem item, Action<string> cancel)
    {
        var failed = item.Status == TransferStatus.Failed;
        var glyph = item.Direction == TransferDirection.Upload ? "\uE898" : "\uE896";
        var status = DescribeStatus(item);

        Element cancelButton = item.IsActive
            ? Button(Icon(FontIcon("\uE711", fontSize: 12)), () => cancel(item.Id))
                .SubtleButton()
                .ToolTip("Cancel transfer")
                .AutomationName("Cancel " + item.Name)
            : Border(Empty());

        return Grid(
            columns: [GridSize.Px(24), GridSize.Star(), GridSize.Px(140), GridSize.Px(40)],
            rows: [GridSize.Auto],
            Icon(FontIcon(glyph, fontSize: 14)).Grid(row: 0, column: 0),
            VStack(
                    2,
                    TextBlock(item.Name).TextTrimming(TextTrimming.CharacterEllipsis),
                    Caption(status).Foreground(failed ? Theme.SystemCritical : Theme.SecondaryText))
                .Grid(row: 0, column: 1),
            Progress(item.Fraction * 100)
                .Grid(row: 0, column: 2)
                .VAlign(VerticalAlignment.Center)
                .Margin(AppTheme.SpacingS, 0, AppTheme.SpacingS, 0),
            cancelButton.Grid(row: 0, column: 3));
    }

    private static string DescribeStatus(TransferItem item) => item.Status switch
    {
        TransferStatus.Queued => "Queued · " + DataFormat.Bytes(item.TotalBytes),
        TransferStatus.Running => DataFormat.Percent(item.Fraction) + " · "
                                  + DataFormat.Bytes(item.DoneBytes) + " of " + DataFormat.Bytes(item.TotalBytes),
        TransferStatus.Completed => "Completed · " + DataFormat.Bytes(item.DoneBytes),
        TransferStatus.Cancelled => "Cancelled",
        TransferStatus.Failed => "Failed · " + (item.Error ?? "unknown error"),
        _ => item.Status.ToString(),
    };
}
