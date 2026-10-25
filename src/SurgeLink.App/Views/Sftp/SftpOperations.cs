using SurgeLink.App.Localization;
using SurgeLink.Core.Files;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// Side effect free helpers for the file manager's commands: turns the open
/// dialog into one remote call and builds transfer jobs. Keeping them here lets
/// the view stay a thin layer of state and composition.
/// </summary>
internal static class SftpOperations
{
    /// <summary>Human readable title of the action a dialog commits.</summary>
    public static string Title(SftpDialogKind kind) => kind switch
    {
        SftpDialogKind.NewFile => Str.T("sftp.operation.createFile", "Create file"),
        SftpDialogKind.NewFolder => Str.T("sftp.operation.createFolder", "Create folder"),
        SftpDialogKind.Rename => Str.T("sftp.operation.rename", "Rename"),
        SftpDialogKind.Delete => Str.T("sftp.operation.delete", "Delete"),
        SftpDialogKind.Permissions => Str.T("sftp.operation.permissions", "Permissions"),
        _ => Str.T("sftp.operation.generic", "File operation"),
    };

    /// <summary>True when the open dialog can be applied with the current input.</summary>
    public static bool CanApply(SftpDialogState state, string text) => state.Kind switch
    {
        SftpDialogKind.NewFile => IsValidName(text),
        SftpDialogKind.NewFolder => IsValidName(text),
        SftpDialogKind.Rename => state.Target is not null && IsValidName(text) && text != state.Target.Name,
        SftpDialogKind.Delete => state.DeleteTargets.Count > 0,
        SftpDialogKind.Permissions => state.Target is not null,
        _ => false,
    };

    /// <summary>A name is one POSIX path segment; spaces are preserved as part of the name.</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
        && !name.Contains('/') && !name.Contains('\0');

    /// <summary>Runs the remote call that the open dialog stands for.</summary>
    public static Task ApplyAsync(
        SftpDialogState state,
        string text,
        int mode,
        string workingDirectory,
        IRemoteFileSystem fileSystem,
        CancellationToken cancellationToken) => state.Kind switch
    {
        SftpDialogKind.NewFile => CreateFileAsync(
            fileSystem, SftpPath.Combine(workingDirectory, text), cancellationToken),
        SftpDialogKind.NewFolder => fileSystem.CreateDirectoryAsync(
            SftpPath.Combine(workingDirectory, text), cancellationToken),
        SftpDialogKind.Rename when state.Target is { } target => fileSystem.RenameAsync(
            target.FullPath, SftpPath.Combine(workingDirectory, text), cancellationToken),
        SftpDialogKind.Delete => RemoteFileDelete.DeleteManyAsync(fileSystem, state.DeleteTargets, cancellationToken),
        SftpDialogKind.Permissions when state.Target is { } target => fileSystem.SetPermissionsAsync(
            target.FullPath, mode, cancellationToken),
        _ => Task.CompletedTask,
    };

    private static async Task CreateFileAsync(
        IRemoteFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken)
    {
        await RemoteFileWrite.WriteAsync(fileSystem, path, 0, null, false,
            (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the upload job for a picked local file.</summary>
    public static TransferItem CreateUpload(string workingDirectory, string localPath, string name)
    {
        var info = new FileInfo(localPath);
        return new TransferItem
        {
            Id = Guid.NewGuid().ToString("n"),
            Direction = TransferDirection.Upload,
            Name = name,
            RemotePath = SftpPath.Combine(workingDirectory, name),
            LocalPath = localPath,
            TotalBytes = info.Exists ? info.Length : 0,
        };
    }

    /// <summary>Builds the download job for a remote entry into a local folder.</summary>
    public static TransferItem CreateDownload(RemoteFileEntry entry, string localFolder) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Direction = TransferDirection.Download,
        Name = entry.IsDirectory ? entry.Name + ".zip" : entry.Name,
        RemotePath = entry.FullPath,
        LocalPath = LocalDownloadPath.Resolve(localFolder, entry.IsDirectory ? entry.Name + ".zip" : entry.Name),
        IsDirectory = entry.IsDirectory,
        TotalBytes = entry.Size,
    };
}
