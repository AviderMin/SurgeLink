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
using SurgeLink.Core.Sessions;
using SurgeLink.App.Shell;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Props for the standalone text editor window.</summary>
internal sealed record FileEditorWindowProps(
    IRemoteFileSystem FileSystem,
    RemoteFileEntry Target,
    IAppSettingsStore Settings,
    IProtocolSession? Session = null);

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
        var (savedText, setSavedText) = UseState(string.Empty, threadSafe: true);
        var dirty = !string.Equals(text, savedText, StringComparison.Ordinal);
        var (loadFailed, setLoadFailed) = UseState(false, threadSafe: true);
        var (confirmation, setConfirmation) = UseState(0);
        var window = UseWindow();
        var allowClose = UseRef(false);
        var operationBusy = UseRef(false);
        var lifetime = UseMemo(() => new CancellationTokenSource(), props.FileSystem, target.FullPath);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (status, setStatus) = UseState<string?>(null, threadSafe: true);
        var (wrap, setWrap) = UseState(true);
        UseClosingGuard(() =>
        {
            if (operationBusy.Current)
            {
                setStatus(Str.T("sftp.editor.waitForSave", "Wait for the current operation to finish before closing."));
                return false;
            }
            if (dirty && !allowClose.Current) { setConfirmation(1); return false; }
            return true;
        });
        var lineNumberTransform = UseMemo(() => new TranslateTransform());

        UseEffect(
            () =>
            {
                void OnSessionStateChanged(object? _, SessionStateChangedEventArgs args)
                {
                    if (args.NewState is ConnectionState.Disconnected or ConnectionState.Disconnecting or ConnectionState.Faulted)
                    {
                        lifetime.Cancel();
                        setError(Str.T("sftp.editor.sessionClosed", "The session has closed. Keep a copy of any unsaved text before closing this window."));
                    }
                }
                if (props.Session is { } session) { session.StateChanged += OnSessionStateChanged; }
                _ = LoadAsync(lifetime.Token);
                return () =>
                {
                    if (props.Session is { } activeSession) { activeSession.StateChanged -= OnSessionStateChanged; }
                    lifetime.Cancel();
                    lifetime.Dispose();
                };
            },
            lifetime);

        async Task LoadAsync(CancellationToken cancellationToken)
        {
            setLoading(true);
            setLoadFailed(false);
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
                var content = await BoundedTextFile.ReadAsync(stream, MaximumEditorBytes, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                setText(content);
                setSavedText(content);
                setLoading(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                setError(exception.Message);
                setLoadFailed(true);
                setLoading(false);
            }
        }

        async Task SaveAsync()
        {
            if (loading || operationBusy.Current || loadFailed || lifetime.IsCancellationRequested) { return; }
            operationBusy.Current = true;
            setSaving(true);
            setError(null);
            setStatus(null);
            var snapshot = text;
            var token = lifetime.Token;
            try
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(snapshot);
                if (bytes.Length > MaximumEditorBytes) { throw new IOException(Str.T("sftp.editor.tooLarge", "This file is too large for the built-in text editor.")); }
                await RemoteFileWrite.WriteAsync(props.FileSystem, target.FullPath, bytes.Length, null, true,
                    (stream, ct) => stream.WriteAsync(bytes, ct).AsTask(), token).ConfigureAwait(true);
                // Edits made while the write was in flight remain dirty.
                if (!token.IsCancellationRequested)
                {
                    setSavedText(snapshot);
                    setStatus(Str.T("sftp.editor.saved", "Saved"));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested) { setError(exception.Message); }
            }
            finally
            {
                operationBusy.Current = false;
                if (!token.IsCancellationRequested) { setSaving(false); }
            }
        }

        void Reload()
        {
            if (loading || operationBusy.Current || lifetime.IsCancellationRequested) { return; }
            if (dirty) { setConfirmation(2); }
            else { _ = LoadAsync(lifetime.Token); }
        }

        Element? confirmationDialog = confirmation == 0 ? null : AppDialog.Render(
            Str.T("sftp.editor.discardTitle", "Discard unsaved changes?"),
            TextBlock(Str.T("sftp.editor.discardBody", "Your unsaved changes will be lost. Continue?"))
                .TextWrapping(TextWrapping.Wrap),
            "EditorDiscard", result =>
            {
                var action = confirmation;
                setConfirmation(0);
                if (result != ContentDialogResult.Primary) { return; }
                if (action == 1) { allowClose.Current = true; window?.Close(); }
                else if (!lifetime.IsCancellationRequested) { _ = LoadAsync(lifetime.Token); }
            }, Str.T("sftp.editor.discard", "Discard changes"), Str.T("common.cancel", "Cancel"),
            ThemeController.ToElementTheme(settings.Theme));

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
                        .IsEnabled(!loading && !saving && !loadFailed && !lifetime.IsCancellationRequested)
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
        else if (loadFailed)
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
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
            header.Grid(row: 0),
            body.Grid(row: 1),
            error is not null && !loadFailed ? InfoBar(Str.T("sftp.editor.saveFailed", "Could not save the file"), error)
                .Severity(InfoBarSeverity.Error).IsClosable(false).Grid(row: 2) : null,
            footer.Grid(row: 3),
            confirmationDialog?.Grid(row: 0, rowSpan: 4))
            .Background(Theme.LayerFill)
            .RequestedTheme(ThemeController.ToElementTheme(settings.Theme))
            .Set(root =>
            {
                // The editor owns a separate window. Theme the host as well as
                // the editor subtree so the window backdrop does not stay light.
                var theme = ThemeController.ToElementTheme(settings.Theme);
                root.RequestedTheme = theme;
                if (root.XamlRoot?.Content is FrameworkElement windowRoot)
                {
                    windowRoot.RequestedTheme = theme;
                }
            });
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
