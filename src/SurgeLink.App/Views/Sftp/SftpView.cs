using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SurgeLink.App.Components;
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
    private SftpTransferQueue? _queue;
    private Action<IReadOnlyList<TransferItem>>? _publishTransfers;
    private Action<TransferItem>? _finishTransfer;

    public override Element Render()
    {
        var fileSystem = Props.FileSystem;
        var settingsStore = Props.Settings;

        // Hooks: constant order and count on every render.
        var (settings, setSettings) = UseState(settingsStore.Current);
        UseEffect(
            () =>
            {
                setSettings(settingsStore.Current);
                return settingsStore.Subscribe(() => setSettings(settingsStore.Current));
            },
            settingsStore);

        var (requestedPath, setRequestedPath) = UseState<string?>(null);
        var (cwd, setCwd) = UseState("/");
        var (entries, setEntries) = UseState<IReadOnlyList<RemoteFileEntry>>(Array.Empty<RemoteFileEntry>(), threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (refreshToken, setRefreshToken) = UseState(0);
        var (sortColumn, setSortColumn) = UseState(SftpSortColumn.Name);
        var (ascending, setAscending) = UseState(true);
        var (selected, setSelected) = UseState<IReadOnlyList<RemoteFileEntry>>(Array.Empty<RemoteFileEntry>());
        var (dialog, setDialog) = UseState(SftpDialogState.None);
        var (dialogText, setDialogText) = UseState(string.Empty);
        var (dialogMode, setDialogMode) = UseState(0);
        var (banner, setBanner) = UseState<SftpBanner?>(null, threadSafe: true);
        var (transfers, setTransfers) = UseState<IReadOnlyList<TransferItem>>(Array.Empty<TransferItem>(), threadSafe: true);

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

        // The queue publishes through fields that every render refreshes, so a
        // long running transfer always talks to the current state setters.
        _publishTransfers = setTransfers;
        _finishTransfer = item => setBanner(item.Status switch
        {
            TransferStatus.Completed => new SftpBanner(
                InfoBarSeverity.Success,
                item.Direction == TransferDirection.Upload ? "Upload complete" : "Download complete",
                item.Name + " · " + DataFormat.Bytes(item.DoneBytes)),
            TransferStatus.Failed => new SftpBanner(
                InfoBarSeverity.Error,
                "Transfer failed",
                item.Name + " · " + (item.Error ?? "unknown error")),
            _ => null,
        });
        _queue ??= new SftpTransferQueue(
            items => _publishTransfers?.Invoke(items),
            item => _finishTransfer?.Invoke(item));
        var queue = _queue;

        var sortKey = ((int)sortColumn) + "|" + (ascending ? "up" : "down") + "|" + (settings.ShowHiddenFiles ? "hidden" : "visible");
        var rows = UseMemo(
            () => SftpContent.BuildRows(entries, settings.ShowHiddenFiles, sortColumn, ascending),
            entries,
            sortKey);

        void Refresh() => setRefreshToken(refreshToken + 1);

        void CloseDialog()
        {
            setDialog(SftpDialogState.None);
            setDialogText(string.Empty);
        }

        void ShowBanner(InfoBarSeverity severity, string title, string message) =>
            setBanner(new SftpBanner(severity, title, message));

        void Navigate(string path)
        {
            setRequestedPath(SftpPath.Normalize(path));
            setSelected(Array.Empty<RemoteFileEntry>());
        }

        void GoHome()
        {
            // A null request means "ask the protocol for the home directory" and
            // is how the very first load resolves it too.
            setRequestedPath(null);
            setSelected(Array.Empty<RemoteFileEntry>());
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

            try
            {
                await operation(fileSystem, CancellationToken.None).ConfigureAwait(true);
                CloseDialog();
                Refresh();
            }
            catch (Exception ex)
            {
                ShowBanner(InfoBarSeverity.Error, title, ex.Message);
                CloseDialog();
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

            _ = ExecuteAsync(
                SftpOperations.Title(current.Kind),
                (fs, token) => SftpOperations.ApplyAsync(current, dialogText, dialogMode, cwd, fs, token));
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
            var current = (sender as ListView)?.SelectedItem as RemoteFileEntry;
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
            selected.Count > 0,
            selected.Count == 1,
            fileSystem.SupportsPermissions,
            settings.ShowHiddenFiles,
            Navigate,
            () => Navigate(SftpPath.Parent(cwd)),
            GoHome,
            Refresh,
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
            value => settingsStore.Update(current => current with { ShowHiddenFiles = value })));

        Element? bannerBar = banner is null
            ? null
            : InfoBar(banner.Title, banner.Message)
                .Severity(banner.Severity)
                .IsClosable(true)
                .Closed(() => setBanner(null));

        var content = SftpContent.Build(rows, loading, error, setSelected, OpenEntry, HandleListKey, Refresh, setDialogText, setDialog);

        var transferPanel = SftpTransferPanel.Render(transfers, id => queue.Cancel(id), queue.ClearFinished);

        var dialogs = new List<Element>();
        if (dialog.Kind == SftpDialogKind.NewFolder)
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
                GridSize.Auto,
                GridSize.Star(),
                GridSize.Auto,
            ],
            toolbar.Grid(row: 0),
            bannerBar is null ? null : bannerBar.Grid(row: 1),
            SftpEntryList.Header(sortColumn, ascending, SortBy).Grid(row: 2),
            content.Grid(row: 3),
            transferPanel is null ? null : transferPanel.Grid(row: 4),
            dialogs.Count == 0 ? null : Grid(columns: [GridSize.Star()], rows: [GridSize.Star()], dialogs.ToArray()).Grid(row: 0, rowSpan: 5));
    }
}
