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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

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
    // The gutter scroll viewer is created during render, so it is published
    // through a ref: a replaced element would otherwise leave the scroll
    // handler pinned to a detached control.
    private readonly Ref<ScrollViewer?> gutterRef = new(null);
    private TextBox? _editor;

    public override Element Render()
    {
        var props = Props;
        var settings = UseExternalStore(props.Settings.Subscribe, () => props.Settings.Current);
        var target = props.Target;
        var (text, setText) = UseState(string.Empty, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (saving, setSaving) = UseState(false, threadSafe: true);
        var (connected, setConnected) = UseState(props.Session is null || props.Session.State == ConnectionState.Connected, threadSafe: true);
        var (exporting, setExporting) = UseState(false);
        var (savedText, setSavedText) = UseState(string.Empty, threadSafe: true);
        var dirty = !string.Equals(text, savedText, StringComparison.Ordinal);
        var (loadFailed, setLoadFailed) = UseState(false, threadSafe: true);
        var (confirmation, setConfirmation) = UseState(0);
        // Caret offset and selected length, tracked independently of the text so
        // the editor can restore them after a re-render. Reading them from the
        // control while rendering is not an option: during a re-render the
        // control still holds the previous value. The length matters because a
        // find result must stay selected; pinning only the start would collapse
        // the match and break "find next" and "replace".
        var (caret, setCaret) = UseState(0);
        var (selectionLength, setSelectionLength) = UseState(0);
        var window = UseWindow();
        var pickers = UseMemo(() => window is null ? null : new WindowStoragePicker(window.NativeWindow), window!);
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

        // One place that turns a text change into editor state, so the caret
        // bookkeeping can never be forgotten by a caller.
        void CommitText(int caretOffset, string value, int selectedLength = 0)
        {
            setText(value);
            setCaret(caretOffset);
            setSelectionLength(selectedLength);
            setStatus(null);
        }

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
            // Record the match before re-rendering: the render callback restores
            // this selection instead of leaving a collapsed caret.
            editor.Select(index, query.Length);
            setCaret(index);
            setSelectionLength(query.Length);
            setSearchMessage(Str.T("sftp.editor.find.found", "Match selected"));
        }

        void ReplaceCurrent()
        {
            if (editorRef.Current is not { } editor || string.IsNullOrEmpty(query)) { return; }
            if (string.Equals(editor.SelectedText, query, comparison))
            {
                var start = editor.SelectionStart;
                var selected = editor.SelectionLength;
                var updated = string.Concat(editor.Text.AsSpan(0, start), replacement, editor.Text.AsSpan(start + selected));
                editor.Text = updated;
                editor.Select(start + replacement.Length, 0);
                CommitText(start + replacement.Length, updated);
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
            var clampedCaret = Math.Min(editor.SelectionStart, updated.Length);
            editor.Select(clampedCaret, 0);
            CommitText(clampedCaret, updated);
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
                        setConnected(false);
                        setLoading(false);
                        setSaving(false);
                        setError(Str.T("sftp.editor.sessionClosed", "The session has closed. Keep a copy of any unsaved text before closing this window."));
                    }
                }
                if (props.Session is { } session) { session.StateChanged += OnSessionStateChanged; }
                if (props.Session is null || props.Session.State == ConnectionState.Connected) { _ = LoadAsync(lifetime.Token); }
                else { setConnected(false); setLoading(false); setError(Str.T("sftp.editor.sessionClosed", "The session has closed. Keep a copy of any unsaved text before closing this window.")); }
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
                RemoteFileAccess.RequireRegularFile(target);
                await RemoteFileAccess.RequireRegularFileAsync(props.FileSystem, target.FullPath, cancellationToken).ConfigureAwait(true);
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
            if (!connected || loading || operationBusy.Current || loadFailed || document.Current?.Sha256 is null || lifetime.IsCancellationRequested) { return; }
            operationBusy.Current = true;
            setSaving(true);
            setError(null);
            setStatus(null);
            var snapshot = text;
            var token = lifetime.Token;
            try
            {
                var baseline = document.Current ?? throw new InvalidOperationException("No loaded document.");
                if ((long)baseline.Encoding.GetByteCount(snapshot) + baseline.Encoding.GetPreamble().Length > MaximumEditorBytes)
                    throw new IOException(Str.T("sftp.editor.tooLarge", "This file is too large for the built-in text editor."));
                var savedDocument = await RemoteTextEdit.SaveAsync(props.FileSystem, target.FullPath,
                    baseline, snapshot, MaximumEditorBytes, token).ConfigureAwait(true);
                document.Current = savedDocument;
                // Edits made while the write was in flight remain dirty.
                if (!token.IsCancellationRequested)
                {
                    setSavedText(snapshot);
                    setStatus(Str.T("sftp.editor.saved", "Saved"));
                }
            }
            catch (OperationCanceledException) { }
            catch (RemoteFileConflictException)
            {
                setError(Str.T("sftp.editor.conflict", "The remote file changed after loading. Keep your edits and reload before saving."));
            }
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
            if (loading || operationBusy.Current || (!connected && document.Current is null)) { return; }
            if (dirty) { setConfirmation(2); }
            else if (!connected) { RestoreLoadedCopy(); }
            else if (!lifetime.IsCancellationRequested) { _ = LoadAsync(lifetime.Token); }
        }

        void RestoreLoadedCopy()
        {
            if (document.Current is not { } baseline) { return; }
            setText(baseline.Text);
            setSavedText(baseline.Text);
            setLoadFailed(false);
            setStatus(Str.T("sftp.editor.restored", "Restored the last loaded copy."));
            setError(null);
        }

        void CopyAll()
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                setError(null);
                setStatus(Str.T("sftp.editor.copied", "Copied all text."));
            }
            catch (Exception exception) { setError(exception.Message); }
        }

        async Task ExportAsync()
        {
            if (loading || operationBusy.Current || pickers is null) { return; }
            operationBusy.Current = true;
            setExporting(true);
            var snapshot = text;
            var encoding = document.Current?.Encoding ?? new System.Text.UTF8Encoding(false, true);
            try
            {
                var file = await pickers.PickSaveFileAsync(Str.T("sftp.editor.textFiles", "Text files")).ConfigureAwait(true);
                if (file is null) { return; }
                await FileIO.WriteBytesAsync(file, [.. encoding.GetPreamble(), .. encoding.GetBytes(snapshot)]);
                setError(null);
                setStatus(Str.T("sftp.editor.exported", "Saved a local copy."));
            }
            catch (Exception exception) { setError(exception.Message); }
            finally { operationBusy.Current = false; setExporting(false); }
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
                else if (!connected) { RestoreLoadedCopy(); }
                else if (!lifetime.IsCancellationRequested) { _ = LoadAsync(lifetime.Token); }
            }, Str.T("sftp.editor.discard", "Discard changes"), Str.T("common.cancel", "Cancel"),
            ThemeController.ToElementTheme(settings.Theme));

        var lineCount = LineLayout.CountLines(text);
        var characterCount = text.Length;
        var lineNumbers = LineLayout.GutterText(lineCount);
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
                    Button(Str.T("sftp.editor.copyAll", "Copy all"), CopyAll).SubtleButton()
                        .AutomationName(Str.T("sftp.editor.copyAll", "Copy all"))
                        .IsEnabled(!loading && text.Length > 0),
                    Button(Str.T("sftp.editor.export", "Save local copy"), () => _ = ExportAsync()).SubtleButton()
                        .AutomationName(Str.T("sftp.editor.export", "Save local copy"))
                        .IsEnabled(!loading && !saving && !exporting),
                    Button(Icon(FontIcon("\uE721", fontSize: 16)), () => OpenSearch(false))
                        .AutomationName(Str.T("sftp.editor.find.title", "Find (Ctrl+F)"))
                        .SubtleButton().ToolTip(Str.T("sftp.editor.find.title", "Find (Ctrl+F)")),
                    Button(Str.T("sftp.editor.replace.title", "Replace"), () => OpenSearch(true))
                        .AutomationName(Str.T("sftp.editor.replace.title", "Replace")).SubtleButton(),
                    ToggleButton(Str.T("sftp.editor.wordWrap", "Word wrap"), wrap, setWrap)
                        .AutomationName(Str.T("sftp.editor.wordWrap", "Word wrap")),
                    Button(connected ? Str.T("sftp.editor.reload", "Reload") : Str.T("sftp.editor.restore", "Restore loaded copy"), Reload)
                        .SubtleButton()
                        .IsEnabled(!loading && !saving && !exporting && (connected || document.Current is not null))
                        .AutomationName(Str.T("sftp.editor.reload", "Reload")),
                    Button(Str.T("sftp.editor.save", "Save"), () => _ = SaveAsync())
                        .AccentButton()
                        .IsEnabled(connected && !loading && !saving && !exporting && !loadFailed && document.Current?.Sha256 is not null && !lifetime.IsCancellationRequested)
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
                    gutterRef.Current = viewer;
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
                                // Keep the caret and selection where the user left them:
                                // the control text is written back on every render, and a
                                // write without an explicit offset moves the caret to the
                                // end. Programmatic replacements supply their own state
                                // through CommitText instead.
                                if (editorRef.Current is { } current)
                                {
                                    setCaret(current.SelectionStart);
                                    setSelectionLength(current.SelectionLength);
                                    setText(value);
                                    setStatus(null);
                                }
                                else
                                {
                                    CommitText(caret, value);
                                }
                            })
                            // Write order matters. The TextBox descriptor applies its
                            // own AcceptsReturn/TextWrapping before Text so a single-line
                            // box cannot strip the document's line breaks; using Set for
                            // the copy constructor would defeat that ordering, so these
                            // two must stay fluent modifiers.
                            .AcceptsReturn(true)
                            .TextWrapping(wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
                            .FontFamily(AppTheme.Monospace)
                            .FontSize(14)
                            .AutomationName(Str.T("sftp.editor.contents", "File contents"))
                            .IsSpellCheckEnabled(false)
                            .HAlign(HorizontalAlignment.Stretch)
                            .VAlign(VerticalAlignment.Stretch)
                            .Set(box =>
                            {
                                editorRef.Current = box;
                                // Restore the caret and the selection after the text
                                // write: a TextBox whose Text is assigned collapses its
                                // selection and moves the caret to the end, and the
                                // element's SelectionStart cannot be applied here because
                                // the descriptor writes Text after it. A find result must
                                // survive the status-message re-render that follows it.
                                box.SelectionStart = Math.Clamp(caret, 0, box.Text.Length);
                                box.SelectionLength = Math.Clamp(selectionLength, 0, box.Text.Length - box.SelectionStart);
                                Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                                Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                                AttachEditorScroll(box);
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
                    .Grid(column: 2)) with
        { ColumnSpacing = AppTheme.SpacingL })
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
            !connected ? InfoBar(Str.T("sftp.offline.title", "Connection closed"),
                Str.T("sftp.editor.sessionClosed", "The session has closed. Keep a copy of any unsaved text before closing this window."))
                .Severity(InfoBarSeverity.Warning).IsClosable(false).Grid(row: 4)
            : error is not null && !loadFailed ? InfoBar(Str.T("sftp.editor.saveFailed", "Could not save the file"), error)
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

    /// <summary>
    /// Keeps the line-number gutter in step with the editor. The handler is
    /// attached once per control: the editor element is re-created on every
    /// render, so subscribing unconditionally would stack a new handler on each
    /// keystroke.
    /// </summary>
    private void AttachEditorScroll(TextBox editor)
    {
        if (ReferenceEquals(_editor, editor)) { return; }
        _editor = editor;

        void Attach()
        {
            if (FindScrollViewer(editor) is not { } editorScroll) { return; }
            editorScroll.ViewChanged += (_, _) =>
            {
                gutterRef.Current?.ChangeView(null, editorScroll.VerticalOffset, null, disableAnimation: true);
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
