using System.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Props for the standalone text editor window.</summary>
internal sealed record FileEditorWindowProps(
    IRemoteFileSystem FileSystem,
    RemoteFileEntry Target,
    IAppSettingsStore Settings);

/// <summary>
/// A standalone editor window for one remote text file. The window owns its
/// editing state, reads through the session's file system capability and writes
/// back through the same SFTP/SCP path, so no UI-thread file I/O is performed.
/// </summary>
internal sealed class FileEditorWindow : Component<FileEditorWindowProps>
{
    private const int MaximumEditorBytes = 4 * 1024 * 1024;
    private ScrollViewer? _gutterScroll;

    public override Element Render()
    {
        var props = Props;
        var settings = UseExternalStore(props.Settings.Subscribe, () => props.Settings.Current);
        var target = props.Target;
        var (text, setText) = UseState(string.Empty, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (saving, setSaving) = UseState(false, threadSafe: true);
        var (dirty, setDirty) = UseState(false, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (status, setStatus) = UseState<string?>(null, threadSafe: true);
        var (wrap, setWrap) = UseState(true);
        var lineNumberTransform = UseMemo(() => new TranslateTransform());

        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                _ = LoadAsync(cancellation.Token);
                return () => cancellation.Cancel();
            },
            props.FileSystem,
            target.FullPath);

        async Task LoadAsync(CancellationToken cancellationToken)
        {
            setLoading(true);
            setError(null);
            setStatus(null);
            try
            {
                if (target.Size > MaximumEditorBytes)
                {
                    throw new InvalidOperationException(
                        Str.T("sftp.editor.tooLarge", "This file is too large for the built-in text editor."));
                }

                await using var stream = await props.FileSystem
                    .OpenReadAsync(target.FullPath, cancellationToken)
                    .ConfigureAwait(true);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                setText(content);
                setDirty(false);
                setLoading(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                setError(exception.Message);
                setLoading(false);
            }
        }

        async Task SaveAsync()
        {
            if (loading || saving || error is not null)
            {
                return;
            }

            setSaving(true);
            setError(null);
            setStatus(null);
            try
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                await using (var stream = await props.FileSystem
                                 .OpenWriteAsync(
                                     target.FullPath,
                                     bytes.Length,
                                     progress: null,
                                     CancellationToken.None)
                                 .ConfigureAwait(true))
                {
                    await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(true);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(true);
                }

                setDirty(false);
                setSaving(false);
                setStatus(Str.T("sftp.editor.saved", "Saved"));
            }
            catch (Exception exception)
            {
                setError(exception.Message);
                setSaving(false);
            }
        }

        void Reload()
        {
            if (!loading && !saving)
            {
                _ = LoadAsync(CancellationToken.None);
            }
        }

        var lineCount = text.Count(character => character == '\n') + 1;
        var characterCount = text.Length;
        var lineNumbers = string.Join(
            "\n",
            Enumerable.Range(1, lineCount).Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var statusText = error ?? (loading
            ? Str.T("sftp.editor.loading", "Loading file…")
            : saving
                ? Str.T("sftp.editor.saving", "Saving…")
                : dirty
                    ? Str.T("sftp.editor.unsaved", "Unsaved changes")
                    : status ?? Str.T("sftp.editor.ready", "Ready"));

        var header = Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            VStack(
                    AppTheme.SpacingXs,
                    BodyStrong(target.Name).TextTrimming(TextTrimming.CharacterEllipsis),
                    TextBlock(target.FullPath)
                        .FontFamily(AppTheme.Monospace)
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis))
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            HStack(
                    AppTheme.SpacingS,
                    ToggleButton(Str.T("sftp.editor.wordWrap", "Word wrap"), wrap, setWrap)
                        .AutomationName(Str.T("sftp.editor.wordWrap", "Word wrap")),
                    Button(Str.T("sftp.editor.reload", "Reload"), Reload)
                        .SubtleButton()
                        .IsEnabled(!loading && !saving)
                        .AutomationName(Str.T("sftp.editor.reload", "Reload")),
                    Button(Str.T("sftp.editor.save", "Save"), () => _ = SaveAsync())
                        .AccentButton()
                        .IsEnabled(!loading && !saving && error is null)
                        .AutomationName(Str.T("sftp.editor.save", "Save")))
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1))
            .Padding(AppTheme.SpacingL, AppTheme.SpacingM, AppTheme.SpacingL, AppTheme.SpacingS);

        Element body;
        if (loading)
        {
            body = Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Star()],
                    ProgressRing().HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                .MinHeight(240);
        }
        else if (error is not null)
        {
            body = InfoBar(Str.T("sftp.editor.openFailed", "Could not open the file"), error)
                .Severity(InfoBarSeverity.Error)
                .IsClosable(false)
                .VAlign(VerticalAlignment.Top);
        }
        else
        {
            var gutter = ScrollViewer(
                    TextBlock(lineNumbers)
                        .FontFamily(AppTheme.Monospace)
                        .FontSize(14)
                        .LineHeight(20)
                        .Foreground(Theme.SecondaryText)
                        .TextAlignment(TextAlignment.Right)
                        .Padding(0, 8, 8, 8)
                        .Set(block => block.IsHitTestVisible = false))
                .Set(viewer =>
                {
                    _gutterScroll = viewer;
                    viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                    viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    viewer.VerticalScrollMode = ScrollMode.Enabled;
                    viewer.HorizontalScrollMode = ScrollMode.Disabled;
                    viewer.IsHitTestVisible = false;
                })
                .Width(48);

            body = Border(
                    Grid(
                        columns: [GridSize.Px(48), GridSize.Star()],
                        rows: [GridSize.Star()],
                        gutter.Grid(row: 0, column: 0),
                        TextBox(text, value =>
                            {
                                setText(value);
                                setDirty(true);
                                setStatus(null);
                            })
                            .AcceptsReturn(true)
                            .TextWrapping(wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
                            .FontFamily(AppTheme.Monospace)
                            .FontSize(14)
                            .AutomationName(Str.T("sftp.editor.contents", "File contents"))
                            .HAlign(HorizontalAlignment.Stretch)
                            .VAlign(VerticalAlignment.Stretch)
                            .Set(box =>
                            {
                                box.IsSpellCheckEnabled = false;
                                Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                                Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                            })
                            .OnMountAdd(fe =>
                            {
                                if (fe is TextBox editor)
                                {
                                    AttachEditorScroll(editor);
                                }
                            })
                            .Grid(row: 0, column: 1)))
                .Padding(AppTheme.SpacingS)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(AppTheme.CardRadius);
        }

        var footer = (Grid(
                columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                rows: [GridSize.Auto],
                TextBlock(statusText)
                    .Foreground(error is null ? Theme.SecondaryText : Theme.SystemCritical)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(statusText)
                    .Grid(column: 0),
                TextBlock(Str.F("sftp.editor.lines", "{0} lines", lineCount))
                    .Foreground(Theme.SecondaryText)
                    .Grid(column: 1),
                TextBlock(Str.F("sftp.editor.characters", "{0} characters", characterCount))
                    .Foreground(Theme.SecondaryText)
                    .Grid(column: 2)) with { ColumnSpacing = AppTheme.SpacingL })
            .Padding(AppTheme.SpacingL, AppTheme.SpacingS, AppTheme.SpacingL, AppTheme.SpacingM)
            .VAlign(VerticalAlignment.Center);

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            header.Grid(row: 0),
            body.Grid(row: 1),
            footer.Grid(row: 2))
            .RequestedTheme(ThemeController.ToElementTheme(settings.Theme));
    }

    private void AttachEditorScroll(TextBox editor)
    {
        void Attach()
        {
            var editorScroll = FindScrollViewer(editor);
            if (editorScroll is null)
            {
                return;
            }

            editorScroll.ViewChanged += (_, _) =>
            {
                _gutterScroll?.ChangeView(null, editorScroll.VerticalOffset, null, disableAnimation: true);
            };
        }

        if (editor.IsLoaded)
        {
            Attach();
        }
        else
        {
            editor.Loaded += (_, _) => Attach();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < children; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
