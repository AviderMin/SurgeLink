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
/// The transfer queue panel: one row per file with progress and a cancel
/// button. Transfers are sequential, so the panel doubles as the queue view.
/// </summary>
internal sealed class SftpTransferPanel : Component<FileTransferQueue>
{
    public override Element Render()
    {
        var queue = Props;
        var items = UseExternalStore(queue.Subscribe, () => queue.Snapshot);
        return RenderPanel(items, queue.Cancel, queue.ClearFinished) ?? Empty();
    }

    /// <summary>Renders the panel; returns <c>null</c> when there is nothing to show.</summary>
    private static Element? RenderPanel(IReadOnlyList<TransferItem> items, Action<string> cancel, Action clearFinished)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var active = items.Count(item => item.IsActive);
        var header = Grid(
            columns: [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            BodyStrong(Str.T("sftp.transfers.title", "Transfers")).Grid(row: 0, column: 0),
            Caption(active > 0
                    ? Str.F("sftp.transfers.activeCount", "{0} active", active)
                    : Str.T("sftp.transfers.idle", "idle"))
                .Foreground(Theme.SecondaryText)
                .Margin(AppTheme.SpacingS, 0, 0, 0)
                .Grid(row: 0, column: 1),
            items.Any(item => !item.IsActive)
                ? Button(Str.T("sftp.transfers.clearFinished", "Clear finished"), clearFinished)
                    .SubtleButton()
                    .AutomationName(Str.T("sftp.transfers.clearFinished", "Clear finished"))
                    .Grid(row: 0, column: 3)
                : null);

        var rows = items
            .Select(item => SftpTransferRow.Render(item, cancel).WithKey(item.Id))
            .ToArray();

        return Border(
                VStack(
                    AppTheme.SpacingS,
                    header,
                    Divider(),
                    ScrollView(VStack(AppTheme.SpacingXs, rows)).MaxHeight(160)))
            .Padding(AppTheme.SpacingM)
            .Background(Theme.LayerFill)
            .WithBorder(Theme.DividerStroke, 1)
            .CornerRadius(AppTheme.CardRadius)
            .AutomationName(Str.T("sftp.transfers.automation", "Transfers"));
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
                .ToolTip(Str.T("sftp.transfers.cancel", "Cancel transfer"))
                .AutomationName(Str.F("sftp.transfers.cancelAutomation", "Cancel {0}", item.Name))
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
        TransferStatus.Queued => Str.F("sftp.transfers.queued", "Queued · {0}", DataFormat.Bytes(item.TotalBytes)),
        TransferStatus.Running => Str.F(
            "sftp.transfers.running",
            "{0} · {1} of {2}",
            DataFormat.Percent(item.Fraction),
            DataFormat.Bytes(item.DoneBytes),
            DataFormat.Bytes(item.TotalBytes)),
        TransferStatus.Completed => Str.F("sftp.transfers.completed", "Completed · {0}", DataFormat.Bytes(item.DoneBytes)),
        TransferStatus.Cancelled => Str.T("sftp.transfers.cancelled", "Cancelled"),
        TransferStatus.Failed => Str.F(
            "sftp.transfers.failed",
            "Failed · {0}",
            item.Error ?? Str.T("sftp.transfers.unknownError", "unknown error")),
        _ => item.Status.ToString(),
    };
}
