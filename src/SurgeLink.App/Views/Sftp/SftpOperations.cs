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
        SftpDialogKind.NewFolder => "Create folder",
        SftpDialogKind.Rename => "Rename",
        SftpDialogKind.Delete => "Delete",
        SftpDialogKind.Permissions => "Permissions",
        _ => "File operation",
    };

    /// <summary>True when the open dialog can be applied with the current input.</summary>
    public static bool CanApply(SftpDialogState state, string text) => state.Kind switch
    {
        SftpDialogKind.NewFolder => !string.IsNullOrWhiteSpace(text),
        SftpDialogKind.Rename => state.Target is not null && !string.IsNullOrWhiteSpace(text),
        SftpDialogKind.Delete => state.Target is not null,
        SftpDialogKind.Permissions => state.Target is not null,
        _ => false,
    };

    /// <summary>Runs the remote call that the open dialog stands for.</summary>
    public static Task ApplyAsync(
        SftpDialogState state,
        string text,
        int mode,
        string workingDirectory,
        IRemoteFileSystem fileSystem,
        CancellationToken cancellationToken) => state.Kind switch
    {
        SftpDialogKind.NewFolder => fileSystem.CreateDirectoryAsync(
            SftpPath.Combine(workingDirectory, text.Trim()), cancellationToken),
        SftpDialogKind.Rename when state.Target is { } target => fileSystem.RenameAsync(
            target.FullPath, SftpPath.Combine(workingDirectory, text.Trim()), cancellationToken),
        SftpDialogKind.Delete when state.Target is { } target => fileSystem.DeleteAsync(
            target.FullPath, target.IsDirectory, cancellationToken),
        SftpDialogKind.Permissions when state.Target is { } target => fileSystem.SetPermissionsAsync(
            target.FullPath, mode, cancellationToken),
        _ => Task.CompletedTask,
    };

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
        Name = entry.Name,
        RemotePath = entry.FullPath,
        LocalPath = Path.Combine(localFolder, entry.Name),
        TotalBytes = entry.Size,
    };
}
