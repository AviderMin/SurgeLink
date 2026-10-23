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
        var document = UseRef<TextFileDocument?>(null);
        var lifetime = UseMemo(() => new CancellationTokenSource(), props.FileSystem, target.FullPath);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (status, setStatus) = UseState<string?>(null, threadSafe: true);
        var (wrap, setWrap) = UseState(true);
        var (searchOpen, setSearchOpen) = UseState(false);
        var (replaceOpen, setReplaceOpen) = UseState(false);
        var (query, setQuery) = UseState(string.Empty);
        var (replacement, setReplacement) = UseState(string.Empty);
        var (matchCase, setMatchCase) = UseState(false);
        var (searchMessage, setSearchMessage) = UseState(string.Empty);
        var editorRef = UseRef<TextBox?>(null);
        var focusSearch = UseRef(false);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        void OpenSearch(bool replace)
        {
            if (editorRef.Current is { SelectionLength: > 0 } editor && !editor.SelectedText.Contains('\n'))
            {
                setQuery(editor.SelectedText);
            }
            focusSearch.Current = true;
            setReplaceOpen(replace);
            setSearchOpen(true);
        }

        void FindMatch(bool previous = false)
        {
            if (editorRef.Current is not { } editor || string.IsNullOrEmpty(query)) { return; }
            var source = editor.Text;
            var start = previous ? editor.SelectionStart - 1 : editor.SelectionStart + editor.SelectionLength;
            var index = LiteralTextSearch.Find(source, query, start, previous, matchCase);
            if (index < 0)
            {
                setSearchMessage(Str.T("sftp.editor.find.none", "No matches"));
                return;
            }
            editor.Select(index, query.Length);
            setSearchMessage(Str.T("sftp.editor.find.found", "Match selected"));
        }

        void ReplaceCurrent()
        {
            if (editorRef.Current is not { } editor || string.IsNullOrEmpty(query)) { return; }
            if (string.Equals(editor.SelectedText, query, comparison))
            {
                editor.SelectedText = replacement;
                setText(editor.Text);
                setStatus(null);
            }
            FindMatch();
        }

        void ReplaceAll()
        {
            if (editorRef.Current is not { } editor || string.IsNullOrEmpty(query)) { return; }
            var source = editor.Text;
            var updated = source.Replace(query, replacement, comparison);
            if (source == updated)
            {
                setSearchMessage(Str.T("sftp.editor.find.none", "No matches"));
                return;
            }
            editor.Text = updated;
            setText(updated);
            setStatus(null);
            setSearchMessage(Str.T("sftp.editor.find.replaced", "All matches replaced"));
        }

        void HandleEditorKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
        {
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (control && args.Key == Windows.System.VirtualKey.F) { OpenSearch(false); }
            else if (control && args.Key == Windows.System.VirtualKey.H) { OpenSearch(true); }
            else if (args.Key == Windows.System.VirtualKey.F3) { FindMatch(shift); }
            else if (args.Key == Windows.System.VirtualKey.Escape && searchOpen)
            {
                setSearchOpen(false);
                editorRef.Current?.Focus(FocusState.Programmatic);
            }
            else { return; }
            args.Handled = true;
        }

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
                        setLoading(false);
                        setSaving(false);
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
                var content = await BoundedTextFile.ReadDocumentAsync(stream, MaximumEditorBytes, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                document.Current = content;
                setText(content.Text);
                setSavedText(content.Text);
            }
            catch (OperationCanceledException)
            {
                setLoadFailed(true);
            }
            catch (Exception exception)
            {
                setError(exception.Message);
                setLoadFailed(true);
            }
            finally
            {
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
                var bytes = (document.Current ?? throw new InvalidOperationException("No loaded document.")).Encode(snapshot);
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
                setSaving(false);
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
                    Button(Icon(FontIcon("\uE721", fontSize: 16)), () => OpenSearch(false))
                        .AutomationName(Str.T("sftp.editor.find.title", "Find (Ctrl+F)"))
                        .SubtleButton().ToolTip(Str.T("sftp.editor.find.title", "Find (Ctrl+F)")),
                    Button(Str.T("sftp.editor.replace.title", "Replace"), () => OpenSearch(true))
                        .AutomationName(Str.T("sftp.editor.replace.title", "Replace")).SubtleButton(),
                    ToggleButton(Str.T("sftp.editor.wordWrap", "Word wrap"), wrap, setWrap)
                        .AutomationName(Str.T("sftp.editor.wordWrap", "Word wrap")),
                    Button(Str.T("sftp.editor.reload", "Reload"), Reload)
                        .SubtleButton()
                        .IsEnabled(!loading && !saving && !lifetime.IsCancellationRequested)
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
                                editorRef.Current = box;
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

        Element? searchBar = searchOpen ? Border(
            VStack(AppTheme.SpacingS,
                Grid(
                    columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                    rows: [GridSize.Auto],
                    TextBox(Optional<string>.Of(query), value => { setQuery(value); setSearchMessage(string.Empty); },
                            placeholderText: Str.T("sftp.editor.find.placeholder", "Find in document"))
                        .AutomationName(Str.T("sftp.editor.find.placeholder", "Find in document"))
                        .Set(box =>
                        {
                            if (focusSearch.Current)
                            {
                                focusSearch.Current = false;
                                box.DispatcherQueue.TryEnqueue(() => { box.Focus(FocusState.Programmatic); box.SelectAll(); });
                            }
                        })
                        .OnKeyDown((_, args) =>
                        {
                            if (args.Key == Windows.System.VirtualKey.Enter) { FindMatch(); args.Handled = true; }
                        }).Grid(column: 0),
                    ToggleButton("Aa", matchCase, setMatchCase)
                        .ToolTip(Str.T("sftp.editor.find.case", "Match case"))
                        .AutomationName(Str.T("sftp.editor.find.case", "Match case")).Grid(column: 1),
                    Button(Icon(FontIcon("\uE70E", fontSize: 14)), () => FindMatch(true))
                        .AutomationName(Str.T("sftp.editor.find.previous", "Previous match (Shift+F3)"))
                        .SubtleButton()
                        .IsEnabled(query.Length > 0).Grid(column: 2),
                    Button(Icon(FontIcon("\uE70D", fontSize: 14)), () => FindMatch())
                        .AutomationName(Str.T("sftp.editor.find.next", "Next match (F3)"))
                        .SubtleButton()
                        .IsEnabled(query.Length > 0).Grid(column: 3),
                    Button(Icon(FontIcon("\uE711", fontSize: 14)), () =>
                        { setSearchOpen(false); editorRef.Current?.Focus(FocusState.Programmatic); }).SubtleButton()
                        .AutomationName(Str.T("sftp.editor.find.close", "Close find (Esc)")).Grid(column: 4))
                    .Set(grid => grid.ColumnSpacing = AppTheme.SpacingS),
                replaceOpen ? Grid(
                    columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
                    TextBox(Optional<string>.Of(replacement), setReplacement,
                            placeholderText: Str.T("sftp.editor.replace.placeholder", "Replace with"))
                        .AutomationName(Str.T("sftp.editor.replace.placeholder", "Replace with")).Grid(column: 0),
                    Button(Str.T("sftp.editor.replace.title", "Replace"), ReplaceCurrent)
                        .AutomationName(Str.T("sftp.editor.replace.title", "Replace"))
                        .IsEnabled(query.Length > 0 && !loading && !loadFailed).Grid(column: 1),
                    Button(Str.T("sftp.editor.replace.all", "Replace all"), ReplaceAll)
                        .AutomationName(Str.T("sftp.editor.replace.all", "Replace all"))
                        .IsEnabled(query.Length > 0 && !loading && !loadFailed).Grid(column: 2))
                    .Set(grid => grid.ColumnSpacing = AppTheme.SpacingS) : null,
                searchMessage.Length == 0 ? null : Caption(searchMessage).Foreground(Theme.SecondaryText)))
            .Padding(AppTheme.SpacingL, AppTheme.SpacingS)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke)
            .WithKey("editor-search") : null;

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
            rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
            TitleBar(Str.F("sftp.editor.windowTitle", "{0} — Text editor", target.Name))
                .Icon(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png")).AbsoluteUri)
                .Grid(row: 0),
            header.Grid(row: 1),
            searchBar?.Grid(row: 2),
            body.WithKey("editor-body").Grid(row: 3),
            error is not null && !loadFailed ? InfoBar(Str.T("sftp.editor.saveFailed", "Could not save the file"), error)
                .Severity(InfoBarSeverity.Error).IsClosable(false).Grid(row: 4) : null,
            footer.Grid(row: 5),
            confirmationDialog?.Grid(row: 0, rowSpan: 6))
            .OnKeyDown(HandleEditorKey)
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
