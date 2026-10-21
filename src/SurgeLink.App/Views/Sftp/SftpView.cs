using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// The SFTP file manager: breadcrumb navigation, a sortable file table, file
/// operations and a sequential transfer queue with progress and cancellation.
/// All state lives in hooks; the remote calls run on the thread pool and push
/// results back through thread safe state setters.
/// </summary>
internal sealed class SftpView : Component<SftpViewProps>
{
    private static readonly object[] NoDependencies = Array.Empty<object>();

    public override Element Render()
    {
        var fileSystem = Props.FileSystem;
        var settingsStore = Props.Settings;
        var queue = Props.Transfers;

        // Hooks: constant order and count on every render.
        var settings = UseExternalStore(settingsStore.Subscribe, () => settingsStore.Current);

        var (requestedPath, setRequestedPath) = UseState<string?>(Props.InitialPath);
        var (cwd, setCwd) = UseState(Props.InitialPath ?? "/", threadSafe: true);
        var (entries, setEntries) = UseState<IReadOnlyList<RemoteFileEntry>>(Array.Empty<RemoteFileEntry>(), threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (refreshToken, setRefreshToken) = UseState(Guid.Empty);
        var (sortColumn, setSortColumn) = UseState(SftpSortColumn.Name);
        var (ascending, setAscending) = UseState(true);
        var (selected, setSelected) = UseState<IReadOnlyList<RemoteFileEntry>>(Array.Empty<RemoteFileEntry>(), threadSafe: true);
        var (dialog, setDialog) = UseState(SftpDialogState.None);
        var (dialogText, setDialogText) = UseState(string.Empty);
        var (dialogMode, setDialogMode) = UseState(0);
        var (banner, setBanner) = UseState<SftpBanner?>(null, threadSafe: true);

        // The queue belongs to the session tab, not to this component: switching
        // to Monitor/Terminal (or rebuilding the tree for a language change)
        // unmounts the view while transfers keep running. The snapshot is the
        // only thing the view owns; cancel and clear go straight to the queue.
        var transfers = UseExternalStore(queue.Subscribe, () => queue.Snapshot);

        // Ordinary file operations are view-scoped work: cancel them when this
        // view unmounts so a dialog command cannot outlive its list.
        var operationLifetime = UseRef<CancellationTokenSource?>(null);
        // ItemClick is the reliable input path for a WinUI ListView; the
        // platform's DoubleTapped event is swallowed by the item container.
        // Track two clicks on the same row to preserve Explorer-style opening.
        var lastClick = UseRef<(string Path, DateTime At)>((string.Empty, DateTime.MinValue));
        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                operationLifetime.Current = cancellation;
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                    if (ReferenceEquals(operationLifetime.Current, cancellation))
                    {
                        operationLifetime.Current = null;
                    }
                };
            },
            NoDependencies);

        UseEffect(
            () =>
            {
                if (fileSystem is null)
                {
                    setLoading(false);
                    return () => { };
                }

                var cancellation = new CancellationTokenSource();
                var target = requestedPath;
                setLoading(true);
                setSelected(Array.Empty<RemoteFileEntry>());
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var path = string.IsNullOrEmpty(target)
                            ? await fileSystem.GetHomeDirectoryAsync(cancellation.Token).ConfigureAwait(false)
                            : target!;
                        var listing = await fileSystem.ListAsync(path, cancellation.Token).ConfigureAwait(false);
                        if (cancellation.IsCancellationRequested)
                        {
                            return;
                        }

                        setCwd(path);
                        Props.PathChanged?.Invoke(path);
                        setEntries(listing);
                        setError(null);
                        setLoading(false);
                        setSelected(Array.Empty<RemoteFileEntry>());
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            return;
                        }

                        setError(ex.Message);
                        setLoading(false);
                    }
                });

                return () => cancellation.Cancel();
            },
            fileSystem,
            requestedPath,
            refreshToken);

        // A completion banner is view-local chrome; it is subscribed only while
        // this view is mounted, so a transfer that ends while the user is on
        // another tab still lands in the queue snapshot without touching an
        // unmounted component's state.
        UseEffect(
            () =>
            {
                void OnFinished(TransferItem item) => setBanner(item.Status switch
                {
                    TransferStatus.Completed => new SftpBanner(
                        InfoBarSeverity.Success,
                        item.Direction == TransferDirection.Upload
                            ? Str.T("sftp.banner.uploadComplete", "Upload complete")
                            : Str.T("sftp.banner.downloadComplete", "Download complete"),
                        item.Name + " · " + DataFormat.Bytes(item.DoneBytes)),
                    TransferStatus.Failed => new SftpBanner(
                        InfoBarSeverity.Error,
                        Str.T("sftp.banner.transferFailed", "Transfer failed"),
                        item.Name + " · " + (item.Error ?? Str.T("sftp.transfers.unknownError", "unknown error"))),
                    _ => null,
                });

                queue.TransferFinished += OnFinished;
                return () => queue.TransferFinished -= OnFinished;
            },
            queue);

        var sortKey = ((int)sortColumn) + "|" + (ascending ? "up" : "down") + "|" + (settings.ShowHiddenFiles ? "hidden" : "visible");
        var rows = UseMemo(
            () => SftpContent.BuildRows(entries, settings.ShowHiddenFiles, sortColumn, ascending),
            entries,
            sortKey);

        void Refresh()
        {
            setLoading(true);
            setSelected(Array.Empty<RemoteFileEntry>());
            setRefreshToken(Guid.NewGuid());
        }

        void CloseDialog()
        {
            setDialog(SftpDialogState.None);
            setDialogText(string.Empty);
        }

        void ShowBanner(InfoBarSeverity severity, string title, string message) =>
            setBanner(new SftpBanner(severity, title, message));

        void Navigate(string path)
        {
            var normalized = SftpPath.Normalize(path);
            if (normalized == cwd && error is null && !loading)
            {
                return;
            }

            setRequestedPath(normalized);
            Refresh();
        }

        void GoHome()
        {
            // A null request means "ask the protocol for the home directory" and
            // is how the very first load resolves it too.
            setRequestedPath(null);
            Refresh();
        }

        void SortBy(SftpSortColumn column)
        {
            if (sortColumn == column)
            {
                setAscending(!ascending);
            }
            else
            {
                setSortColumn(column);
                setAscending(true);
            }
        }

        void OpenEntry(RemoteFileEntry entry)
        {
            if (entry.IsDirectory)
            {
                Navigate(entry.FullPath);
            }
        }

        void HandleItemClick(RemoteFileEntry entry)
        {
            var now = DateTime.UtcNow;
            var previous = lastClick.Current;
            if (previous.Path == entry.FullPath &&
                now - previous.At <= TimeSpan.FromMilliseconds(500))
            {
                lastClick.Current = (string.Empty, DateTime.MinValue);
                OpenEntry(entry);
            }
            else
            {
                lastClick.Current = (entry.FullPath, now);
            }
        }

        void OpenEditor()
        {
            if (fileSystem is null || selected.Count != 1 || selected[0].IsDirectory)
            {
                return;
            }

            var target = selected[0];
            ReactorApp.OpenWindow(
                new WindowSpec
                {
                    Title = Str.F("sftp.editor.windowTitle", "{0} — Text editor", target.Name),
                    Width = 1000,
                    Height = 720,
                    MinWidth = 640,
                    MinHeight = 480,
                    StartPosition = WindowStartPosition.CenterOnPrimary,
                    Backdrop = BackdropChoice.Of(BackdropKind.MicaAlt),
                },
                _ => Component<FileEditorWindow, FileEditorWindowProps>(
                    new FileEditorWindowProps(fileSystem, target, settingsStore)));
        }

        void OpenRename(RemoteFileEntry? entry)
        {
            if (entry is null)
            {
                return;
            }

            setDialogText(entry.Name);
            setDialog(new SftpDialogState(SftpDialogKind.Rename, entry));
        }

        void OpenDelete(RemoteFileEntry? entry)
        {
            if (entry is null)
            {
                return;
            }

            setDialog(new SftpDialogState(SftpDialogKind.Delete, entry));
        }

        void OpenPermissions(RemoteFileEntry? entry)
        {
            if (entry is null || fileSystem is not { SupportsPermissions: true })
            {
                return;
            }

            setDialogMode(entry.UnixMode ?? 0x1A4);
            setDialog(new SftpDialogState(SftpDialogKind.Permissions, entry));
        }

        async Task ExecuteAsync(string title, Func<IRemoteFileSystem, CancellationToken, Task> operation)
        {
            if (fileSystem is null)
            {
                return;
            }

            var token = operationLifetime.Current?.Token ?? CancellationToken.None;
            try
            {
                await operation(fileSystem, token).ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                {
                    Refresh();
                }
            }
            catch (OperationCanceledException)
            {
                // Leaving the view (or closing the session) cancels ordinary
                // file operations; that is not an error the user should see.
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    ShowBanner(InfoBarSeverity.Error, title, ex.Message);
                }
            }
        }

        void CommitDialog()
        {
            var current = dialog;
            if (!SftpOperations.CanApply(current, dialogText))
            {
                CloseDialog();
                return;
            }

            // The native dialog has closed. Clear its controlled open state now,
            // before a remote call yields and another render can reopen it.
            var text = dialogText;
            var mode = dialogMode;
            var directory = cwd;
            CloseDialog();
            _ = ExecuteAsync(
                SftpOperations.Title(current.Kind),
                (fs, token) => SftpOperations.ApplyAsync(current, text, mode, directory, fs, token));
        }

        // The commands are only reachable while the toolbar is rendered, which
        // happens after the null check below, so the file system exists.
        // The picker "hooks" only open a dialog and return a task (no hook state
        // is registered), which is the documented Reactor usage for pickers, so
        // the hook ordering analyzer is silenced for these two call sites.
        void Upload()
        {
#pragma warning disable REACTOR_HOOKS_001
            _ = SftpViewCommands.UploadAsync(
                fileSystem!, cwd, queue, () => UseFilePickerAsync(new FilePickerOptions()), setBanner);
#pragma warning restore REACTOR_HOOKS_001
        }

        void Download()
        {
#pragma warning disable REACTOR_HOOKS_001
            _ = SftpViewCommands.DownloadAsync(
                fileSystem!, selected, queue, () => UseFolderPickerAsync(new FolderPickerOptions()), setBanner);
#pragma warning restore REACTOR_HOOKS_001
        }

        void HandleListKey(object sender, KeyRoutedEventArgs args)
        {
            var list = sender as ListView;
            var current = list?.SelectedItems.Count == 1 ? list.SelectedItem as RemoteFileEntry : null;
            switch (args.Key)
            {
                case VirtualKey.Enter:
                    if (current is not null)
                    {
                        OpenEntry(current);
                    }

                    args.Handled = true;
                    break;
                case VirtualKey.Back:
                    Navigate(SftpPath.Parent(cwd));
                    args.Handled = true;
                    break;
                case VirtualKey.F2:
                    OpenRename(current);
                    args.Handled = true;
                    break;
                case VirtualKey.Delete:
                    OpenDelete(current);
                    args.Handled = true;
                    break;
                case VirtualKey.F5:
                    Refresh();
                    args.Handled = true;
                    break;
            }
        }

        if (fileSystem is null)
        {
            return SftpContent.Unavailable();
        }

        var toolbar = SftpToolbar.Render(new SftpToolbarProps(
            cwd,
            cwd != "/",
            !loading && error is null && selected.Any(entry => !entry.IsDirectory),
            !loading && error is null,
            !loading && error is null && selected.Count == 1,
            fileSystem.SupportsPermissions,
            !loading && error is null && selected.Count == 1 && !selected[0].IsDirectory,
            Navigate,
            () => Navigate(SftpPath.Parent(cwd)),
            GoHome,
            Refresh,
            () =>
            {
                setDialogText(string.Empty);
                setDialog(new SftpDialogState(SftpDialogKind.NewFile));
            },
            () =>
            {
                setDialogText(string.Empty);
                setDialog(new SftpDialogState(SftpDialogKind.NewFolder));
            },
            Upload,
            Download,
            () => OpenRename(selected.Count == 1 ? selected[0] : null),
            () => OpenDelete(selected.Count == 1 ? selected[0] : null),
            () => OpenPermissions(selected.Count == 1 ? selected[0] : null),
            OpenEditor));

        Element? bannerBar = banner is null
            ? null
            : InfoBar(banner.Title, banner.Message)
                .Severity(banner.Severity)
                .IsClosable(true)
                .Closed(() => setBanner(null));

        var content = SftpContent.Build(rows, loading, error, setSelected, HandleItemClick, HandleListKey, Refresh, setDialogText, setDialog);

        var transferPanel = SftpTransferPanel.Render(transfers, id => queue.Cancel(id), queue.ClearFinished);

        var dialogs = new List<Element>();
        if (dialog.Kind == SftpDialogKind.NewFile)
        {
            dialogs.Add(SftpDialogs.NewFile(true, dialogText, setDialogText, CommitDialog, CloseDialog));
        }
        else if (dialog.Kind == SftpDialogKind.NewFolder)
        {
            dialogs.Add(SftpDialogs.NewFolder(true, dialogText, setDialogText, CommitDialog, CloseDialog));
        }
        else if (dialog.Target is { } target)
        {
            dialogs.Add(dialog.Kind switch
            {
                SftpDialogKind.Rename => SftpDialogs.Rename(true, target, dialogText, setDialogText, CommitDialog, CloseDialog),
                SftpDialogKind.Delete => SftpDialogs.Delete(true, target, CommitDialog, CloseDialog),
                SftpDialogKind.Permissions => SftpDialogs.Permissions(true, target, dialogMode, setDialogMode, CommitDialog, CloseDialog),
                _ => Empty(),
            });
        }


        return Grid(
            columns: [GridSize.Star()],
            rows:
            [
                GridSize.Auto,
                GridSize.Auto,
                GridSize.Star(),
                GridSize.Auto,
            ],
            toolbar.Grid(row: 0),
            bannerBar is null ? null : bannerBar.Grid(row: 1),
            // The ListView owns vertical scrolling. Keep the header and rows in
            // the same star-sized Grid so the list gets the viewport height.
            Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Auto, GridSize.Star()],
                    SftpEntryList.Header(sortColumn, ascending, SortBy).Grid(row: 0),
                    content.Grid(row: 1))
                .MinWidth(SftpEntryList.MinimumWidth)
                .Grid(row: 2),
            transferPanel is null ? null : transferPanel.Grid(row: 3),
            dialogs.Count == 0 ? null : Grid(columns: [GridSize.Star()], rows: [GridSize.Star()], dialogs.ToArray()).Grid(row: 0, rowSpan: 4));
    }
}
