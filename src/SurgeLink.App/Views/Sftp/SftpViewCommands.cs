using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
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
        Action<SftpBanner?> setBanner)
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
            queue.Enqueue(SftpOperations.CreateUpload(workingDirectory, localPath, name), fileSystem);
            setBanner(null);
        }
        catch (Exception ex)
        {
            setBanner(new SftpBanner(InfoBarSeverity.Error, Str.T("sftp.command.upload", "Upload"), ex.Message));
        }
    }

    /// <summary>Queues the selected folder, including nested and empty directories.</summary>
    public static async Task UploadFolderAsync(
        IRemoteFileSystem fileSystem, string workingDirectory, FileTransferQueue queue,
        Func<Task<StorageFolder?>> pickFolder, Action<SftpBanner?> setBanner)
    {
        try
        {
            var folder = await pickFolder().ConfigureAwait(true);
            if (folder is null) { return; }
            queue.Enqueue(new TransferItem
            {
                Id = Guid.NewGuid().ToString("n"),
                Direction = TransferDirection.Upload,
                Name = folder.Name,
                LocalPath = folder.Path,
                RemotePath = SftpPath.Combine(workingDirectory, folder.Name),
                IsDirectory = true,
            }, fileSystem);
            setBanner(null);
        }
        catch (Exception ex)
        {
            setBanner(new SftpBanner(InfoBarSeverity.Error, Str.T("sftp.command.uploadFolder", "Upload folder"), ex.Message));
        }
    }

    /// <summary>Picks a local destination; folders download as ZIP archives.</summary>
    public static async Task DownloadAsync(
        IRemoteFileSystem fileSystem,
        IReadOnlyList<RemoteFileEntry> selected,
        FileTransferQueue queue,
        Func<Task<StorageFolder?>> pickFolder,
        Action<SftpBanner?> setBanner)
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

            foreach (var entry in files)
            {
                queue.Enqueue(SftpOperations.CreateDownload(entry, folder.Path), fileSystem);
            }

            setBanner(null);
        }
        catch (Exception ex)
        {
            setBanner(new SftpBanner(InfoBarSeverity.Error, Str.T("sftp.command.download", "Download"), ex.Message));
        }
    }
}
