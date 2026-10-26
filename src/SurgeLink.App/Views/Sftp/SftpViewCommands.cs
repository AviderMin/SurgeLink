using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.Core.Files;
using Windows.Storage;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// The two transfer commands of the file manager. They are separate from the
/// view so the picker plumbing (which must run on the UI thread through the
/// Reactor picker hooks) is one small, readable unit; the view supplies the
/// pickers and the queue.
/// </summary>
internal static class SftpViewCommands
{
    /// <summary>Picks one local file and queues it for upload into the current folder.</summary>
    public static async Task UploadAsync(
        IRemoteFileSystem fileSystem,
        string workingDirectory,
        FileTransferQueue queue,
        Func<Task<StorageFile?>> pickFile,
        Action<SftpBanner?> setBanner,
        InteractionService interaction,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await pickFile().ConfigureAwait(true);
            if (file is null)
            {
                return;
            }

            var localPath = file.Path;
            var name = string.IsNullOrEmpty(file.Name) ? Path.GetFileName(localPath) : file.Name;
            var item = await PrepareUploadAsync(fileSystem, workingDirectory, localPath, name, false, interaction, cancellationToken).ConfigureAwait(true);
            if (item is null) { return; }
            queue.Enqueue(item, fileSystem);
            setBanner(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            setBanner(new SftpBanner(InfoBarSeverity.Error, Str.T("sftp.command.upload", "Upload"), ex.Message));
        }
    }

    /// <summary>Queues the selected folder, including nested and empty directories.</summary>
    public static async Task UploadFolderAsync(
        IRemoteFileSystem fileSystem, string workingDirectory, FileTransferQueue queue,
        Func<Task<StorageFolder?>> pickFolder, Action<SftpBanner?> setBanner,
        InteractionService interaction, CancellationToken cancellationToken)
    {
        try
        {
            var folder = await pickFolder().ConfigureAwait(true);
            if (folder is null) { return; }
            var item = await PrepareUploadAsync(fileSystem, workingDirectory, folder.Path, folder.Name, true, interaction, cancellationToken).ConfigureAwait(true);
            if (item is null) { return; }
            queue.Enqueue(item, fileSystem);
            setBanner(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            setBanner(new SftpBanner(InfoBarSeverity.Error, Str.T("sftp.command.uploadFolder", "Upload folder"), ex.Message));
        }
    }

    private static async Task<TransferItem?> PrepareUploadAsync(IRemoteFileSystem fileSystem, string directory,
        string localPath, string name, bool isDirectory, InteractionService interaction, CancellationToken token)
    {
        var destination = SftpPath.Combine(directory, name);
        var conflicts = await UploadConflicts.FindAsync(fileSystem, localPath, destination, isDirectory, token).ConfigureAwait(true);
        IReadOnlySet<string> overwrite = new HashSet<string>(StringComparer.Ordinal);
        if (conflicts.Count > 0)
        {
            var message = Str.F("sftp.upload.conflicts", "{0} existing destination(s). Choose how to handle this upload.", conflicts.Count)
                + "\n\n" + string.Join("\n", conflicts.Take(10));
            var choice = await interaction.RequestChoiceAsync(new ChoiceRequest(
                Str.T("sftp.upload.conflictTitle", "Upload conflict"), message,
                Str.T("sftp.upload.rename", "Rename upload"), Str.T("sftp.upload.overwrite", "Overwrite existing files"),
                Str.T("sftp.upload.skip", "Skip upload")), token).ConfigureAwait(true);
            if (choice == 0) { return null; }
            if (choice == 1)
            {
                var siblings = await fileSystem.ListAsync(directory, token).ConfigureAwait(true);
                var names = siblings.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
                var suffix = 1;
                var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
                var extension = isDirectory ? string.Empty : Path.GetExtension(name);
                do { destination = SftpPath.Combine(directory, stem + " (" + suffix++ + ")" + extension); }
                while (names.Contains(destination[(destination.LastIndexOf('/') + 1)..]));
            }
            else { overwrite = conflicts.ToHashSet(StringComparer.Ordinal); }
        }
        token.ThrowIfCancellationRequested();
        return new TransferItem
        {
            Id = Guid.NewGuid().ToString("n"), Direction = TransferDirection.Upload, Name = name,
            LocalPath = localPath, RemotePath = destination, IsDirectory = isDirectory, OverwritePaths = overwrite,
            TotalBytes = isDirectory ? 0 : new FileInfo(localPath).Length,
        };
    }

    /// <summary>Picks a local destination; folders download as ZIP archives.</summary>
    public static async Task DownloadAsync(
        IRemoteFileSystem fileSystem,
        IReadOnlyList<RemoteFileEntry> selected,
        FileTransferQueue queue,
        Func<Task<StorageFolder?>> pickFolder,
        Action<SftpBanner?> setBanner,
        CancellationToken cancellationToken = default)
    {
        var files = selected.ToList();
        if (files.Count == 0)
        {
            setBanner(new SftpBanner(
                InfoBarSeverity.Warning,
                Str.T("sftp.command.download", "Download"),
                Str.T("sftp.warning.selectEntries", "Select at least one file or folder to download.")));
            return;
        }

        try
        {
            var folder = await pickFolder().ConfigureAwait(true);
            if (folder is null)
            {
                return;
            }

            // The session can disconnect or the tab can close while the native
            // picker is open. The queue is already disposed in that case, so
            // do not enqueue work that can only fail.
            cancellationToken.ThrowIfCancellationRequested();
            var localFolder = folder.Path;
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                queue.Enqueue(SftpOperations.CreateDownload(entry, localFolder), fileSystem);
            }

            setBanner(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            setBanner(new SftpBanner(InfoBarSeverity.Error, Str.T("sftp.command.download", "Download"), ex.Message));
        }
    }
}
